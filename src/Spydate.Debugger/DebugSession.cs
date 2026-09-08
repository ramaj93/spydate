using System.Collections.Concurrent;
using System.Runtime.InteropServices;


namespace Spydate.Debugger;

/// <summary>What the debuggee is doing.</summary>
public enum DebugState
{
    NotStarted,
    Running,
    Stopped,
    Exited,
}

/// <summary>Something the debuggee did, in the order it did it.</summary>
public sealed record DebugEvent(string Kind, string Text)
{
    /// <summary>Where it stopped, when it stopped.</summary>
    public ulong? Address { get; init; }
}

/// <summary>
/// A breakpoint, and the byte it is standing in for.
///
/// <paramref name="Address"/> is the <em>static</em> address — the one in the listing. It is
/// translated to where the module actually landed each time it is planted, rather than once when it
/// is set: a breakpoint is usually set before anything is running, when there is no load address to
/// translate against, and on a module that may be loaded and unloaded more than once in a run.
/// </summary>
public sealed record Breakpoint(ulong Address, byte Original)
{
    public bool Planted { get; init; }
}

/// <summary>A module the debuggee has loaded, and where it landed.</summary>
public sealed record LoadedModule(string Path, ulong Base)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// A process running under a debug loop.
///
/// Everything that touches the debuggee happens on one thread. That is not tidiness: Windows ties a
/// debugging session to the thread that started it, and <c>WaitForDebugEvent</c> called from anywhere
/// else simply does not see the events. So the loop owns the process and callers post work to it.
///
/// Running the program is the whole point and also the thing to be careful about: everything else in
/// Spydate reads a file, and this executes it. It is never started implicitly — opening a binary,
/// analysing it and patching it all leave it inert, and only an explicit start runs anything.
/// </summary>
public sealed class DebugSession : IDisposable
{
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly SemaphoreSlim _resume = new(0, 1);
    private readonly Dictionary<ulong, Breakpoint> _breakpoints = new();
    private readonly Dictionary<ulong, LoadedModule> _modules = new();
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// The file name of the module whose addresses the listing is about, or null for the process's
    /// own executable. It is what makes debugging a DLL possible: the thing being run and the thing
    /// being read are then two different files, and only the second one's load address translates
    /// the addresses on screen.
    /// </summary>
    private string? _target;

    private Thread? _loop;
    private IntPtr _process;
    private uint _processId;
    private uint _threadId;

    /// <summary>Set while stepping over a breakpoint, so its byte goes back afterwards.</summary>
    private ulong? _reArm;

    /// <summary>A breakpoint that exists to get somewhere once: step-over, or run-to-cursor.</summary>
    private ulong? _temporary;
    private byte _temporaryOriginal;

    private bool _sawInitialBreak;

    /// <summary>Raised on the debug loop's thread. Consumers marshal for themselves.</summary>
    public event EventHandler<DebugEvent>? Reported;

    public DebugState State { get; private set; } = DebugState.NotStarted;

    /// <summary>Where the loaded image actually is, which is not where the file says on a rebased load.</summary>
    public ulong LoadedBase { get; private set; }

    /// <summary>What the file says its base is, taken from the caller so this need not parse the PE.</summary>
    public ulong ImageBase { get; private set; }

    /// <summary>Every module loaded right now, in load order.</summary>
    public IReadOnlyList<LoadedModule> Modules
    {
        get
        {
            lock (_modules)
            {
                return _modules.Values.ToList();
            }
        }
    }

    /// <summary>
    /// Whether the module the listing is about has been loaded yet. False for a DLL until its host
    /// gets round to loading it, which is when its breakpoints can first go in.
    /// </summary>
    public bool TargetLoaded => LoadedBase != 0;

    /// <summary>Where execution is, when it is stopped.</summary>
    public ulong CurrentAddress { get; private set; }

    /// <summary>How big the image is, so the translation knows where it stops applying.</summary>
    public uint ImageSize { get; private set; }

    /// <summary>
    /// A static address as it will be at run time. The two differ by the load bias whenever the image
    /// is relocated, which for anything built this decade is every run — a breakpoint set on the
    /// address in the listing lands nowhere without this.
    /// </summary>
    public ulong ToRuntime(ulong staticVa)
        => Translatable && staticVa >= ImageBase && staticVa < ImageBase + ImageSize
            ? staticVa - ImageBase + LoadedBase
            : staticVa;

    /// <summary>
    /// The inverse, for reporting a stop at an address the listing will recognise.
    ///
    /// Only inside the image. Execution stops in ntdll and in every other module far more often than
    /// it stops in the binary being read — at the loader break, in a system call, in an exception
    /// handler — and applying the image's bias to one of those addresses does not fail, it produces
    /// a number that looks like an address in this program and belongs to nothing at all.
    /// </summary>
    public ulong ToStatic(ulong runtimeVa)
        => Translatable && runtimeVa >= LoadedBase && runtimeVa < LoadedBase + ImageSize
            ? runtimeVa - LoadedBase + ImageBase
            : runtimeVa;

    /// <summary>Whether a stop is somewhere the listing can show.</summary>
    public bool InsideImage(ulong runtimeVa)
        => Translatable && runtimeVa >= LoadedBase && runtimeVa < LoadedBase + ImageSize;

    private bool Translatable => LoadedBase != 0 && ImageBase != 0 && ImageSize != 0;

    public IReadOnlyList<Breakpoint> Breakpoints
    {
        get
        {
            lock (_breakpoints)
            {
                return _breakpoints.Values.ToList();
            }
        }
    }

    /// <summary>
    /// Starts <paramref name="path"/> under the debugger. Nothing runs until this is called, and it
    /// is only ever called because somebody asked for it.
    /// </summary>
    /// <param name="path">What to run. For a DLL this is the host that loads it, not the DLL.</param>
    /// <param name="imageBase">The preferred base of the module being read, from its own headers.</param>
    /// <param name="imageSize">Its size, so the translation knows where it stops applying.</param>
    /// <param name="module">
    /// The file name of the module the listing is about. Null means the process's own executable,
    /// which is the case whenever the thing being run is also the thing being read.
    /// </param>
    public void Start(
        string path,
        ulong imageBase,
        uint imageSize,
        string? arguments = null,
        string? workingDirectory = null,
        string? module = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (State != DebugState.NotStarted)
        {
            throw new InvalidOperationException("this session has already been started");
        }

        ImageBase = imageBase;
        ImageSize = imageSize;
        _target = module is { Length: > 0 } ? System.IO.Path.GetFileName(module) : null;
        var ready = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        _loop = new Thread(() => Loop(path, arguments, workingDirectory, ready))
        {
            IsBackground = true,
            Name = "spydate-debug",
        };

        _loop.Start();

        if (ready.Task.GetAwaiter().GetResult() is { } failure)
        {
            throw failure;
        }
    }

    /// <summary>Lets it run on. Does nothing unless it is stopped.</summary>
    public void Continue() => Post(() => { });

    /// <summary>One instruction, then stop again. Into a call, not over it.</summary>
    public void StepInstruction() => Post(() => Trap());

    /// <summary>
    /// One instruction, but over a call rather than into it.
    ///
    /// A call is stepped by breaking on the instruction after it and letting the whole call run,
    /// which is the only way that works: single-stepping through a call means single-stepping every
    /// instruction it and everything it calls executes, which for anything touching the CRT is
    /// millions of round trips through the debug loop.
    ///
    /// Everything else is an ordinary step. A conditional jump stepped "over" still has to go where
    /// it goes — there is no instruction after it to break on in any useful sense.
    /// </summary>
    public void StepOver() => Post(() =>
    {
        if (Decode() is not { } instruction || !IsCall(instruction))
        {
            Trap();
            return;
        }

        // Armed only if it is really in place; otherwise a one-shot nobody will ever hit stays set.
        if (Plant(instruction.NextIP, temporary: true))
        {
            _temporary = instruction.NextIP;
        }
        else
        {
            Trap();
        }
    });

    /// <summary>
    /// Runs until execution reaches a static address, then stops. The breakpoint is not remembered:
    /// it is a way of getting somewhere, not a place to keep stopping at.
    /// </summary>
    public void RunTo(ulong staticVa) => Post(() =>
    {
        ulong runtime = ToRuntime(staticVa);
        if (Plant(runtime, temporary: true))
        {
            _temporary = runtime;
        }
    });

    /// <summary>The instruction at RIP, or null when it cannot be read or decoded.</summary>
    private Iced.Intel.Instruction? Decode()
    {
        using var context = new ThreadContext();
        var thread = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, _threadId);
        if (thread == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!context.Read(thread))
            {
                return null;
            }

            byte[] code = ReadMemory(context.Rip, 16);
            if (code.Length == 0)
            {
                return null;
            }

            var decoder = Iced.Intel.Decoder.Create(64, new Iced.Intel.ByteArrayCodeReader(code), context.Rip);
            var instruction = decoder.Decode();
            return instruction.IsInvalid ? null : instruction;
        }
        finally
        {
            Native.CloseHandle(thread);
        }
    }

    private static bool IsCall(Iced.Intel.Instruction instruction)
        => instruction.FlowControl is Iced.Intel.FlowControl.Call or Iced.Intel.FlowControl.IndirectCall;

    /// <summary>Asks the processor to fault after the next instruction.</summary>
    private void Trap()
    {
        using var context = new ThreadContext();
        var thread = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, _threadId);
        if (thread == IntPtr.Zero)
        {
            return;
        }

        if (context.Read(thread))
        {
            context.SetTrapFlag(true);
            context.Write(thread);
        }

        Native.CloseHandle(thread);
    }

    /// <summary>
    /// Adds a breakpoint at a <em>static</em> address — the one in the listing. It is translated for
    /// the run and planted when the process is there to plant it in.
    /// </summary>
    public bool AddBreakpoint(ulong staticVa)
    {
        lock (_breakpoints)
        {
            if (_breakpoints.ContainsKey(staticVa))
            {
                return false;
            }

            _breakpoints[staticVa] = new Breakpoint(staticVa, 0);
        }

        // Only if the module is actually here. On a DLL that its host has not loaded yet there is
        // no address to put it at; it goes in when the module arrives.
        if (State == DebugState.Stopped && TargetLoaded)
        {
            Post(() => PlantStatic(staticVa));
        }

        return true;
    }

    public bool RemoveBreakpoint(ulong staticVa)
    {
        Breakpoint? existing;
        lock (_breakpoints)
        {
            if (!_breakpoints.TryGetValue(staticVa, out existing))
            {
                return false;
            }

            _breakpoints.Remove(staticVa);
        }

        if (existing.Planted && State == DebugState.Stopped)
        {
            ulong runtime = ToRuntime(staticVa);
            Post(() => WriteByte(runtime, existing.Original));
        }

        return true;
    }

    /// <summary>Reads the debuggee's memory. Empty when it cannot be read, which is not exceptional.</summary>
    public byte[] ReadMemory(ulong runtimeAddress, int length)
    {
        if (_process == IntPtr.Zero || length <= 0)
        {
            return [];
        }

        byte[] buffer = new byte[length];
        unsafe
        {
            fixed (byte* p = buffer)
            {
                if (!Native.ReadProcessMemory(_process, runtimeAddress, p, (nuint)length, out nuint read))
                {
                    return [];
                }

                return read == (nuint)length ? buffer : buffer[..(int)read];
            }
        }
    }

    /// <summary>The registers as they stand. Null unless it is stopped, because otherwise they are a guess.</summary>
    public IReadOnlyList<(string Name, ulong Value)>? Registers()
    {
        if (State != DebugState.Stopped)
        {
            return null;
        }

        using var context = new ThreadContext();
        var thread = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, _threadId);
        if (thread == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!context.Read(thread))
            {
                return null;
            }

            var all = context.General().ToList();
            all.Add(("rip", context.Rip));
            all.Add(("rflags", context.EFlags));
            return all;
        }
        finally
        {
            Native.CloseHandle(thread);
        }
    }

    /// <summary>
    /// The top of the stack, as address and value pairs. At a breakpoint this is usually the first
    /// thing worth looking at: the return address, then whatever the frame is holding.
    /// </summary>
    public IReadOnlyList<(ulong Address, ulong Value)> Stack(int count = 16)
    {
        if (State != DebugState.Stopped)
        {
            return [];
        }

        var registers = Registers();
        ulong rsp = registers?.FirstOrDefault(r => r.Name == "rsp").Value ?? 0;
        if (rsp == 0)
        {
            return [];
        }

        byte[] bytes = ReadMemory(rsp, count * 8);
        var rows = new List<(ulong, ulong)>();
        for (int i = 0; i + 8 <= bytes.Length; i += 8)
        {
            rows.Add((rsp + (ulong)i, BitConverter.ToUInt64(bytes, i)));
        }

        return rows;
    }

    /// <summary>Ends the debuggee. It is a debugged process, so it does not outlive the session.</summary>
    public void Stop()
    {
        _stopping.Cancel();
        if (_process != IntPtr.Zero)
        {
            Native.TerminateProcess(_process, 0);
        }

        // Let a stopped loop notice it is finished.
        if (_resume.CurrentCount == 0)
        {
            try
            {
                _resume.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    private void Post(Action work)
    {
        if (State != DebugState.Stopped)
        {
            return;
        }

        _commands.Enqueue(work);
        State = DebugState.Running;
        try
        {
            _resume.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private void Report(string kind, string text, ulong? address = null)
    {
        // Stopped is true before anybody is told, not after.
        //
        // Whoever hears this acts on it immediately — the assistant hears "stopped" and asks to
        // continue within microseconds — and Post refuses work unless the state already says
        // stopped. Telling first and setting after left a window in which that command was dropped
        // on the floor: the loop then waited for a command that would never arrive, the process
        // never resumed, and the panel showed a session where only Stop could be pressed. A person
        // clicking a button rarely lost that race. Something reacting in code lost it nearly always.
        if (kind == "stopped")
        {
            State = DebugState.Stopped;
        }

        Reported?.Invoke(this, new DebugEvent(kind, text) { Address = address });
    }

    private void Loop(string path, string? arguments, string? workingDirectory, TaskCompletionSource<Exception?> ready)
    {
        var startup = new Native.STARTUPINFO { cb = (uint)Marshal.SizeOf<Native.STARTUPINFO>() };

        // Quoted, and writable: CreateProcessW may modify the buffer it is given.
        char[] command = ($"\"{path}\""
                          + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments)
                          + '\0').ToCharArray();

        Native.PROCESS_INFORMATION info;
        bool started;
        unsafe
        {
            fixed (char* line = command)
            {
                started = Native.CreateProcess(
                    null, line, IntPtr.Zero, IntPtr.Zero, false,
                    Native.DEBUG_ONLY_THIS_PROCESS | Native.CREATE_NEW_CONSOLE,
                    IntPtr.Zero, workingDirectory, ref startup, out info);
            }
        }

        if (!started)
        {
            ready.SetResult(new InvalidOperationException(
                $"could not start {path}: {Marshal.GetLastPInvokeErrorMessage()}"));
            return;
        }

        _process = info.hProcess;
        _processId = info.dwProcessId;
        State = DebugState.Running;
        ready.SetResult(null);

        try
        {
            Pump();
        }
        finally
        {
            State = DebugState.Exited;
            Native.CloseHandle(info.hThread);
            Native.CloseHandle(info.hProcess);
            _process = IntPtr.Zero;
        }
    }

    private void Pump()
    {
        while (!_stopping.IsCancellationRequested)
        {
            if (!Native.WaitForDebugEvent(out var e, 200))
            {
                continue;
            }

            _threadId = e.dwThreadId;
            uint status = Native.DBG_CONTINUE;
            bool stop = false;

            switch (e.dwDebugEventCode)
            {
                case Native.CREATE_PROCESS_DEBUG_EVENT:
                    OnModuleLoaded(e.CreateProcessFile, e.CreateProcessImageBase, main: true);
                    break;

                case Native.LOAD_DLL_DEBUG_EVENT:
                    OnModuleLoaded(e.LoadDllFile, e.LoadDllBase, main: false);
                    break;

                case Native.UNLOAD_DLL_DEBUG_EVENT:
                    OnModuleUnloaded(e.UnloadDllBase);
                    break;

                case Native.EXIT_PROCESS_DEBUG_EVENT:
                    // In hex as well as decimal, because a crash exit code is an NTSTATUS and only
                    // one of the two forms is recognisable: 0xC0000005 is an access violation at a
                    // glance, and 3221225477 is not.
                    Report("exited", e.ExitCode == 0
                        ? "exited normally"
                        : $"exited with code {e.ExitCode} (0x{e.ExitCode:X8})");
                    Native.ContinueDebugEvent(e.dwProcessId, e.dwThreadId, Native.DBG_CONTINUE);
                    return;

                case Native.OUTPUT_DEBUG_STRING_EVENT:
                    break;

                case Native.EXCEPTION_DEBUG_EVENT:
                    (stop, status) = OnException(e);
                    break;
            }

            if (stop)
            {
                State = DebugState.Stopped;
                _resume.Wait();

                if (_stopping.IsCancellationRequested)
                {
                    return;
                }

                while (_commands.TryDequeue(out var work))
                {
                    work();
                }

                State = DebugState.Running;
            }

            Native.ContinueDebugEvent(e.dwProcessId, e.dwThreadId, status);
        }
    }

    /// <summary>
    /// Records a module and, when it is the one the listing is about, fixes the load bias and puts
    /// the waiting breakpoints in.
    ///
    /// The handle is closed here whatever happens. Windows hands the debugger a file handle with
    /// every one of these events and it is the debugger's to close — a process that loads two
    /// hundred modules leaks two hundred handles otherwise.
    /// </summary>
    private void OnModuleLoaded(IntPtr file, ulong loadBase, bool main)
    {
        string? path = Native.PathOf(file);
        Native.CloseHandle(file);

        if (loadBase == 0)
        {
            return;
        }

        var module = new LoadedModule(path ?? string.Empty, loadBase);
        lock (_modules)
        {
            _modules[loadBase] = module;
        }

        string name = module.Name.Length > 0 ? module.Name : "an unnamed module";
        string where = $"at 0x{loadBase:X}"
                       + (ImageBase != 0 && loadBase != ImageBase ? $" (file says 0x{ImageBase:X})" : string.Empty);

        if (main)
        {
            Report("started", $"running {name} {where}");
        }

        // The main image when nothing else was named, or whichever module was: under a host, the
        // process's own executable is not the thing being read.
        bool wanted = _target is null ? main : string.Equals(module.Name, _target, StringComparison.OrdinalIgnoreCase);
        if (!wanted)
        {
            return;
        }

        LoadedBase = loadBase;

        // Its breakpoints could not go in before this moment, so they go in now rather than waiting
        // for a stop that may never come. Nothing to do for the main image — the loader break is
        // still ahead, and planting is what that is for.
        if (main)
        {
            return;
        }

        PlantAll();

        int waiting;
        lock (_breakpoints)
        {
            waiting = _breakpoints.Count;
        }

        Report("module", $"{name} loaded {where}"
                         + (waiting > 0 ? $"; {waiting} breakpoint{(waiting == 1 ? string.Empty : "s")} armed" : string.Empty));
    }

    /// <summary>
    /// Drops a module, and if it was the one being read, stops pretending its addresses mean
    /// anything. A DLL can be freed and loaded again at a different address in one run.
    /// </summary>
    private void OnModuleUnloaded(ulong loadBase)
    {
        LoadedModule? gone;
        lock (_modules)
        {
            _modules.Remove(loadBase, out gone);
        }

        if (loadBase != LoadedBase || LoadedBase == 0)
        {
            return;
        }

        LoadedBase = 0;
        Unplant();
        Report("module", $"{(gone?.Name is { Length: > 0 } name ? name : "the target module")} was unloaded; "
                         + "its breakpoints go back in if it is loaded again");
    }

    private (bool Stop, uint Status) OnException(Native.DEBUG_EVENT e)
    {
        switch (e.ExceptionCode)
        {
            case Native.EXCEPTION_BREAKPOINT:
                // The loader breaks once, before the program's own code runs. It is not one of ours,
                // and it is the moment the image is finally mapped — so breakpoints go in here.
                if (!_sawInitialBreak)
                {
                    _sawInitialBreak = true;
                    PlantAll();
                    CurrentAddress = e.ExceptionAddress;
                    Report("stopped", "stopped at the loader break, before the program's own code", ToStatic(e.ExceptionAddress));
                    return (true, Native.DBG_CONTINUE);
                }

                return (HitBreakpoint(e.ExceptionAddress), Native.DBG_CONTINUE);

            case Native.EXCEPTION_SINGLE_STEP:
                // Either a step the user asked for, or the one taken to get off a breakpoint.
                if (_reArm is { } address)
                {
                    _reArm = null;
                    Plant(address);
                    return (false, Native.DBG_CONTINUE);
                }

                CurrentAddress = e.ExceptionAddress;
                Report("stopped", $"stepped to 0x{ToStatic(e.ExceptionAddress):X}", ToStatic(e.ExceptionAddress));
                return (true, Native.DBG_CONTINUE);

            default:
                // Anything else belongs to the program. It is offered its own handlers first, so a
                // first chance is only news; an unhandled one is where the program dies, and the
                // loop stops there so it can be looked at.
                //
                // That stop is reported as a stop. It used to be reported as "exception" whether or
                // not it ended the program, and nothing listening treated that as having stopped —
                // so on a crash the loop sat waiting while the panel went on saying "running", with
                // Continue greyed out and no way to do anything but stop. An access violation is
                // the single most interesting place a debugger ever pauses, and it was the one
                // place the panel could not tell you it had.
                bool fatal = !e.FirstChance;
                if (fatal)
                {
                    CurrentAddress = e.ExceptionAddress;
                }

                Report(fatal ? "stopped" : "exception",
                    $"0x{e.ExceptionCode:X8} at 0x{ToStatic(e.ExceptionAddress):X}"
                    + (fatal ? " (unhandled — the program would die here)" : " (first chance)"),
                    Reportable(e.ExceptionAddress));

                return (fatal, Native.DBG_EXCEPTION_NOT_HANDLED);
        }
    }

    /// <summary>An int3 we planted: put the byte back, wind RIP back onto it, and stop.</summary>
    private bool HitBreakpoint(ulong address)
    {
        // A one-shot first: step-over and run-to-cursor put it there to get here, and it goes away
        // whether or not a real breakpoint happens to be at the same address.
        if (_temporary == address)
        {
            _temporary = null;
            WriteByte(address, _temporaryOriginal);
            Rewind(address, thenStep: false);

            // Not re-armed — that is the whole difference from a breakpoint someone set.
            CurrentAddress = address;
            Report("stopped", $"stopped at 0x{ToStatic(address):X}", Reportable(address));
            return true;
        }

        // Looked up by the address in the listing, which is how they are kept: the int3 is at a
        // runtime address, and the same static address is a different runtime one every run.
        ulong staticVa = ToStatic(address);
        Breakpoint? hit;
        lock (_breakpoints)
        {
            _breakpoints.TryGetValue(staticVa, out hit);
        }

        if (hit is null)
        {
            Report("exception", $"a breakpoint instruction at 0x{ToStatic(address):X} that we did not plant", ToStatic(address));
            return true;
        }

        WriteByte(address, hit.Original);
        Rewind(address, thenStep: true);

        // Re-planted after the single step that carries execution off this address; planting it now
        // would break on the instruction we are about to resume.
        _reArm = address;
        CurrentAddress = address;
        Report("stopped", $"breakpoint at 0x{ToStatic(address):X}", Reportable(address));
        return true;
    }

    /// <summary>
    /// Puts RIP back on the instruction the int3 replaced, and asks for a single step off it.
    ///
    /// RIP is one past the int3 that just executed, which is the middle of whatever instruction used
    /// to be there. Resuming from it would run the tail of that instruction as if it were a whole
    /// one — the same failure as a patch that overwrites half of something.
    /// </summary>
    private void Rewind(ulong address, bool thenStep)
    {
        var thread = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, _threadId);
        if (thread == IntPtr.Zero)
        {
            return;
        }

        using var context = new ThreadContext();
        if (context.Read(thread))
        {
            context.Rip = address;
            context.SetTrapFlag(thenStep);
            context.Write(thread);
        }

        Native.CloseHandle(thread);
    }

    /// <summary>
    /// The address to hand a listing, or null when the stop is not in this image at all — in ntdll, in
    /// a system call, in another module. A number there would be read as a place in this program.
    /// </summary>
    private ulong? Reportable(ulong runtimeVa) => InsideImage(runtimeVa) ? ToStatic(runtimeVa) : null;

    /// <summary>
    /// Puts in every breakpoint whose module is loaded. Called at the loader break and again each
    /// time the module the listing is about is loaded, which for a DLL is the only moment its
    /// addresses mean anything — and can happen more than once in a run.
    /// </summary>
    private void PlantAll()
    {
        if (!TargetLoaded)
        {
            return;
        }

        foreach (var breakpoint in Breakpoints)
        {
            PlantStatic(breakpoint.Address);
        }
    }

    /// <summary>Plants the breakpoint held for a static address, at wherever that is right now.</summary>
    private bool PlantStatic(ulong staticVa) => Plant(ToRuntime(staticVa));

    /// <summary>
    /// Forgets where every breakpoint was, without forgetting the breakpoints.
    ///
    /// Used when the module goes away. The bytes they saved belong to a mapping that no longer
    /// exists, and writing one back later would put a byte from the last load into whatever occupies
    /// that address now.
    /// </summary>
    private void Unplant()
    {
        lock (_breakpoints)
        {
            foreach (ulong address in _breakpoints.Keys.ToList())
            {
                _breakpoints[address] = _breakpoints[address] with { Original = 0, Planted = false };
            }
        }
    }

    /// <summary>
    /// Puts an int3 at an address and remembers the byte it replaced. A temporary one is remembered
    /// separately, because it is removed on the first hit rather than kept and re-armed.
    /// </summary>
    private bool Plant(ulong address, bool temporary = false)
    {
        byte[] existing = ReadMemory(address, 1);
        if (existing.Length != 1)
        {
            Report("problem", $"could not read 0x{ToStatic(address):X} to put a breakpoint there");
            return false;
        }

        if (existing[0] == 0xCC)
        {
            // Already an int3. Either ours from an earlier plant, in which case the byte it replaced
            // is recorded and must not be overwritten with 0xCC; or the program's own, in which case
            // 0xCC is genuinely what belongs here and is what has to go back when this is hit or
            // removed. Recording nothing left Original at zero, and restoring it wrote a zero byte
            // over the program's instruction — a debugger corrupting the thing it is watching.
            if (!temporary)
            {
                ulong at = ToStatic(address);
                lock (_breakpoints)
                {
                    if (_breakpoints.TryGetValue(at, out var already) && !already.Planted)
                    {
                        _breakpoints[at] = already with { Original = 0xCC, Planted = true };
                    }
                }
            }

            return true;
        }

        if (temporary)
        {
            _temporaryOriginal = existing[0];
        }
        else
        {
            // Recorded against the address in the listing, which is the key they are kept under;
            // what was read is the byte at wherever that address is in this particular run.
            ulong staticVa = ToStatic(address);
            lock (_breakpoints)
            {
                if (_breakpoints.TryGetValue(staticVa, out var breakpoint))
                {
                    _breakpoints[staticVa] = breakpoint with { Original = existing[0], Planted = true };
                }
            }
        }

        WriteByte(address, 0xCC);
        return true;
    }

    private void WriteByte(ulong address, byte value)
    {
        unsafe
        {
            byte b = value;
            if (Native.WriteProcessMemory(_process, address, &b, 1, out _))
            {
                Native.FlushInstructionCache(_process, address, 1);
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _loop?.Join(TimeSpan.FromSeconds(2));
        _stopping.Dispose();
        _resume.Dispose();
    }
}
