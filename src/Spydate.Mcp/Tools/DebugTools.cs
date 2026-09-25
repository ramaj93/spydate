using System.Globalization;
using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Spydate.Mcp.Rendering;
using Spydate.Mcp.Session;

namespace Spydate.Mcp.Tools;

/// <summary>
/// Running the binary and watching it, for an agent.
///
/// This is the one part of the surface that leaves the property everything else rests on. Every
/// other tool reads a file or edits the one project beside it; these execute code that the analyst
/// opened precisely because they did not trust it. So they are off unless the host says otherwise
/// twice over — <see cref="McpOptions.AllowDebug"/> has to be set, and the host has to have supplied
/// a debugger to drive. The window supplies one because a person answered "run this binary?" and is
/// watching it; the stdio server supplies none.
///
/// Four tools rather than a dozen. Every schema here is sent on every turn of every conversation,
/// including the ones that never debug anything, so an action argument is cheaper than eight verbs
/// and reads no worse.
/// </summary>
[McpServerToolType]
public sealed class DebugTools
{
    private const int MaxRead = 4096;

    /// <summary>How long to let an action land before reporting where it got.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long "wait" waits. Longer, because it exists for the case the settle cannot cover: real
    /// programs take their time reaching the code being watched, and a DLL under a host is not
    /// loaded until the host gets round to it — which was over a minute in the run this came from.
    /// Short enough that it returns inside an MCP client's own call timeout, and repeatable.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly SessionStore _store;
    private readonly McpOptions _options;

    public DebugTools(SessionStore store, McpOptions options)
    {
        _store = store;
        _options = options;
    }

    [McpServerTool(Name = "debug_run")]
    [Description("Move a debugged process: start, stop, continue, pause, step, step_over, step_out, run_to, wait. \"wait\" waits for the next stop without moving it - a DLL runs only once its host loads it. Reports where it got.")]
    public string Run(
        [Description("One of the actions above.")] string action,
        [Description("Address, sub_XXXX or a name. Only for run_to.")] string? target = null,
        [Description("Thread to step.")] uint? thread = null)
    {
        if (Refusal() is { } refused)
        {
            return refused;
        }

        if (_store.ManagedDebug is { } managed)
        {
            return ManagedDebugging.Run(managed, action, thread);
        }

        var debug = _store.Debug!;
        string what = action.Trim().ToLowerInvariant().Replace('-', '_');

        // Asked before acting, because the answer decides whether acting means anything. Moving a
        // process that is not stopped is what the loop was: the request did nothing, the state was
        // reported as running anyway, and the only thing left to try was the same request again.
        if (what is "continue" or "step" or "step_over" or "run_to")
        {
            var now = debug.Snapshot();
            if (now.State != "stopped")
            {
                return now.State switch
                {
                    "exited" => $"it has exited — {now.Status} Nothing is left to continue; "
                                + "use start to run it again." + Recent(now),
                    "running" => "it is running and has not stopped. Continuing does nothing from here — "
                                 + "use wait to be there when it does stop, pause to stop it where it is, "
                                 + "or set a breakpoint somewhere it will reach." + Recent(now),
                    _ => "nothing is running. Use start." + Recent(now),
                };
            }
        }

        if (thread is { } picked && Pick(debug, picked) is { } wrong)
        {
            return wrong;
        }

        switch (what)
        {
            case "start":
                if (debug.Start() is { } problem)
                {
                    return $"did not start: {problem}";
                }

                break;

            case "stop":
                debug.Stop();
                return "stopped, and the process is gone";

            case "continue":
                debug.Continue();
                break;

            case "step":
                debug.StepInstruction();
                break;

            case "step_over":
                debug.StepOver();
                break;

            case "run_to":
                if (Address(target) is not { } to)
                {
                    return "run_to needs an address, sub_XXXX or a name to run to";
                }

                debug.RunTo(to);
                break;

            case "pause":
                // The way back from running that does not end the run. Stop was the only one, and it
                // takes the process with it - every breakpoint reached, every module loaded.
                var going = debug.Snapshot();
                if (going.State != "running")
                {
                    return going.State == "stopped"
                        ? $"it is already stopped — {going.Status}" + Recent(going)
                        : "nothing is running, so there is nothing to pause." + Recent(going);
                }

                debug.Pause();
                break;

            case "wait":
                // Asks the process for nothing at all. It is the only way to be present when a
                // breakpoint is finally reached, rather than having been told ten seconds earlier
                // that it had not been reached yet.
                //
                // The next stop, so a process that is already stopped is not something to wait on:
                // there will be no next one until something lets it run, and returning the state it
                // already had looked exactly like having waited and found nothing changed.
                var idle = debug.Snapshot();
                if (idle.State != "running")
                {
                    return idle.State switch
                    {
                        "stopped" => $"it is already stopped — {idle.Status} Nothing further happens "
                                     + "until you continue or step." + Recent(idle),
                        "exited" => $"it has exited — {idle.Status} There is nothing left to wait for."
                                    + Recent(idle),
                        _ => "nothing is running, so there is nothing to wait for. Use start." + Recent(idle),
                    };
                }

                break;

            default:
                return $"no such action \"{action}\". Use start, stop, continue, pause, step, step_over, run_to or wait.";
        }

        // Waited for rather than reported straight away: the debug loop is on its own thread and the
        // process runs until it hits something, so returning now would describe the state before the
        // thing that was asked for had happened.
        var patience = what == "wait" ? Patience : Settle;
        if (debug.WaitUntilStopped(patience))
        {
            return Describe(debug.Snapshot());
        }

        // What it has been doing goes in even here — especially here. This is the answer that used
        // to say only "still running", which told an agent nothing except to ask again, while the
        // log in front of the analyst was saying the module had loaded and the process had gone.
        var timedOut = debug.Snapshot();

        // A step that has not landed is nearly always this, and the general answer below - about a
        // DLL not loaded yet - would send it looking in exactly the wrong place.
        if (what is "step" or "step_over"
            && timedOut.Threads.FirstOrDefault(t => t.Id == timedOut.SelectedThread) is { Waiting: true } parked)
        {
            return "thread " + parked.Id + " is waiting in the kernel, so its step lands only when that wait "
                   + "ends - the other threads are running so that it can. Use wait to be there when it does, "
                   + "or pause to stop everything where it is." + Recent(timedOut);
        }
        return $"still running after {patience.TotalSeconds:0}s and it has not stopped. It may stop "
               + "later — a breakpoint in a DLL cannot be reached until its host loads the module. "
               + "Use wait to keep waiting, pause to stop it where it is, or set a breakpoint somewhere it will reach."
               + Recent(timedOut);
    }

    /// <summary>What the process has been doing, when there is anything to say.</summary>
    private static string Recent(DebugSnapshot snapshot)
        => snapshot.Recent.Count == 0
            ? string.Empty
            : "\n\nwhat it has done:\n" + string.Join("\n", snapshot.Recent.Select(line => $"  {line}"));

    [McpServerTool(Name = "debug_break")]
    [Description("Set or clear a breakpoint at a listing address (open module) or a .NET Type::Method(+IL_7); in another assembly, by name not address. In another native module, Name.dll+0xRVA, not its listing address. A .NET program debugged natively takes a managed breakpoint at a managed address, a native one elsewhere. Works before its module loads.")]
    public string Break(
        [Description("Address, sub_XXXX, a name, .NET method, or Name.dll+0xRVA.")] string target,
        [Description("True to set it, false to clear it.")] bool on = true)
    {
        if (Refusal() is { } refused)
        {
            return refused;
        }

        // Module+RVA first, and for both engines. It names a place in a module that is not the one
        // being read, so nothing about the open binary can resolve it and neither resolver should
        // try - a .NET assembly running native code through a P/Invoke is exactly when this is asked
        // for, and that is the managed engine's session as much as the native one's.
        if (ModuleTarget(target) is var (module, rva))
        {
            if (_store.Debug is not { } debug)
            {
                return "this host does not support breakpoints in other modules.";
            }

            return debug.SetModuleBreakpoint(module, rva, on)
                   ?? (on
                       ? $"breakpoint at {module}+0x{rva:X}. It goes into the process when {module} loads."
                       : $"cleared the breakpoint at {module}+0x{rva:X}");
        }

        if (_store.ManagedDebug is { } managed)
        {
            return ManagedDebugging.Break(_store.Current!, managed, target, on);
        }

        if (Address(target) is not { } va)
        {
            return $"could not work out an address from \"{target}\"";
        }

        bool now = _store.Debug!.SetBreakpoint(va, on);
        return now ? $"breakpoint at 0x{va:X}" : $"no breakpoint at 0x{va:X}";
    }

    [McpServerTool(Name = "debug_state")]
    [Description("Where a debugged process is: state, where it stopped, threads, registers, flags, stack, modules, breakpoints, and what it has done lately. In native mode it also gives the managed method, IL offset and call stack.")]
    public string State(
        [Description("Thread to show.")] uint? thread = null,
        [Description("Loaded modules whose name contains this (* = all), with bases.")] string? modules = null)
    {
        if (Refusal() is { } refused)
        {
            return refused;
        }

        if (_store.ManagedDebug is { } managed)
        {
            if (thread is { } wanted && managed.SelectThread(wanted) is { } noThread)
            {
                return noThread;
            }

            return ManagedDebugging.Describe(managed.Snapshot());
        }

        var debug = _store.Debug!;
        return thread is { } picked && Pick(debug, picked) is { } wrong ? wrong : Describe(debug.Snapshot(), modules);
    }

    [McpServerTool(Name = "debug_config")]
    [Description("Read or change how the next run starts - the Debug Program settings - without opening the dialog. No arguments reports them; any argument changes that field and leaves the rest. Only while stopped. engine picks the model for a .NET binary: managed (.NET CLR - locals, IL stepping) or native (mixed mode - native and managed breakpoints, managed stack, no locals); a native binary is native only.")]
    public string Config(
        [Description("\"managed\" or \"native\"; a native binary only native.")] string? engine = null,
        [Description("Host for a DLL or .NET assembly.")] string? executable = null,
        [Description("Command-line arguments.")] string? arguments = null,
        [Description("Empty is the host's own folder.")] string? working_directory = null,
        [Description("An option the read reports.")] string? break_at = null)
    {
        if (!_options.AllowDebug)
        {
            return "debugging is off. It runs the binary, so it is not on by default: start the server "
                   + "with --allow-debug, or use the assistant panel in the window.";
        }

        if (_store.DebugSettings is not { } settings)
        {
            return "this host does not let its run configuration be changed here.";
        }

        if (_store.Current is null)
        {
            return SessionTools.NothingOpen;
        }

        bool changing = engine is not null || executable is not null || arguments is not null
                        || working_directory is not null || break_at is not null;
        if (changing && settings.Apply(engine, executable, arguments, working_directory, break_at) is { } problem)
        {
            return problem;
        }

        return Render(settings.Read());
    }

    private static string Render(DebugSettingsSnapshot s)
    {
        var sb = new StringBuilder();

        string engine = s.Engine == "native" && s.TargetIsManaged ? "native (mixed mode)" : s.Engine;
        sb.AppendLine(s.TargetIsManaged
            ? $"engine: {engine}  (.NET target — also takes: {string.Join(", ", s.EngineOptions)})"
            : $"engine: {engine}  (native binary — native only)");
        sb.AppendLine($"executable: {(s.Executable.Length > 0 ? s.Executable : "(the target itself)")}"
                      + (s.ExecutableEditable ? string.Empty : "  (fixed — a native EXE is its own program)"));
        sb.AppendLine($"arguments: {(s.Arguments.Length > 0 ? s.Arguments : "(none)")}");
        sb.AppendLine($"working directory: {(s.WorkingDirectory.Length > 0 ? s.WorkingDirectory : "(the host's own folder)")}");
        sb.AppendLine($"break at: {s.BreakAt}  (options: {string.Join(", ", s.BreakAtOptions)})");
        if (s.Running)
        {
            sb.AppendLine("a run is in progress — these cannot be changed until it stops.");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Makes a thread the one shown and stepped, or says why it cannot. Null when it worked.
    ///
    /// Only at a stop: a running thread has no registers to show and cannot be single-stepped, and
    /// picking one then would read as having done something. An id that is not there gets the list
    /// back, because the likely mistake is a thread that has exited since it was last seen.
    /// </summary>
    private static string? Pick(IDebugControl debug, uint threadId)
    {
        var now = debug.Snapshot();
        if (now.State != "stopped")
        {
            return $"a thread can only be picked while it is stopped, and it is {now.State}.";
        }

        return debug.SelectThread(threadId)
            ? null
            : $"no thread {threadId}. The threads are {string.Join(", ", now.Threads.Select(t => t.Id))}.";
    }

    [McpServerTool(Name = "debug_memory")]
    [Description("Read native process memory at a listing address, up to 4096 bytes. Not for .NET: the address is file IL — use debug_state.")]
    public string Memory(
        [Description("Address, sub_XXXX, or an existing name.")] string target,
        [Description("How many bytes, up to 4096.")] int length = 128)
    {
        if (Refusal() is { } refused)
        {
            return refused;
        }

        if (_store.ManagedDebug is not null)
        {
            // Refused rather than approximated. In an IL-only assembly a listing address names a
            // byte of IL in the file, and the bytes at that address in the process are the JIT's
            // output for something else entirely. What a managed stop holds is in debug_state.
            return "this is a .NET process, where a listing address names IL in the file rather than "
                   + "anything in the running program. debug_state reports what the stopped frame holds.";
        }

        if (Address(target) is not { } va)
        {
            return $"could not work out an address from \"{target}\"";
        }

        int wanted = Math.Clamp(length, 1, MaxRead);
        byte[] bytes = _store.Debug!.ReadMemory(va, wanted);

        if (bytes.Length == 0)
        {
            return $"could not read 0x{va:X} — it is not mapped, or nothing is running";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{bytes.Length} bytes at 0x{va:X}"
                      + (bytes.Length < wanted ? $" (asked for {wanted}; the rest is not mapped)" : string.Empty));
        sb.Append(HexDump.Render(bytes, va));
        return sb.ToString();
    }

    // ------------------------------------------------------------------

    /// <summary>Why this cannot be done, or null. One place, because all four ask the same things.</summary>
    private string? Refusal()
    {
        if (!_options.AllowDebug)
        {
            return "debugging is off. It runs the binary, which is the one thing here that is not "
                   + "reading a file, so it is not on by default: start the server with --allow-debug, "
                   + "or use the assistant panel in the window.";
        }

        if (_store.Debug is null && _store.ManagedDebug is null)
        {
            return "this host has no debugger to drive.";
        }

        if (_store.Current is not { } open)
        {
            return SessionTools.NothingOpen;
        }

        // Said here, once, for every debug tool: an ELF is read and decompiled, never run. Without it a
        // start would reach CreateProcess and come back as a Windows error about an unrecognised file.
        return open.CanDebug
            ? null
            : $"debugging is not available for {(open.Image.Format == Core.Binary.BinaryFormat.Pe ? open.MachineName + " code" : open.Image.Format + " files")}: "
              + "the debugger runs x86 and x64 Windows programs, and this one is read, not run. Everything that reads it still works.";
    }

    /// <summary>
    /// <c>Name.dll+0x1234</c> split into the module and the RVA, or null when the target is not
    /// written that way.
    ///
    /// The file extension is what tells this apart from a .NET <c>Type::Method+IL_7</c> and from an
    /// arithmetic-looking name: the left side has to end in one, so <c>Mingus.dll+0x6F10</c> is a
    /// module and <c>sub_1000+8</c> is not.
    /// </summary>
    private static (string Module, uint Rva)? ModuleTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        int plus = target.LastIndexOf('+');
        if (plus <= 0)
        {
            return null;
        }

        string module = target[..plus].Trim();
        if (!module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            && !module.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string rva = target[(plus + 1)..].Trim();
        if (rva.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            rva = rva[2..];
        }

        return uint.TryParse(rva, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed)
            ? (module, parsed)
            : null;
    }

    private ulong? Address(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || _store.Current is not { } session)
        {
            return null;
        }

        var resolved = Targets.Resolve(session, target);
        return resolved.Found ? resolved.Va : null;
    }

    /// <summary>
    /// What is worth knowing about one thread before choosing it: whether it caused the stop,
    /// whether it is the one shown, and whether it is parked in the kernel — which decides whether
    /// stepping it can land at all.
    /// </summary>
    private static string Tags((uint Id, ulong Start, bool Waiting) thread, DebugSnapshot snapshot)
    {
        var tags = new List<string>(3);
        if (thread.Id == snapshot.CurrentThread)
        {
            tags.Add("stopped it");
        }

        if (thread.Id == snapshot.SelectedThread)
        {
            tags.Add("shown");
        }

        if (thread.Waiting)
        {
            tags.Add("waiting in the kernel");
        }

        return tags.Count == 0 ? string.Empty : " (" + string.Join(", ", tags) + ")";
    }

    private static string Describe(DebugSnapshot snapshot, string? modulesFilter = null)
    {
        var sb = new StringBuilder();
        sb.Append(snapshot.State);
        if (snapshot.Address is { } at)
        {
            sb.Append($" at 0x{at:X}");
        }

        // Only when it is someone else's address: the stop belongs to the thread that caused it, and
        // with another one selected, a bare address above that thread's registers reads as its rip.
        bool elsewhere = snapshot.SelectedThread != 0 && snapshot.SelectedThread != snapshot.CurrentThread;
        if (elsewhere && snapshot.Address is not null)
        {
            sb.Append($" in thread {snapshot.CurrentThread}");
        }

        sb.AppendLine($" — {snapshot.Status}");

        if (snapshot.Threads.Count > 0)
        {
            sb.AppendLine("threads: " + string.Join(", ", snapshot.Threads.Select(t => t.Id + Tags(t, snapshot))));
        }

        // Whose they are, said every time. Registers with no thread named are read as the thread that
        // stopped, which is exactly wrong the moment another one has been picked.
        string whose = snapshot.SelectedThread != 0 ? $" of thread {snapshot.SelectedThread}" : string.Empty;

        if (snapshot.Registers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"registers{whose}:");
            // Four to a line: sixteen registers one per line is sixteen lines of mostly zeroes.
            var names = snapshot.Registers.Select(r => $"{r.Name}={r.Value:X16}").ToList();
            for (int i = 0; i < names.Count; i += 4)
            {
                sb.AppendLine(string.Join("  ", names.Skip(i).Take(4)));
            }

            if (snapshot.Flags.Length > 0)
            {
                sb.AppendLine(snapshot.Flags);
            }
        }

        if (snapshot.Stack.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"stack{whose}:");
            foreach (var (address, value) in snapshot.Stack.Take(8))
            {
                sb.AppendLine($"  {address:X16}  {value:X16}");
            }
        }

        // The managed side of a mixed-mode stop — the native loop driving a .NET target. The status
        // already names the method and IL offset; this is the call stack behind it, so the native
        // registers above read as a place in the C#. Locals are not here: the read-only DAC does not
        // expose them (switch the window's Debug engine to .NET CLR for those).
        if (snapshot.ManagedFrames.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("managed frames (.NET target under the native engine — mixed mode; innermost first):");
            foreach (string frame in snapshot.ManagedFrames.Take(12))
            {
                sb.AppendLine($"  {frame}");
            }
        }

        if (snapshot.Modules.Count > 0)
        {
            sb.AppendLine();
            if (modulesFilter is null)
            {
                // The list is the process's own — every native DLL it has loaded, at the base the
                // loader gave it. It is not dumped by default, because there are dozens; pass a name to
                // list the ones that match, which is how a native module's runtime base is found.
                var target = snapshot.Modules.FirstOrDefault(m => m.IsTarget);
                sb.AppendLine(target.Name is { Length: > 0 }
                    ? $"{snapshot.Modules.Count} modules loaded (pass modules=<name>, or *, to list them with their bases); the one being read is {target.Name} at 0x{target.Base:X}"
                    : $"{snapshot.Modules.Count} modules loaded (pass modules=<name>, or *, to list them); the one being read is not among them yet");
            }
            else
            {
                bool all = modulesFilter is "*" or "";
                var matched = snapshot.Modules
                    .Where(m => all || m.Name.Contains(modulesFilter, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (matched.Count == 0)
                {
                    sb.AppendLine($"no loaded module matches \"{modulesFilter}\", of {snapshot.Modules.Count} loaded");
                }
                else
                {
                    sb.AppendLine(all
                        ? $"{matched.Count} modules loaded:"
                        : $"loaded modules matching \"{modulesFilter}\":");
                    foreach (var (name, @base, isTarget) in matched.Take(200))
                    {
                        sb.AppendLine($"  {(name.Length > 0 ? name : "(unnamed)")}  0x{@base:X}"
                                      + (isTarget ? "  (the one being read)" : string.Empty));
                    }
                }
            }
        }

        if (snapshot.Breakpoints.Count > 0 || snapshot.ModuleBreakpoints.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("breakpoints: " + string.Join(
                ", ",
                snapshot.Breakpoints.Select(b => $"0x{b:X}").Concat(snapshot.ModuleBreakpoints)));
        }

        sb.Append(Recent(snapshot));

        return sb.ToString().TrimEnd();
    }
}
