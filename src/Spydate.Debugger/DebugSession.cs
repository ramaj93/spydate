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

/// <summary>A breakpoint, and the byte it is standing in for.</summary>
public sealed record Breakpoint(ulong Address, byte Original)
{
    public bool Planted { get; init; }
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
    private readonly CancellationTokenSource _stopping = new();

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
    public void Start(string path, ulong imageBase, uint imageSize, string? arguments = null, string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (State != DebugState.NotStarted)
        {
            throw new InvalidOperationException("this session has already been started");
        }

        ImageBase = imageBase;
        ImageSize = imageSize;
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
        ulong runtime = ToRuntime(staticVa);
        lock (_breakpoints)
        {
            if (_breakpoints.ContainsKey(runtime))
            {
                return false;
            }

            _breakpoints[runtime] = new Breakpoint(runtime, 0);
        }

        if (State == DebugState.Stopped)
        {
            Post(() => Plant(runtime));
        }

        return true;
    }

    public bool RemoveBreakpoint(ulong staticVa)
    {
        ulong runtime = ToRuntime(staticVa);
        Breakpoint? existing;
        lock (_breakpoints)
        {
            if (!_breakpoints.TryGetValue(runtime, out existing))
            {
                return false;
            }

            _breakpoints.Remove(runtime);
        }

        if (existing.Planted && State == DebugState.Stopped)
        {
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
            return all;
        }
        finally
        {
            Native.CloseHandle(thread);
        }
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
        => Reported?.Invoke(this, new DebugEvent(kind, text) { Address = address });

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
                    LoadedBase = e.CreateProcessImageBase;
                    Report("started", $"running, image at 0x{LoadedBase:X}"
                                      + (ImageBase != 0 && LoadedBase != ImageBase ? $" (file says 0x{ImageBase:X})" : string.Empty));
                    break;

                case Native.LOAD_DLL_DEBUG_EVENT:
                    Report("module", $"loaded a module at 0x{e.LoadDllBase:X}");
                    break;

                case Native.EXIT_PROCESS_DEBUG_EVENT:
                    Report("exited", $"exited with code {e.ExitCode}");
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
                // Anything else belongs to the program. It is offered its own handlers first, and
                // only reported here so the analyst can see it happening.
                Report("exception", $"0x{e.ExceptionCode:X8} at 0x{ToStatic(e.ExceptionAddress):X}"
                                    + (e.FirstChance ? " (first chance)" : " (unhandled)"),
                    ToStatic(e.ExceptionAddress));
                return (!e.FirstChance, Native.DBG_EXCEPTION_NOT_HANDLED);
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

        Breakpoint? hit;
        lock (_breakpoints)
        {
            _breakpoints.TryGetValue(address, out hit);
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

    private void PlantAll()
    {
        foreach (var breakpoint in Breakpoints)
        {
            Plant(breakpoint.Address);
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
            return true;   // already broken here, by us or by the program itself
        }

        if (temporary)
        {
            _temporaryOriginal = existing[0];
        }
        else
        {
            lock (_breakpoints)
            {
                if (_breakpoints.TryGetValue(address, out var breakpoint))
                {
                    _breakpoints[address] = breakpoint with { Original = existing[0], Planted = true };
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
