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
    [Description("Move a debugged process: start, stop, continue, step, step_over, run_to, wait. \"wait\" watches for the next breakpoint without moving it - a DLL is not reached until its host loads it. Reports where it got.")]
    public string Run(
        [Description("One of the actions above.")] string action,
        [Description("Address, sub_XXXX or a name. Only for run_to.")] string? target = null)
    {
        if (Refusal() is { } refused)
        {
            return refused;
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
                                 + "use wait to be there when it does stop, or set a breakpoint somewhere "
                                 + "it will reach." + Recent(now),
                    _ => "nothing is running. Use start." + Recent(now),
                };
            }
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

            case "wait":
                // Asks the process for nothing at all. It is the only way to be present when a
                // breakpoint is finally reached, rather than having been told ten seconds earlier
                // that it had not been reached yet.
                break;

            default:
                return $"no such action \"{action}\". Use start, stop, continue, step, step_over, run_to or wait.";
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
        return $"still running after {patience.TotalSeconds:0}s and it has not stopped. It may stop "
               + "later — a breakpoint in a DLL cannot be reached until its host loads the module. "
               + "Use wait to keep waiting, or set a breakpoint somewhere it will reach, or stop."
               + Recent(timedOut);
    }

    /// <summary>What the process has been doing, when there is anything to say.</summary>
    private static string Recent(DebugSnapshot snapshot)
        => snapshot.Recent.Count == 0
            ? string.Empty
            : "\n\nwhat it has done:\n" + string.Join("\n", snapshot.Recent.Select(line => $"  {line}"));

    [McpServerTool(Name = "debug_break")]
    [Description("Set or clear a breakpoint at a listing address. Works before anything runs, and on a DLL before its host loads it.")]
    public string Break(
        [Description("Address, sub_XXXX, or an existing name.")] string target,
        [Description("True to set it, false to clear it.")] bool on = true)
    {
        if (Refusal() is { } refused)
        {
            return refused;
        }

        if (Address(target) is not { } va)
        {
            return $"could not work out an address from \"{target}\"";
        }

        bool now = _store.Debug!.SetBreakpoint(va, on);
        return now ? $"breakpoint at 0x{va:X}" : $"no breakpoint at 0x{va:X}";
    }

    [McpServerTool(Name = "debug_state")]
    [Description("Where a debugged process is: state, where it stopped, registers, flags, stack, modules, breakpoints, and what it has done lately.")]
    public string State()
        => Refusal() ?? Describe(_store.Debug!.Snapshot());

    [McpServerTool(Name = "debug_memory")]
    [Description("Read the running process's memory at a listing address. Hex and ASCII, up to 4096 bytes.")]
    public string Memory(
        [Description("Address, sub_XXXX, or an existing name.")] string target,
        [Description("How many bytes, up to 4096.")] int length = 128)
    {
        if (Refusal() is { } refused)
        {
            return refused;
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

        if (_store.Debug is null)
        {
            return "this host has no debugger to drive.";
        }

        return _store.Current is null ? SessionTools.NothingOpen : null;
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

    private static string Describe(DebugSnapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.Append(snapshot.State);
        if (snapshot.Address is { } at)
        {
            sb.Append($" at 0x{at:X}");
        }

        sb.AppendLine($" — {snapshot.Status}");

        if (snapshot.Registers.Count > 0)
        {
            sb.AppendLine();
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
            sb.AppendLine("stack:");
            foreach (var (address, value) in snapshot.Stack.Take(8))
            {
                sb.AppendLine($"  {address:X16}  {value:X16}");
            }
        }

        if (snapshot.Modules.Count > 0)
        {
            sb.AppendLine();
            var target = snapshot.Modules.FirstOrDefault(m => m.IsTarget);
            sb.AppendLine(target.Name is { Length: > 0 }
                ? $"{snapshot.Modules.Count} modules loaded; the one being read is {target.Name} at 0x{target.Base:X}"
                : $"{snapshot.Modules.Count} modules loaded; the one being read is not among them yet");
        }

        if (snapshot.Breakpoints.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("breakpoints: " + string.Join(", ", snapshot.Breakpoints.Select(b => $"0x{b:X}")));
        }

        sb.Append(Recent(snapshot));

        return sb.ToString().TrimEnd();
    }
}
