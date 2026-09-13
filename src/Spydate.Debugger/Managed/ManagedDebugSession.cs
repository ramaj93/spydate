using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Spydate.Debugger.Managed;

/// <summary>
/// A .NET process running under the CLR debugging interface.
///
/// A separate type from <see cref="DebugSession"/>, and not because it would have been tidier. The
/// two cannot coexist on one process: ICorDebug takes the native debug port and means to be the only
/// debugger attached, so which of them runs is decided when a binary is opened. They also answer
/// different questions. The native session stops at a virtual address and reports registers and
/// stack words; this one stops at a method and an IL offset and reports frames and typed values, and
/// neither set of facts is meaningful in the other's world.
///
/// Where the native loop owns a thread because Windows ties <c>WaitForDebugEvent</c> to it, this one
/// owns none: the runtime calls in on a thread of its own whenever something happens, and the
/// discipline that replaces the loop is that every callback continues the debuggee or deliberately
/// does not. See <see cref="ManagedCallback"/>.
/// </summary>
public sealed class ManagedDebugSession : IDisposable, IManagedEvents
{
    private readonly ConcurrentQueue<DebugEvent> _log = new();
    private readonly ManualResetEventSlim _settled = new(false);
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _attached = new(false);
    private readonly Lock _gate = new();

    private readonly ManagedTypes _types = new();
    private GCHandle _self;
    private IntPtr _unregister;
    private IntPtr _launched;
    private ICorDebug? _debug;
    private ICorDebugProcess? _process;
    private ManagedCallback? _callback;
    private string? _startupProblem;
    private uint _pid;
    private bool _holdAtStart;
    private ICorDebugThread? _stopped;

    /// <summary>
    /// The thread being looked at, when somebody picked one other than the thread that stopped. Null
    /// means the stopped thread. Values, the arrow and stepping all follow it, so they cannot describe
    /// one thread while acting on another.
    /// </summary>
    private ICorDebugThread? _selected;

    /// <summary>
    /// Which frame of the thread is being looked at: -1 for the innermost managed one (the method
    /// that stopped), or an index into the call stack chosen by <see cref="SelectFrame"/>. Locals
    /// and the arrow follow it, so a caller's variables are readable, not only the method that
    /// stopped in.
    /// </summary>
    private int _frame = -1;

    /// <summary>Set high while a property getter is being run, so its completion callback is ours to hold.</summary>
    private readonly ManualResetEventSlim _evalDone = new(false);
    private bool _evaluating;
    private bool _evalFailed;

    /// <summary>
    /// Handles on values a getter produced, by the number a path names them with.
    ///
    /// A handle is the only thing here that survives the process running: everything else describes a
    /// frame, and a frame is gone the moment it steps. Without one an evaluated property could be
    /// shown and never opened. They are dropped the moment the process runs on, because a handle held
    /// past its usefulness is an object the collector may not take.
    /// </summary>
    private readonly Dictionary<uint, IntPtr> _handles = new();
    private uint _handled;

    /// <summary>CorDebugHandleType.HANDLE_STRONG — keeps the object alive until it is disposed.</summary>
    private const int HandleStrong = 1;
    private ICorDebugStepper? _stepper;
    private bool _disposed;

    /// <summary>How many events to keep. Enough to explain a stop, not a transcript of the run.</summary>
    private const int Remembered = 60;

    public event EventHandler<DebugEvent>? Reported;

    public DebugState State { get; private set; } = DebugState.NotStarted;

    /// <summary>A line saying what it is doing, in the same shape the panel shows for a native run.</summary>
    public string Status { get; private set; } = "nothing is running";

    /// <summary>The operating system id of the debuggee, once it exists.</summary>
    public uint ProcessId { get; private set; }

    /// <summary>
    /// How many times it has come to a stop, or been looked at from another thread. A view compares
    /// this rather than the location to know it has something new to read: a loop stops at the same
    /// offset every time round, with different values each time.
    /// </summary>
    public int Stops { get; private set; }

    /// <summary>Why the last stop happened.</summary>
    public ManagedStopKind StoppedBy { get; private set; }

    /// <summary>
    /// Whether the debuggee gets a console window of its own.
    ///
    /// On by default: a console program's output is half of what the person watching it came for.
    /// Off for tests, which start a dozen of these — each window takes the foreground as it appears,
    /// so a suite run while somebody is working takes the keyboard away from them over and over. The
    /// program still gets a console either way; only the window is withheld.
    /// </summary>
    public bool ShowConsole { get; init; } = true;

    /// <summary>Modules the runtime has loaded, by name.</summary>
    public IReadOnlyList<string> Modules
    {
        get
        {
            lock (_gate)
            {
                return _modules.ToList();
            }
        }
    }

    private readonly List<string> _modules = new();
    private readonly Dictionary<string, ICorDebugModule> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ManagedBreakpoint> _wanted = new();
    private readonly List<Planted> _planted = new();

    /// <summary>
    /// Breakpoints named by a <c>Type::Method</c> that is not in a module known yet — a framework
    /// method, most often. A managed breakpoint needs a module and a token, and neither is known until
    /// a module defining that type loads, so these are held and resolved against each module as it
    /// arrives, the way a breakpoint by module name waits for its module. Once one resolves it becomes
    /// an ordinary planted breakpoint and leaves this list.
    /// </summary>
    private readonly List<NamedBreakpoint> _byName = new();

    /// <summary>A breakpoint waiting to be matched to a type in some not-yet-known module.</summary>
    private sealed record NamedBreakpoint(string Type, string Method, uint Offset);

    /// <summary>
    /// Patches to write into a module the moment it loads, before its methods are compiled.
    ///
    /// Held rather than applied, the way breakpoints are: the module the address is in may not be
    /// loaded yet — usually it is not — and the only useful time to write a managed patch is the
    /// instant it lands, which is what <see cref="IManagedEvents.ModuleLoaded"/> is for.
    /// </summary>
    private readonly List<ManagedPatch> _patches = new();

    /// <summary>
    /// A breakpoint that is actually in the process, and the runtime's object for it.
    ///
    /// The pointer is kept beside what it is a breakpoint <em>for</em>, which is the whole of what
    /// makes one removable. A bare list of pointers says a breakpoint exists and gives no way to
    /// find the one the caller means.
    /// </summary>
    private sealed record Planted(string Module, uint MethodToken, uint Offset, IntPtr Pointer);

    /// <summary>
    /// Runs a call to the debugging interface on a thread that is allowed to make it.
    ///
    /// ICorDebug's objects cannot cross a COM apartment. Every interface pointer here arrives on one
    /// of the runtime's own threads, which are MTA, and a window's thread is an STA — so the moment
    /// the panel pressed continue, the runtime callable wrapper tried to marshal itself into the STA,
    /// found that ICorDebugProcess supports no marshalling, and threw
    /// <c>InvalidCastException: No such interface supported</c> before the call was even attempted.
    /// The tests never saw it because xunit's threads are MTA, and so are the pool's, which is what
    /// makes this fix as short as it is.
    ///
    /// So every public entry point that touches the interface comes through here. The caller blocks
    /// either way — these are short calls into a debuggee that is already stopped — and a caller
    /// that is already on an MTA thread pays nothing.
    /// </summary>
    private static T Interop<T>(Func<T> work)
        => Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
            ? Task.Run(work).GetAwaiter().GetResult()
            : work();

    private static void Interop(Action work)
        => Interop<bool>(() =>
        {
            work();
            return true;
        });

    /// <summary>Whether a breakpoint is the one being named. Module names compare as file names do.</summary>
    private static bool Same(string module, uint token, uint offset, string wantedModule, uint wantedToken, uint wantedOffset)
        => token == wantedToken
           && offset == wantedOffset
           && string.Equals(module, wantedModule, StringComparison.OrdinalIgnoreCase);

    /// <summary>The last things that happened, oldest first.</summary>
    public IReadOnlyList<string> Recent => _log.Select(e => e.Text).ToList();

    /// <summary>
    /// Launches the program and waits for its runtime to become debuggable. Null on success.
    ///
    /// Launched suspended and resumed only once the debugger is registered. Doing it the other way
    /// round is a race that is usually won and occasionally lost, and losing it means the runtime
    /// starts, publishes nothing, and the debugger waits for an event that has already gone past.
    /// </summary>
    /// <param name="holdAtStart">
    /// Stop as soon as the process exists, before it runs any managed code. This is the only moment
    /// at which a breakpoint is certainly in place before the code it is about runs; setting one
    /// afterwards is a race against a program that is already going.
    /// </param>
    public string? Start(
        string path,
        string? arguments = null,
        string? workingDirectory = null,
        TimeSpan timeout = default,
        bool holdAtStart = false)
        => Interop(() => StartCore(path, arguments, workingDirectory, timeout, holdAtStart));

    private string? StartCore(
        string path,
        string? arguments,
        string? workingDirectory,
        TimeSpan timeout,
        bool holdAtStart)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (State is DebugState.Running or DebugState.Stopped)
        {
            return "it is already running";
        }

        if (!File.Exists(path))
        {
            return $"there is no file at {path}";
        }

        // Asked of the file before anything is launched, because the answer decides which of two
        // entirely different routes gets hold of the runtime, and there is no second chance at it:
        // by the time a program is running, the moment to have attached has gone.
        var target = ManagedTarget.Of(path);

        if (target.Wide != Environment.Is64BitProcess)
        {
            string them = target.Wide ? "64-bit" : "32-bit";
            string us = Environment.Is64BitProcess ? "64-bit" : "32-bit";
            return $"{System.IO.Path.GetFileName(path)} runs as a {them} process and this is a {us} "
                   + $"Spydate. The CLR debugging interface does not cross that line, so a {us} "
                   + $"debugger cannot drive it — it would need the {them} build.";
        }

        string command = arguments is { Length: > 0 } ? $"\"{path}\" {arguments}" : $"\"{path}\"";
        _holdAtStart = holdAtStart;
        _callback = new ManagedCallback(this);
        _self = GCHandle.Alloc(this);
        _startupProblem = null;
        _ready.Reset();
        _attached.Reset();
        _settled.Reset();

        var patience = timeout == default ? TimeSpan.FromSeconds(30) : timeout;

        // Said before the wait rather than after it. Getting hold of a runtime takes a moment and
        // can take the whole timeout, and for all of it the panel read "nothing is running" — which
        // is what it says when nobody has asked for anything, so a start in progress was
        // indistinguishable from a start that had been ignored.
        lock (_gate)
        {
            Status = $"starting {System.IO.Path.GetFileName(path)}";
        }

        if ((target.Framework
                ? StartUnderFramework(path, command, workingDirectory, patience, target.Runtime)
                : StartUnderCore(path, command, workingDirectory, patience)) is { } refused)
        {
            // Kept, so that anything asking the session afterwards is told why rather than being
            // told nothing ever ran. The caller gets the same sentence to show straight away.
            lock (_gate)
            {
                Status = refused;
            }

            return refused;
        }

        lock (_gate)
        {
            // Not overwritten when it is being held: the callback got there first and stopping is
            // what was asked for.
            if (State != DebugState.Stopped)
            {
                State = DebugState.Running;
                Status = "running";
            }
        }

        return null;
    }

    /// <summary>
    /// Launches under CoreCLR, through dbgshim's runtime-startup handshake. Null when it attached.
    /// </summary>
    private string? StartUnderCore(string path, string command, string? workingDirectory, TimeSpan patience)
    {
        if (Launch(command, workingDirectory, out uint pid, out IntPtr resume) is { } refused)
        {
            Release();
            return refused;
        }

        _pid = pid;

        // Reported from the moment there is a process, not from the moment the runtime owns up to
        // one. A launch that never becomes debuggable still started something, and a caller asking
        // what it started was being told nothing.
        ProcessId = pid;

        int hr;
        unsafe
        {
            hr = DbgShim.RegisterForRuntimeStartup(pid, &Started, GCHandle.ToIntPtr(_self), out _unregister);
        }

        if (hr < 0)
        {
            // Killed rather than left. It is suspended and nobody is going to resume it, so letting
            // it go would leave a process that never runs and never exits sitting on the machine.
            _ = Native.TerminateProcess(_launched, 1);
            _ = Native.CloseHandle(resume);
            Release();
            return $"could not register for the runtime starting: 0x{hr:X8}";
        }

        _ = Native.ResumeThread(resume);
        _ = Native.CloseHandle(resume);

        lock (_gate)
        {
            Status = "waiting for its runtime";
        }

        Note($"launched {System.IO.Path.GetFileName(path)} as process {pid}, waiting for its runtime");

        if (!_ready.Wait(patience))
        {
            // Asked of the process rather than guessed at. The old sentence listed the two things
            // this could mean and left the reader to work out which, when the debuggee is right
            // there to be asked: a program that exited has an exit code, and one still running
            // simply never published a runtime. Naming what was launched matters too — under a host
            // it is not the file the analyst opened, and that is frequently the whole mistake.
            string what = System.IO.Path.GetFileName(path);
            string outcome = Ended() is { } code
                ? $"{what} exited with code 0x{code:X8} before its runtime started, so there was nothing to attach to. "
                  + "A program that refuses its arguments, or cannot find its runtime, ends this way."
                : $"{what} is still running but never published a debuggable runtime within "
                  + $"{patience.TotalSeconds:0}s. A program that is not .NET looks like this. It has been stopped.";

            // Killed, not abandoned. Something was asked to be debugged and cannot be, and leaving
            // it running is leaving a process nobody asked to simply run — started by a debugger
            // that has given up on it and will never report anything it does.
            if (Ended() is null)
            {
                _ = Native.TerminateProcess(_launched, 1);
            }

            Release();
            return outcome;
        }

        if (_startupProblem is { } problem)
        {
            Release();
            return problem;
        }

        // Waited for separately, because the interface being ready is not the same as there being a
        // process to drive. CreateProcess is the runtime's first callback and the only thing that
        // hands over an ICorDebugProcess — until it arrives there is nothing to continue, nothing
        // to terminate, and a caller told "running" would be holding a session that cannot act.
        if (!_attached.Wait(patience))
        {
            Release();
            return "its runtime started but never reported the process, so there is nothing to drive";
        }

        return null;
    }

    /// <summary>
    /// Launches under .NET Framework, through the metahost. Null when it attached.
    ///
    /// Shorter than the CoreCLR route and not because it does less. There is no handshake to wait
    /// for: the runtime is already installed and already known, so its debugging interface can be
    /// had before the process exists, and the interface then does the launching itself. Holding at
    /// the start comes free with that — the first callback arrives with the program stopped before
    /// its first managed instruction, which is the same place the other route reaches by creating
    /// the process suspended.
    ///
    /// <c>ICorDebug::CreateProcess</c> is the call CoreCLR does not implement.
    /// </summary>
    private string? StartUnderFramework(
        string path,
        string command,
        string? workingDirectory,
        TimeSpan patience,
        string runtime)
    {
        if (MetaHost.Debugger(runtime, out string? refused) is not { } debug)
        {
            Release();
            return refused ?? "the .NET Framework debugging interface could not be reached";
        }

        _debug = debug;

        int hr = debug.Initialize();
        if (hr < 0)
        {
            Release();
            return $"ICorDebug::Initialize failed: 0x{hr:X8}";
        }

        // Set before anything is launched, for the same reason as on the other route: the first
        // callback arrives during the call below, and one arriving with no handler is a debuggee
        // stopped with nobody to let it go.
        hr = debug.SetManagedHandler(_callback!);
        if (hr < 0)
        {
            Release();
            return $"ICorDebug::SetManagedHandler failed: 0x{hr:X8}";
        }

        Native.PROCESS_INFORMATION info;
        IntPtr line = Marshal.StringToHGlobalUni(command);
        try
        {
            unsafe
            {
                var startup = new Native.STARTUPINFO { cb = (uint)sizeof(Native.STARTUPINFO) };
                Native.PROCESS_INFORMATION started = default;

                hr = debug.CreateProcess(
                    null,
                    line,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0,
                    ShowConsole ? Native.CREATE_NEW_CONSOLE : Native.CREATE_NO_WINDOW,
                    IntPtr.Zero,
                    workingDirectory,
                    (IntPtr)(&startup),
                    (IntPtr)(&started),
                    NoSpecialOptions,
                    out IntPtr process);

                info = started;

                // The process comes back held by the callback below, which is where it is wanted.
                // This reference is the call's, not the session's: Created claims its own.
                if (process != IntPtr.Zero)
                {
                    Marshal.Release(process);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(line);
        }

        if (hr < 0)
        {
            Release();
            return $"could not launch {System.IO.Path.GetFileName(path)} under the .NET Framework debugger: 0x{hr:X8}";
        }

        _pid = info.dwProcessId;
        ProcessId = info.dwProcessId;
        _launched = info.hProcess;
        if (info.hThread != IntPtr.Zero)
        {
            _ = Native.CloseHandle(info.hThread);
        }

        Note($"launched {System.IO.Path.GetFileName(path)} as process {info.dwProcessId} under .NET Framework {runtime}");

        if (!_attached.Wait(patience))
        {
            if (Ended() is null)
            {
                _ = Native.TerminateProcess(_launched, 1);
            }

            Release();
            return $"{System.IO.Path.GetFileName(path)} started but its runtime never reported the process "
                   + $"within {patience.TotalSeconds:0}s, so there is nothing to drive. It has been stopped.";
        }

        return null;
    }

    /// <summary><c>CorDebugCreateProcessFlags.DEBUG_NO_SPECIAL_OPTIONS</c> — launch it as it is.</summary>
    private const int NoSpecialOptions = 0;

    /// <summary>
    /// Creates the process suspended, so a debugger can be registered before it runs anything.
    ///
    /// Done here rather than through dbgshim's <c>CreateProcessForLaunch</c>, which is the same
    /// <c>CreateProcessW</c> with no way to pass creation flags. Without them every debuggee gets a
    /// console window, which is right for a person watching a program and wrong for a test suite
    /// that starts a dozen of them behind whatever someone is doing. What dbgshim is needed for is
    /// the runtime-startup handshake below — that part is not reimplementable, and this part is a
    /// single documented call.
    ///
    /// Null when it started. The process handle is kept: until the runtime publishes itself there is
    /// no <c>ICorDebugProcess</c>, and something has to be able to kill it if that never happens.
    /// </summary>
    private string? Launch(string command, string? workingDirectory, out uint pid, out IntPtr resume)
    {
        pid = 0;
        resume = IntPtr.Zero;

        var startup = new Native.STARTUPINFO { cb = (uint)Marshal.SizeOf<Native.STARTUPINFO>() };

        // Writable, because CreateProcessW may modify the buffer it is given.
        char[] line = (command + '\0').ToCharArray();

        Native.PROCESS_INFORMATION info;
        bool started;
        unsafe
        {
            fixed (char* text = line)
            {
                started = Native.CreateProcess(
                    null, text, IntPtr.Zero, IntPtr.Zero, false,
                    Native.CREATE_SUSPENDED
                        | (ShowConsole ? Native.CREATE_NEW_CONSOLE : Native.CREATE_NO_WINDOW),
                    IntPtr.Zero, workingDirectory, ref startup, out info);
            }
        }

        if (!started)
        {
            return $"could not launch it: {Marshal.GetLastPInvokeErrorMessage()}";
        }

        pid = info.dwProcessId;
        resume = info.hThread;
        _launched = info.hProcess;
        return null;
    }

    /// <summary>The debuggee's exit code, or null while it is still running.</summary>
    private uint? Ended()
        => _launched != IntPtr.Zero
           && Native.GetExitCodeProcess(_launched, out uint code)
           && code != Native.STILL_ACTIVE
            ? code
            : null;

    /// <summary>
    /// The runtime is up. Called on a thread dbgshim owns, once, before the debuggee runs any
    /// managed code.
    /// </summary>
    [UnmanagedCallersOnly]
    private static unsafe void Started(IntPtr cordb, IntPtr parameter, int hr)
    {
        if (!GCHandle.FromIntPtr(parameter).IsAllocated
            || GCHandle.FromIntPtr(parameter).Target is not ManagedDebugSession session)
        {
            return;
        }

        session.RuntimeReady(cordb, hr);
    }

    private void RuntimeReady(IntPtr cordb, int hr)
    {
        try
        {
            if (hr < 0 || cordb == IntPtr.Zero)
            {
                _startupProblem = $"the runtime could not be debugged: 0x{hr:X8}";
                return;
            }

            if (Marshal.GetObjectForIUnknown(cordb) is not ICorDebug debug)
            {
                _startupProblem = "what the shim handed back is not an ICorDebug";
                return;
            }

            _debug = debug;

            int step = debug.Initialize();
            if (step < 0)
            {
                _startupProblem = $"ICorDebug::Initialize failed: 0x{step:X8}";
                return;
            }

            // From here the runtime calls in whenever anything happens, and every one of those calls
            // must end in a Continue. Setting the handler is therefore the last thing done, not the
            // first: a callback arriving before the session is ready would stop the debuggee with
            // nobody prepared to let it go.
            step = debug.SetManagedHandler(_callback!);
            if (step < 0)
            {
                _startupProblem = $"ICorDebug::SetManagedHandler failed: 0x{step:X8}";
                return;
            }

            // Attaching is a separate act from having the interface, which is easy to miss because
            // the shim's callback reads like the end of the story. It is not: registering for
            // runtime startup gets an ICorDebug for that process and nothing more, and until
            // DebugActiveProcess is called the runtime reports nothing at all — no CreateProcess, no
            // modules, no stops. The symptom is a debugger that starts cleanly and then sits
            // watching a process it is not attached to.
            step = debug.DebugActiveProcess(_pid, 0, out IntPtr process);
            if (step < 0)
            {
                _startupProblem = $"could not attach to process {_pid}: 0x{step:X8}";
                return;
            }

            if (process != IntPtr.Zero)
            {
                Marshal.Release(process);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            _startupProblem = $"the debugging interface could not be set up: {ex.Message}";
        }
        finally
        {
            _ready.Set();
        }
    }

    /// <summary>
    /// Sets a breakpoint at an IL offset in a method, by the token the metadata gives it.
    ///
    /// This is the shape a managed breakpoint has, and it is the reason for the whole exercise: the
    /// runtime is told about a method and an offset into its IL, and works out for itself where that
    /// landed once the JIT had been at it. Nothing here has to know an address, nothing is written
    /// into the process, and a method that is recompiled keeps its breakpoints.
    ///
    /// Works before the module is loaded, which is the normal case. Null when it was taken.
    /// </summary>
    public string? SetBreakpoint(string module, uint methodToken, uint ilOffset = 0)
        => Interop(() => SetBreakpointCore(module, methodToken, ilOffset));

    private string? SetBreakpointCore(string module, uint methodToken, uint ilOffset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);

        string name = System.IO.Path.GetFileName(module);
        var wanted = new ManagedBreakpoint(name, methodToken, ilOffset);

        ICorDebugModule? loaded;
        lock (_gate)
        {
            _wanted.Add(wanted);
            _loaded.TryGetValue(name, out loaded);
        }

        if (loaded is null)
        {
            Note($"breakpoint recorded for {name}, which is not loaded yet");
            return null;
        }

        if (Plant(loaded, wanted) is { } problem)
        {
            lock (_gate)
            {
                _wanted.Remove(wanted);
            }

            return problem;
        }

        return null;
    }

    /// <summary>
    /// Removes a breakpoint from a running process, by the same three things that set it.
    ///
    /// Turning it off is the part that matters, and releasing the pointer is not it. The runtime
    /// holds a reference of its own to every breakpoint it made, so dropping ours only gives back a
    /// handle: the breakpoint stays in the process and keeps stopping it, which is a debugger that
    /// appears to have cleared something and has not. <c>Activate(false)</c> is what makes it stop
    /// firing; the release afterwards is ordinary tidying.
    ///
    /// Works on one that is still waiting for its module too — that one is only a note, and
    /// forgetting the note is the whole of removing it. Null when it is gone.
    /// </summary>
    public string? ClearBreakpoint(string module, uint methodToken, uint ilOffset = 0)
        => Interop(() => ClearBreakpointCore(module, methodToken, ilOffset));

    private string? ClearBreakpointCore(string module, uint methodToken, uint ilOffset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);

        string name = System.IO.Path.GetFileName(module);

        Planted? planted;
        bool asked;
        lock (_gate)
        {
            asked = _wanted.Any(b => Same(b.Module, b.MethodToken, b.Offset, name, methodToken, ilOffset));
            planted = _planted.Find(p => Same(p.Module, p.MethodToken, p.Offset, name, methodToken, ilOffset));
        }

        if (!asked && planted is null)
        {
            // Said rather than shrugged off. A caller clearing something that was never there has
            // the wrong method or the wrong offset, and a silent success leaves it believing the
            // breakpoint it is still stopping at has been removed.
            return $"there is no breakpoint in {name} at method 0x{methodToken:X8}+IL_{ilOffset:X4}";
        }

        // Nothing to switch off in a process that has gone: the breakpoint object outlives the
        // program it was in, and asking it to deactivate then fails — which would leave a caller
        // unable to drop a breakpoint from a run that is already over.
        if (planted is not null && State != DebugState.Exited)
        {
            int hr = Activation.Set(planted.Pointer, false);
            if (hr < 0)
            {
                // Left in place, and in the list, because it is still in the process. Reporting it
                // as cleared here would be the exact failure this method exists to avoid.
                return $"the runtime would not turn the breakpoint off: 0x{hr:X8}";
            }
        }

        lock (_gate)
        {
            _wanted.RemoveAll(b => Same(b.Module, b.MethodToken, b.Offset, name, methodToken, ilOffset));
            _planted.RemoveAll(p => Same(p.Module, p.MethodToken, p.Offset, name, methodToken, ilOffset));
        }

        if (planted is not null)
        {
            Marshal.Release(planted.Pointer);
        }

        Note($"cleared the breakpoint in {name} at method 0x{methodToken:X8}+IL_{ilOffset:X4}"
             + (planted is null ? ", which was still waiting for its module" : string.Empty));
        return null;
    }

    /// <summary>
    /// Sets or clears a breakpoint named by a <c>Type::Method</c> that may be in an assembly other than
    /// the one opened. Returns a line saying what happened.
    ///
    /// A method's module and token are only knowable from that module's metadata, so a name in an
    /// assembly not loaded yet cannot be turned into a breakpoint now. It is held and resolved against
    /// each module as it loads — a framework assembly like <c>PresentationFramework</c> arrives well
    /// after the run starts — which is the same "wait for the module" a breakpoint by module name does,
    /// one level up. A name in an assembly already loaded resolves and plants at once.
    /// </summary>
    public string SetBreakpointByName(string type, string method, uint ilOffset = 0, bool on = true)
        => Interop(() => on ? SetByNameCore(type, method, ilOffset) : ClearByNameCore(type, method, ilOffset));

    private string SetByNameCore(string type, string method, uint ilOffset)
    {
        var wanted = new NamedBreakpoint(type, method, ilOffset);
        List<(string Name, ICorDebugModule Module)> loaded;
        lock (_gate)
        {
            if (!_byName.Contains(wanted))
            {
                _byName.Add(wanted);
            }

            loaded = _loaded.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        // Resolve now against everything already in — mscorlib and the like are loaded before the
        // program's own code runs, so a break on Environment::Exit can go in at the initial hold.
        var planted = new List<string>();
        foreach (var (name, module) in loaded)
        {
            Resolve(name, module, wanted, planted);
        }

        if (planted.Count > 0)
        {
            lock (_gate)
            {
                _byName.Remove(wanted);
            }

            return $"breakpoint in {type}::{method} at IL_{ilOffset:X4} — {string.Join(", ", planted)}";
        }

        return $"recorded a breakpoint for {type}::{method} at IL_{ilOffset:X4}; it is not in any module "
               + "loaded yet, so it goes in when one that defines it loads";
    }

    private string ClearByNameCore(string type, string method, uint ilOffset)
    {
        bool pending;
        List<(string Name, ICorDebugModule Module)> loaded;
        lock (_gate)
        {
            pending = _byName.RemoveAll(b =>
                b.Type.Equals(type, StringComparison.OrdinalIgnoreCase) && b.Method == method && b.Offset == ilOffset) > 0;
            loaded = _loaded.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        // Resolution is stable — the same modules define the same tokens — so re-resolving finds
        // exactly what setting it planted, and clears that.
        int cleared = 0;
        foreach (var (name, module) in loaded)
        {
            foreach (uint token in _types.MethodsNamed(Com.NameOf(module), type, method))
            {
                if (ClearBreakpointCore(name, token, ilOffset) is null)
                {
                    cleared++;
                }
            }
        }

        if (cleared > 0)
        {
            return $"cleared the breakpoint in {type}::{method} at IL_{ilOffset:X4}";
        }

        return pending
            ? $"removed the pending breakpoint for {type}::{method}, which had not been planted yet"
            : $"there is no breakpoint in {type}::{method} at IL_{ilOffset:X4}";
    }

    /// <summary>Plants a by-name breakpoint into one module if that module defines its type and method.</summary>
    private void Resolve(string moduleName, ICorDebugModule module, NamedBreakpoint wanted, List<string> planted)
    {
        foreach (uint token in _types.MethodsNamed(Com.NameOf(module), wanted.Type, wanted.Method))
        {
            var bp = new ManagedBreakpoint(moduleName, token, wanted.Offset);
            lock (_gate)
            {
                if (_planted.Exists(p => Same(p.Module, p.MethodToken, p.Offset, moduleName, token, wanted.Offset)))
                {
                    continue;   // already there
                }

                _wanted.Add(bp);
            }

            if (Plant(module, bp) is { } problem)
            {
                lock (_gate)
                {
                    _wanted.Remove(bp);
                }

                Note(problem);
            }
            else
            {
                planted.Add($"{moduleName}!0x{token:X8}+IL_{wanted.Offset:X4}");
                Note($"breakpoint planted in {moduleName} at method 0x{token:X8}+IL_{wanted.Offset:X4}");
            }
        }
    }

    /// <summary>The by-name breakpoints still waiting for a module, written for a listing.</summary>
    public IReadOnlyList<string> PendingNamedBreakpoints
    {
        get
        {
            lock (_gate)
            {
                return _byName
                    .Select(b => $"{b.Type}::{b.Method}+IL_{b.Offset:X4} (waiting for its module)")
                    .ToList();
            }
        }
    }

    /// <summary>
    /// Records a patch to write into its module the moment that module loads.
    ///
    /// Meant to be called before the run starts, alongside the breakpoints: the patch is held and
    /// written when the module lands, which for a managed patch is the only time it can take — the
    /// method's IL is in memory then and the JIT has not yet turned it into the native code that
    /// actually runs. Applied afterwards it would change bytes nothing reads. Whether each patch went
    /// in, and why one did not, is reported on the log as its module loads.
    /// </summary>
    public void ApplyOnLoad(ManagedPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentException.ThrowIfNullOrWhiteSpace(patch.Module);

        if (patch.Bytes.IsDefaultOrEmpty)
        {
            return;
        }

        string name = System.IO.Path.GetFileName(patch.Module);
        var normalised = patch with { Module = name };

        ICorDebugModule? loaded;
        lock (_gate)
        {
            _patches.Add(normalised);
            _loaded.TryGetValue(name, out loaded);
        }

        // The usual case is that the module has not loaded, so this is only a note; the module-load
        // callback writes it. But a caller adding one after its module is already in — which is too
        // late to matter for a method that has run, and exactly right for one that has not — should
        // not have it silently ignored, so it is written now.
        if (loaded is not null)
        {
            Interop(() =>
            {
                if (WritePatch(loaded, normalised) is { } problem)
                {
                    Note(problem);
                }

                lock (_gate)
                {
                    _patches.Remove(normalised);
                }
            });
        }
        else
        {
            Note($"patch recorded for {name}, to write when it loads");
        }
    }

    /// <summary>The patches still waiting for a module, by that module's name.</summary>
    private List<ManagedPatch> PatchesFor(string module)
    {
        lock (_gate)
        {
            return _patches.Where(p => string.Equals(p.Module, module, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    /// <summary>The by-name breakpoints still unresolved — every module load is a chance to place them.</summary>
    private List<NamedBreakpoint> NamedFor()
    {
        lock (_gate)
        {
            return _byName.ToList();
        }
    }

    /// <summary>
    /// Writes one patch into a loaded module's method IL, or says why it would not go.
    ///
    /// The address comes from the runtime's IL-code object for the method, not from the module base
    /// and an RVA: a managed image is not mapped section-for-section into the process, so the IL lives
    /// wherever the loader put it and only <c>GetAddress</c> knows where. That is the same route the
    /// breakpoints take to a method (token, then IL code), for the same reason. When the patch carries
    /// what it expected to replace, the IL is read back and checked first, so a patch cut against IL
    /// the method no longer has — recompiled, or a native image running instead — is refused rather
    /// than written over whatever is there now.
    /// </summary>
    private string? WritePatch(ICorDebugModule module, ManagedPatch patch)
    {
        if (_process is not { } process)
        {
            return $"cannot patch {patch.Module}: there is no process";
        }

        int hr = module.GetFunctionFromToken(patch.MethodToken, out var function);
        if (hr < 0 || function is null)
        {
            return $"cannot patch {patch.Module}: it has no method with token 0x{patch.MethodToken:X8} (0x{hr:X8})";
        }

        try
        {
            hr = function.GetILCode(out var code);
            if (hr < 0 || code is null)
            {
                return $"cannot patch method 0x{patch.MethodToken:X8}: it has no IL to write into (0x{hr:X8})";
            }

            try
            {
                hr = code.GetAddress(out ulong ilStart);
                if (hr < 0 || ilStart == 0)
                {
                    return $"cannot patch method 0x{patch.MethodToken:X8}: its IL has no address yet (0x{hr:X8})";
                }

                _ = code.GetSize(out uint ilSize);
                int length = patch.Bytes.Length;
                if (ilSize != 0 && patch.IlOffset + (uint)length > ilSize)
                {
                    return $"cannot patch method 0x{patch.MethodToken:X8}: {length} byte(s) at IL_{patch.IlOffset:X4} "
                           + $"run past the {ilSize}-byte method";
                }

                ulong address = ilStart + patch.IlOffset;
                string where = $"{patch.Module}!0x{patch.MethodToken:X8}+IL_{patch.IlOffset:X4}";

                var expected = patch.Expected;
                if (expected.Length == length)
                {
                    byte[] present = new byte[length];
                    hr = process.ReadMemory(address, (uint)length, present, out IntPtr read);
                    if (hr < 0 || (int)read != length)
                    {
                        return $"patch at {where} not applied: could not read the {length} byte(s) there (0x{hr:X8})";
                    }

                    if (!present.AsSpan().SequenceEqual(expected.AsSpan()))
                    {
                        return $"patch at {where} not applied: the method's IL holds {Convert.ToHexString(present)} "
                               + $"where the patch expected {Convert.ToHexString(expected.AsSpan())} — the code running "
                               + "here is not the file the patch was made against (a recompiled or precompiled method?)";
                    }
                }

                // A method's IL is mapped read-only — nothing is meant to change it — so both the
                // runtime's own WriteMemory and a plain WriteProcessMemory refuse it with
                // ERROR_NOACCESS (0x800703E6). The page has to be made writable first: unlike native
                // code, which lives on executable pages WriteProcessMemory will write through, IL is
                // read-only data. So the protection is lifted with VirtualProtectEx, the bytes are
                // written, and the protection is put straight back.
                if (process.GetHandle(out IntPtr handle) < 0 || handle == IntPtr.Zero)
                {
                    return $"patch at {where} not applied: the process gave no handle to write through";
                }

                if (!Native.VirtualProtectEx(handle, address, (nuint)length, Native.PageReadWrite, out uint previous))
                {
                    return $"patch at {where} not applied: the IL page could not be made writable "
                           + $"(Win32 0x{Marshal.GetLastWin32Error():X})";
                }

                byte[] bytes = patch.Bytes.ToArray();
                bool wrote;
                nuint written;
                try
                {
                    unsafe
                    {
                        fixed (byte* p = bytes)
                        {
                            wrote = Native.WriteProcessMemory(handle, address, p, (nuint)length, out written);
                        }
                    }
                }
                finally
                {
                    Native.VirtualProtectEx(handle, address, (nuint)length, previous, out _);
                }

                if (!wrote || (int)written != length)
                {
                    return $"patch at {where} not applied: the write failed (Win32 0x{Marshal.GetLastWin32Error():X})";
                }

                Native.FlushInstructionCache(handle, address, (nuint)length);
                Note($"patched {where}: {length} byte(s) written before the method was compiled");
                return null;
            }
            finally
            {
                Com.Drop(code);
            }
        }
        finally
        {
            Com.Drop(function);
        }
    }

    /// <summary>Every breakpoint asked for, and whether it is in the process yet.</summary>
    public IReadOnlyList<ManagedBreakpoint> Breakpoints
    {
        get
        {
            lock (_gate)
            {
                return _wanted.ToList();
            }
        }
    }

    /// <summary>Breakpoints waiting on a module that has just arrived.</summary>
    private List<ManagedBreakpoint> Waiting(string module)
    {
        lock (_gate)
        {
            return _wanted.Where(b => !b.Planted && string.Equals(b.Module, module, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    /// <summary>Puts one breakpoint into a loaded module, or says why it would not go.</summary>
    private string? Plant(ICorDebugModule module, ManagedBreakpoint wanted)
    {
        try
        {
            int hr = module.GetFunctionFromToken(wanted.MethodToken, out var function);
            if (hr < 0 || function is null)
            {
                return $"{wanted.Module} has no method with token 0x{wanted.MethodToken:X8} (0x{hr:X8})";
            }

            hr = function.GetILCode(out var code);
            if (hr < 0 || code is null)
            {
                // An abstract method, or one whose IL the runtime will not hand over. Worth naming
                // rather than reporting as a breakpoint that was set and never hit.
                Com.Drop(function);
                return $"method 0x{wanted.MethodToken:X8} has no IL to break in (0x{hr:X8})";
            }

            hr = code.CreateBreakpoint(wanted.Offset, out IntPtr breakpoint);
            if (hr < 0 || breakpoint == IntPtr.Zero)
            {
                Com.Drop(code);
                Com.Drop(function);
                return $"IL_{wanted.Offset:X4} is not somewhere a breakpoint can go in method 0x{wanted.MethodToken:X8} (0x{hr:X8})";
            }

            // A breakpoint arrives switched off. Creating one and forgetting this is a breakpoint
            // that exists, lists, and never fires.
            hr = Activation.Set(breakpoint, true);
            if (hr < 0)
            {
                Marshal.Release(breakpoint);
                Com.Drop(code);
                Com.Drop(function);
                return $"the breakpoint could not be activated (0x{hr:X8})";
            }

            lock (_gate)
            {
                _planted.Add(new Planted(wanted.Module, wanted.MethodToken, wanted.Offset, breakpoint));
                int at = _wanted.IndexOf(wanted);
                if (at >= 0)
                {
                    _wanted[at] = wanted with { Planted = true };
                }
            }

            Com.Drop(code);
            Com.Drop(function);
            return null;
        }
        catch (COMException ex)
        {
            return $"the runtime refused the breakpoint: {ex.Message}";
        }
    }

    /// <summary>
    /// Moves one IL instruction, into a call or over it, and lets the debuggee run until it lands.
    ///
    /// By IL, not by machine instruction: one IL instruction is a unit of the program, where one
    /// machine instruction is a unit of whatever the JIT chose to emit for it on this run. Nothing
    /// about the step refers to an address, which is what makes it repeatable.
    ///
    /// Null when the step was armed. It is reported as complete through the usual stop, so a caller
    /// waits on <see cref="WaitUntilStopped"/> exactly as it would for a breakpoint.
    /// </summary>
    public string? Step(bool into = true) => Interop(() => StepCore(into, null));

    /// <summary>
    /// Runs until execution leaves an IL range, stepping into calls made inside it or over them.
    ///
    /// The range is what turns this from stepping IL into stepping the program. One statement is
    /// several instructions — a receiver pushed, arguments pushed, a call, a result stored — and a
    /// reader who steps through them one at a time is shown four stops that are all the same line.
    /// Which offsets bound a statement is not this type's business to work out: it has the IL of the
    /// method it is stopped in and no way to read a signature, so the caller says.
    /// </summary>
    /// <param name="from">First IL offset of the range.</param>
    /// <param name="to">One past its last, which is where the step lands.</param>
    public string? Step(bool into, uint from, uint to) => Interop(() => StepCore(into, (from, to)));

    private string? StepCore(bool into, (uint From, uint To)? over)
    {
        ICorDebugThread? thread;
        lock (_gate)
        {
            if (State != DebugState.Stopped)
            {
                return State == DebugState.Exited ? "it has exited" : "it is not stopped, so there is nothing to step";
            }

            thread = _selected ?? _stopped;
        }

        if (thread is null)
        {
            return "the stop was not on a managed thread, so there is nothing to step";
        }

        Retire();

        uint at = StoppedAt?.Offset ?? 0;

        // One instruction when nobody said otherwise. A caller that cannot work out where the
        // statement ends — code in another assembly, a method whose signatures will not read — is
        // better served by a step that moves a little than by one that refuses to move at all.
        var (from, to) = over ?? (at, at + 1);

        int hr = thread.CreateStepper(out var stepper);
        if (hr < 0 || stepper is null)
        {
            return $"the runtime would not make a stepper: 0x{hr:X8}";
        }

        // Not stopping in code that maps to no IL at all — prologues, stubs, the insides of the
        // runtime. Without the mask a step regularly lands somewhere with nothing to show.
        _ = stepper.SetRangeIL(1);
        _ = stepper.SetUnmappedStopMask(StopNowhereUnmapped);

        // A range, not a plain Step, and that is the whole difference between stepping IL and
        // stepping the JIT's output. Step() moves one machine instruction, and one IL instruction is
        // usually several of those — so three steps from IL_0001 reported complete three times
        // without the IL offset ever changing, which reads as a debugger that will not move.
        // StepRange runs until execution leaves the range, whatever that range is: one instruction
        // wide steps IL, a statement wide steps the program.
        IntPtr range = Marshal.AllocCoTaskMem(sizeof(uint) * 2);
        try
        {
            Marshal.WriteInt32(range, 0, (int)from);
            Marshal.WriteInt32(range, sizeof(uint), (int)to);
            hr = stepper.StepRange(into ? 1 : 0, range, 1);
        }
        finally
        {
            Marshal.FreeCoTaskMem(range);
        }

        if (hr < 0)
        {
            Com.Drop(stepper);
            return $"the step was refused: 0x{hr:X8}";
        }

        lock (_gate)
        {
            _stepper = stepper;
        }

        Continue();
        return null;
    }

    /// <summary>Runs to the end of the current method and stops in whatever called it.</summary>
    public string? StepOut() => Interop(StepOutCore);

    private string? StepOutCore()
    {
        ICorDebugThread? thread;
        lock (_gate)
        {
            if (State != DebugState.Stopped)
            {
                return "it is not stopped, so there is nothing to step out of";
            }

            thread = _selected ?? _stopped;
        }

        if (thread is null || thread.CreateStepper(out var stepper) < 0 || stepper is null)
        {
            return "the runtime would not make a stepper for this thread";
        }

        try
        {
            _ = stepper.SetUnmappedStopMask(StopNowhereUnmapped);
            int hr = stepper.StepOut();
            if (hr < 0)
            {
                return $"stepping out was refused: 0x{hr:X8}";
            }
        }
        finally
        {
            Com.Drop(stepper);
        }

        Continue();
        return null;
    }

    /// <summary><c>STOP_NONE</c>: never stop in code that maps to no IL.</summary>
    private const int StopNowhereUnmapped = 0;

    /// <summary>How long to give the runtime to bring the process to a halt before terminating it.</summary>
    private const uint SynchroniseTimeout = 5000;

    /// <summary>
    /// Turns off the stepper from the last step, if there is one.
    ///
    /// A completed stepper is not finished with. Left alone it stays armed, and the next step
    /// reports complete straight away at the offset the previous one reached — which reads as a
    /// debugger that steps once and then refuses to move, with every call returning success. The
    /// walk went IL_0000, IL_0001, IL_0001, IL_0001.
    /// </summary>
    private void Retire()
    {
        ICorDebugStepper? previous;
        lock (_gate)
        {
            previous = _stepper;
            _stepper = null;
        }

        if (previous is null)
        {
            return;
        }

        try
        {
            _ = previous.Deactivate();
        }
        catch (COMException)
        {
            // A stepper whose thread has gone cannot be deactivated, and does not need to be.
        }

        Com.Drop(previous);
    }

    /// <summary>
    /// Which interfaces one of the stopped frame's arguments actually answers to.
    ///
    /// A diagnostic, not a feature: two IIDs written from memory were wrong, and a wrong IID fails
    /// as "not available" rather than as a mistake. This asks the object instead of guessing again.
    /// </summary>
    public IReadOnlyList<string> ProbeArgumentInterfaces(uint index, Func<IntPtr, IReadOnlyList<string>> ask)
    {
        ArgumentNullException.ThrowIfNull(ask);

        return Interop(() => ProbeCore(index, ask));
    }

    private IReadOnlyList<string> ProbeCore(uint index, Func<IntPtr, IReadOnlyList<string>> ask)
    {
        ICorDebugThread? thread;
        lock (_gate)
        {
            thread = State == DebugState.Stopped ? _selected ?? _stopped : null;
        }

        if (thread is null || thread.GetActiveFrame(out IntPtr frame) < 0 || frame == IntPtr.Zero)
        {
            return Array.Empty<string>();
        }

        return Com.Owned<ICorDebugILFrame, IReadOnlyList<string>>(frame, il =>
        {
            if (il.GetArgument(index, out IntPtr value) < 0 || value == IntPtr.Zero)
            {
                return Array.Empty<string>();
            }

            try
            {
                var found = new List<string>(ask(value));
                var deeper = Com.Borrow<ICorDebugReferenceValue, IReadOnlyList<string>>(value, r =>
                    r.Dereference(out IntPtr pointed) == 0 && pointed != IntPtr.Zero ? Ask(pointed, ask) : null);
                if (deeper is not null)
                {
                    found.Add("--- dereferenced ---");
                    found.AddRange(deeper);
                }

                return found;
            }
            finally
            {
                Marshal.Release(value);
            }
        }) ?? Array.Empty<string>();
    }

    /// <summary>
    /// Hands the thread being looked at to a diagnostic, as a raw pointer. The same purpose as
    /// <see cref="ProbeArgumentInterfaces"/>: an interface id is asked of a live object before
    /// anything is built on it, and the thread is where stacks and evaluations start.
    /// </summary>
    public IReadOnlyList<string> ProbeThreadInterfaces(Func<IntPtr, IReadOnlyList<string>> ask)
    {
        ArgumentNullException.ThrowIfNull(ask);

        return Interop(() =>
        {
            ICorDebugThread? thread;
            lock (_gate)
            {
                thread = State == DebugState.Stopped ? _selected ?? _stopped : null;
            }

            if (thread is null)
            {
                return Array.Empty<string>();
            }

            IntPtr unknown = Marshal.GetIUnknownForObject(thread);
            try
            {
                return ask(unknown);
            }
            finally
            {
                Marshal.Release(unknown);
            }
        });
    }

    private static IReadOnlyList<string> Ask(IntPtr pointer, Func<IntPtr, IReadOnlyList<string>> ask)
    {
        try
        {
            return ask(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    /// <summary>Lets it run on. Does nothing unless it is stopped.</summary>
    public void Continue() => Interop(ContinueCore);

    private void ContinueCore()
    {
        ICorDebugThread? letting;
        lock (_gate)
        {
            if (State != DebugState.Stopped || _process is null)
            {
                return;
            }

            State = DebugState.Running;
            Status = "running";
            StoppedBy = ManagedStopKind.None;
            letting = _stopped;
            _stopped = null;
            Com.Drop(_selected);
            _selected = null;
            _frame = -1;
            StoppedAt = null;
            _settled.Reset();
        }

        // The stopped thread is only worth holding while it is stopped. Once it runs, the frames it
        // had are gone and anything asked of it afterwards is a question about a program that has
        // moved on. The handles on evaluated values go with them.
        Com.Drop(letting);
        DisposeHandles();

        int hr = _process!.Continue(0);
        if (hr < 0)
        {
            Note($"could not continue: 0x{hr:X8}");
        }
    }

    /// <summary>Ends the process and the session with it.</summary>
    public void Stop() => Interop(StopCore);

    private void StopCore()
    {
        lock (_gate)
        {
            if (State is DebugState.NotStarted or DebugState.Exited)
            {
                return;
            }
        }

        try
        {
            if (_process is { } process)
            {
                // Synchronised, then told, then let go. Each of the three is load-bearing.
                //
                // Terminate on a process that is running is refused outright —
                // CORDBG_E_PROCESS_NOT_SYNCHRONIZED, 0x80131302 — and that return used to be
                // thrown away, so a session that had been asked to kill something said it had. The
                // process went on running and announced itself through the next callback, which is
                // the worst version of this: a debuggee outliving the debugger that believes it is
                // gone. And a synchronised process does not die on the call either; it dies when it
                // is continued, so the continue below is what actually ends it.
                if (process.IsRunning(out int running) >= 0 && running != 0)
                {
                    _ = process.Stop(SynchroniseTimeout);
                }

                int hr = process.Terminate(0);
                if (hr < 0)
                {
                    // Asked once through the runtime and then done anyway. A process that will not
                    // synchronise inside the timeout — a big application still starting, which is
                    // exactly when somebody changes their mind about running it — refuses this with
                    // CORDBG_E_PROCESS_NOT_SYNCHRONIZED, and stopping there leaves the debuggee
                    // running under a session that is about to drop the interface to it. The
                    // handle from the launch does not need the runtime's cooperation.
                    Note($"the runtime would not terminate it (0x{hr:X8}), so it was killed outright");
                    if (_launched != IntPtr.Zero)
                    {
                        _ = Native.TerminateProcess(_launched, 1);
                    }
                }

                _ = process.Continue(0);
            }
            else if (_launched != IntPtr.Zero)
            {
                // No ICorDebugProcess means the runtime never got far enough to report one — and the
                // process is still there regardless, so it is killed through the handle from the
                // launch rather than left behind because the debugging interface never arrived.
                _ = Native.TerminateProcess(_launched, 1);
            }
        }
        catch (COMException)
        {
            // Already gone. The state below says so either way.
        }

        Exited();
    }

    /// <summary>
    /// Waits for the debuggee to come to a stop, or gives up.
    ///
    /// Everything here is asynchronous — the runtime reports on its own thread and the process runs
    /// until something catches it — so a caller that returned the moment it asked would describe the
    /// state before the thing it asked for had happened.
    /// </summary>
    public bool WaitUntilStopped(TimeSpan timeout) => _settled.Wait(timeout);

    // ------------------------------------------------------------------
    // What the callback reports
    // ------------------------------------------------------------------

    bool IManagedEvents.Created(IntPtr process)
    {
        try
        {
            if (Marshal.GetObjectForIUnknown(process) is ICorDebugProcess handle)
            {
                // Held for the session's lifetime, unlike everything else a callback is handed.
                // This is the one object that has to outlive the call it arrived on: it is what
                // Continue, Terminate and every later question go through.
                _process = handle;
                handle.GetID(out uint id);
                ProcessId = id;
                Note($"the runtime is attached to process {id}");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Note("the process could not be held on to");
        }

        if (!_holdAtStart)
        {
            return false;
        }

        lock (_gate)
        {
            State = DebugState.Stopped;
            StoppedBy = ManagedStopKind.Created;
            Status = "held before it ran anything";
        }

        Note("held before it ran anything; set breakpoints and continue");
        _settled.Set();
        return true;
    }

    void IManagedEvents.Attached() => _attached.Set();

    bool IManagedEvents.ModuleLoaded(IntPtr module, IntPtr controller)
    {
        var held = Com.Keep<ICorDebugModule>(module);
        if (held is null)
        {
            Note("a module loaded that could not be read");
            return false;
        }

        string name = Com.NameOf(held) is { } path ? System.IO.Path.GetFileName(path) : "(unnamed)";

        lock (_gate)
        {
            _modules.Add(name);
            _loaded[name] = held;
        }

        // Breakpoints outlive the modules they are in. One set before anything ran has been waiting
        // for exactly this moment, and a debugger that only planted at the time of asking could
        // never break on a library that loads later — which is most of them. Patches wait for the
        // same moment, and for a stronger reason: this is the only point at which writing a managed
        // patch has any effect (see ManagedPatch), so a module with patches queued is held here even
        // when it has no breakpoints.
        var pending = Waiting(name);
        var patches = PatchesFor(name);
        var named = NamedFor();
        if (pending.Count == 0 && patches.Count == 0 && named.Count == 0)
        {
            return false;
        }

        // Planted off this thread, and the debuggee held until it is done.
        //
        // ICorDebugCode::CreateBreakpoint does not return when it is called on the thread the
        // runtime is delivering the callback on — the other calls needed to reach it,
        // GetFunctionFromToken and GetILCode, both answer immediately, and then this one simply
        // never comes back. So the work goes to another thread and this one returns at once
        // without continuing, which leaves the debuggee stopped exactly where it was: the module is
        // loaded and nothing in it has run. The worker continues it when the breakpoints are in.
        var keeper = Com.Keep<ICorDebugController>(controller);
        if (keeper is null)
        {
            return false;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            // Patches first, before the breakpoints and before the continue: writing the module's IL
            // while it is loaded and none of its methods has compiled is the whole point of doing it
            // here. Each is dropped once attempted, so a module loading again does not rewrite it.
            foreach (var patch in patches)
            {
                if (WritePatch(held, patch) is { } problem)
                {
                    Note(problem);
                }

                lock (_gate)
                {
                    _patches.Remove(patch);
                }
            }

            foreach (var wanted in pending)
            {
                if (Plant(held, wanted) is { } problem)
                {
                    Note(problem);
                }
                else
                {
                    Note($"breakpoint planted in {name} at method 0x{wanted.MethodToken:X8}+IL_{wanted.Offset:X4}");
                }
            }

            // A Type::Method named before its assembly was known — this may be that assembly. What
            // resolves here plants and leaves the waiting list; what does not stays for a later module.
            foreach (var wanted in named)
            {
                var planted = new List<string>();
                Resolve(name, held, wanted, planted);
                if (planted.Count > 0)
                {
                    lock (_gate)
                    {
                        _byName.Remove(wanted);
                    }
                }
            }

            keeper.Continue(0);
            Com.Drop(keeper);
        });

        return true;
    }

    bool IManagedEvents.Stopped(ManagedStopKind kind, IntPtr controller, IntPtr thread, string text)
    {
        var at = Where(thread);

        // Held only for as long as the stop lasts. A stepper is created on the thread that stopped,
        // so it has to survive the callback returning — and no longer, because once the program is
        // continued the thread's frames are a description of somewhere it no longer is.
        var stopped = Com.Keep<ICorDebugThread>(thread);

        lock (_gate)
        {
            Com.Drop(_stopped);
            _stopped = stopped;
            Com.Drop(_selected);
            _selected = null;
            _frame = -1;
            State = DebugState.Stopped;
            StoppedBy = kind;
            StoppedAt = at;
            Stops++;
            Status = at is null ? text : $"{text} at {at}";
        }

        Note(Status);
        _settled.Set();
        return true;
    }

    bool IManagedEvents.EvalFinished(bool failed)
    {
        lock (_gate)
        {
            if (!_evaluating)
            {
                return false;   // not ours — an eval nobody here asked for, which is let go
            }

            _evalFailed = failed;
        }

        _evalDone.Set();
        return true;
    }

    /// <summary>Where the program is, once it has stopped: a method and an offset into its IL.</summary>
    public ManagedLocation? StoppedAt { get; private set; }

    /// <summary>
    /// The locals and arguments of the frame it stopped in, as text.
    ///
    /// The whole reason for driving the runtime rather than the process. A native stop gives back
    /// registers and stack words, and turning those into "the path is C:\Windows\notepad.exe" is
    /// work the analyst does by hand from a calling convention they have to know. Here the runtime
    /// knows what every slot is, so the stop can simply say.
    ///
    /// Only while it is stopped: the frame is what the values live in, and there is no frame to read
    /// once the program is running again.
    /// </summary>
    public IReadOnlyList<ManagedValue> Values(bool arguments = false) => Interop(() => ValuesCore(arguments));

    private IReadOnlyList<ManagedValue> ValuesCore(bool arguments)
    {
        ICorDebugThread? thread;
        lock (_gate)
        {
            thread = State == DebugState.Stopped ? _selected ?? _stopped : null;
        }

        if (thread is null || thread.GetActiveFrame(out IntPtr frame) < 0 || frame == IntPtr.Zero)
        {
            return Array.Empty<ManagedValue>();
        }

        return Com.Owned<ICorDebugILFrame, IReadOnlyList<ManagedValue>>(
            frame,
            il =>
            {
                if (!arguments)
                {
                    return ManagedValues.Locals(il, _types);
                }

                var shape = Shape(il).Method;
                return ManagedValues.Arguments(il, _types)
                    .Select(value => value with { Name = ArgumentName(shape, (uint)value.Index) })
                    .ToList();
            })
            ?? Array.Empty<ManagedValue>();
    }

    /// <summary>
    /// The frame's arguments and locals as the top of a value tree: <c>this</c> and the parameters by
    /// name, locals by slot, each with its declared type. Open one with <see cref="Children"/>.
    ///
    /// Read from the thread being looked at, which is the one that stopped unless another has been
    /// picked with <see cref="SelectThread"/>.
    /// </summary>
    public IReadOnlyList<ManagedVariable> Variables() => Interop(VariablesCore);

    /// <summary>
    /// What is inside a value the tree has shown: an object's fields, including every one its base
    /// classes declare, or an array's elements. Walked again from the frame each time, so a path kept
    /// across a step shows what is there now.
    /// </summary>
    public IReadOnlyList<ManagedVariable> Children(ManagedValuePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Interop(() => ChildrenCore(path));
    }

    /// <summary>How many argument or local slots to ask for before deciding a frame is not answering.</summary>
    private const uint MaxSlots = 256;

    private IReadOnlyList<ManagedVariable> VariablesCore()
        => InFrame<IReadOnlyList<ManagedVariable>>(il =>
        {
            var (shape, locals) = Shape(il);
            var rows = new List<ManagedVariable>();

            // As many as the signature says there are, when it can be read. A refusal is then a slot the
            // runtime will not read here — code the JIT optimised, which is most of the framework's own —
            // and is shown as unavailable rather than taken for the end of the list. Taken for the end,
            // Thread.Sleep(int millisecondsTimeout) listed no arguments at all, which reads as a method
            // that has none.
            uint arguments = shape is null ? MaxSlots : (uint)shape.ParameterNames.Count + (shape.IsStatic ? 0u : 1u);
            for (uint i = 0; i < arguments; i++)
            {
                if (il.GetArgument(i, out IntPtr value) < 0)
                {
                    if (shape is null)
                    {
                        break;
                    }

                    value = IntPtr.Zero;
                }

                try
                {
                    rows.Add(ManagedVariables.Present(
                        value, ArgumentName(shape, i), ArgumentType(shape, i), ManagedValuePath.OfArgument(i), _types));
                }
                finally
                {
                    if (value != IntPtr.Zero)
                    {
                        Marshal.Release(value);
                    }
                }
            }

            uint slots = shape is null ? MaxSlots : (uint)locals.Count;
            for (uint i = 0; i < slots; i++)
            {
                if (il.GetLocalVariable(i, out IntPtr value) < 0)
                {
                    if (shape is null)
                    {
                        break;
                    }

                    value = IntPtr.Zero;
                }

                try
                {
                    // V_n until something better is known. Without symbols a local has no name in the
                    // binary at all; the window swaps in the decompiler's, which is the name the C#
                    // view is showing for the same slot.
                    rows.Add(ManagedVariables.Present(
                        value,
                        "V_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        i < locals.Count ? locals[(int)i] : string.Empty,
                        ManagedValuePath.OfLocal(i),
                        _types));
                }
                finally
                {
                    if (value != IntPtr.Zero)
                    {
                        Marshal.Release(value);
                    }
                }
            }

            return rows;
        }) ?? Array.Empty<ManagedVariable>();

    private IReadOnlyList<ManagedVariable> ChildrenCore(ManagedValuePath path)
    {
        bool statics = path.Steps.Length > 0 && path.Steps[^1].IsStaticGroup;

        return Resolved<IReadOnlyList<ManagedVariable>>(path, (value, frame) => statics
            ? ManagedVariables.StaticMembers(value, path, _types, frame)
            : ManagedVariables.Children(value, path, _types))
            ?? Array.Empty<ManagedVariable>();
    }

    /// <summary>
    /// Finds the value a path names and hands it to <paramref name="use"/>, along with the frame it
    /// was found through — which a static needs, since which app domain and thread it belongs to is
    /// decided by where execution is. The value is borrowed: it is released when this returns.
    ///
    /// Three kinds of start. An argument or a local is read from the frame being looked at. A value a
    /// getter produced is read from the handle keeping it alive, and needs no frame at all.
    /// </summary>
    private T? Resolved<T>(ManagedValuePath path, Func<IntPtr, IntPtr, T?> use)
        where T : class
    {
        if (path.Root == ManagedValueRoot.Evaluated)
        {
            IntPtr handle;
            lock (_gate)
            {
                if (!_handles.TryGetValue(path.Slot, out handle))
                {
                    // The handle went when the process ran on. The row it belonged to is about to be
                    // rebuilt anyway; nothing to show is the true answer until it is.
                    return null;
                }
            }

            return Walk(handle, path, IntPtr.Zero, use);
        }

        return InFrame<T>(il =>
        {
            IntPtr frame = FramePointer(il);
            try
            {
                IntPtr value;
                int hr = path.Root == ManagedValueRoot.Argument
                    ? il.GetArgument(path.Slot, out value)
                    : il.GetLocalVariable(path.Slot, out value);
                if (hr < 0 || value == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return Walk(value, path, frame, use);
                }
                finally
                {
                    Marshal.Release(value);
                }
            }
            finally
            {
                if (frame != IntPtr.Zero)
                {
                    Marshal.Release(frame);
                }
            }
        });
    }

    /// <summary>Follows the steps of a path from a value already in hand. Borrows the value it starts from.</summary>
    private T? Walk<T>(IntPtr root, ManagedValuePath path, IntPtr frame, Func<IntPtr, IntPtr, T?> use)
        where T : class
    {
        IntPtr value = root;
        bool owned = false;

        // A path ending at the static-members group stops one step short: the statics belong to the
        // type of the value the last real step reached, not to anything inside it.
        int steps = path.Steps.Length;
        int walk = steps > 0 && path.Steps[steps - 1].IsStaticGroup ? steps - 1 : steps;

        try
        {
            for (int i = 0; i < walk; i++)
            {
                var step = path.Steps[i];
                IntPtr next = step.Static && step.Field != 0
                    ? ManagedVariables.FollowStatic(value, step, frame)
                    : ManagedVariables.Follow(value, step);

                if (owned)
                {
                    Marshal.Release(value);
                }

                if (next == IntPtr.Zero)
                {
                    // The thing this path went through has changed under it since it was shown — a
                    // field that was an object is null now.
                    return null;
                }

                value = next;
                owned = true;
            }

            return use(value, frame);
        }
        finally
        {
            if (owned)
            {
                Marshal.Release(value);
            }
        }
    }

    /// <summary>The frame as an <c>ICorDebugFrame</c> pointer, which is what reading a static wants.</summary>
    private static IntPtr FramePointer(ICorDebugILFrame il)
    {
        IntPtr unknown = Marshal.GetIUnknownForObject(il);
        try
        {
            return Com.QueryInterface(unknown, CorDebugGuids.Frame);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>
    /// Keeps an evaluated object alive behind a handle, and answers with the number a path names it
    /// by. Zero when it is not something that can be held — a number, or a null.
    /// </summary>
    private uint Keep(IntPtr value)
    {
        IntPtr held = Com.Borrow<ICorDebugReferenceValue, IntPtr?>(value, r =>
            r.IsNull(out int isNull) == 0 && isNull == 0 && r.Dereference(out IntPtr pointed) == 0 ? pointed : null) ?? IntPtr.Zero;
        if (held == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            IntPtr handle = Com.Borrow<ICorDebugHeapValue2, IntPtr?>(held, heap =>
                heap.CreateHandle(HandleStrong, out IntPtr made) == 0 ? made : null) ?? IntPtr.Zero;
            if (handle == IntPtr.Zero)
            {
                return 0;
            }

            lock (_gate)
            {
                uint id = ++_handled;
                _handles[id] = handle;
                return id;
            }
        }
        finally
        {
            Marshal.Release(held);
        }
    }

    /// <summary>
    /// Lets go of every handle. Called when the process runs on: the values they name are about to be
    /// described by rows that no longer exist, and a strong handle nobody drops is a leak in the
    /// debuggee rather than in the debugger.
    /// </summary>
    private void DisposeHandles()
    {
        List<IntPtr> holding;
        lock (_gate)
        {
            holding = _handles.Values.ToList();
            _handles.Clear();
        }

        foreach (IntPtr handle in holding)
        {
            _ = Com.Borrow<ICorDebugHandleValue, int?>(handle, h => h.Dispose());
            Marshal.Release(handle);
        }
    }

    /// <summary>
    /// Writes a value into the debuggee: a number, a character, a bool, an enum member, null, or a
    /// string in quotes. Null when it was written; a sentence when it was not.
    ///
    /// A string is made in the debuggee first, which is an evaluation — it runs the runtime's own
    /// allocator — and then the reference is pointed at it. Nothing else here runs any code.
    /// </summary>
    public string? SetValue(ManagedValuePath path, string text)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(text);

        return Interop(() => SetValueCore(path, text));
    }

    private string? SetValueCore(ManagedValuePath path, string text)
    {
        lock (_gate)
        {
            if (State != DebugState.Stopped)
            {
                return "it is not stopped, so there is nothing to write into";
            }
        }

        string wanted = text.Trim();
        if (wanted.Length >= 2 && wanted[0] == '"' && wanted[^1] == '"')
        {
            // The string has to exist in the debuggee before a reference can point at it, and making
            // it is an evaluation. It is made first, outside any frame, then written.
            var made = NewString(wanted[1..^1]);
            if (made.Problem is { } why)
            {
                return why;
            }

            ulong address = made.Address;
            return Answer(Resolved<string>(path, (value, _) =>
                Com.Borrow<ICorDebugReferenceValue, string>(value, r => r.SetValue(address) < 0 ? "the runtime refused it" : string.Empty)
                ?? "that is not a reference, so a string cannot be written into it"));
        }

        return Answer(Resolved<string>(path, (value, _) => ManagedVariables.Set(value, wanted, _types) ?? string.Empty));
    }

    /// <summary>Empty from the walk means it was written; null means the value was not found.</summary>
    private static string? Answer(string? outcome)
        => outcome is null ? "that value is no longer there to write into" : outcome.Length == 0 ? null : outcome;

    /// <summary>Makes a string in the debuggee and answers with where it is.</summary>
    private (ulong Address, string? Problem) NewString(string text)
    {
        ICorDebugThread? thread;
        ICorDebugProcess? process;
        lock (_gate)
        {
            thread = _selected ?? _stopped;
            process = _process;
        }

        if (thread is null || process is null || thread.CreateEval(out IntPtr evalPtr) < 0 || evalPtr == IntPtr.Zero)
        {
            return (0, "a string could not be made here");
        }

        var eval = Com.Keep<ICorDebugEval>(evalPtr);
        Marshal.Release(evalPtr);
        if (eval is null)
        {
            return (0, "a string could not be made here");
        }

        try
        {
            if (eval.NewString(text) < 0)
            {
                return (0, "a string could not be made here");
            }

            if (RunEval(process, thread, eval) is { } problem)
            {
                return (0, problem);
            }

            if (_evalFailed || eval.GetResult(out IntPtr result) < 0 || result == IntPtr.Zero)
            {
                return (0, "a string could not be made here");
            }

            try
            {
                ulong address = Com.Borrow<ICorDebugReferenceValue, ulong?>(result, r => r.GetValue(out ulong at) == 0 ? at : null) ?? 0;
                return address == 0 ? (0, "a string could not be made here") : (address, null);
            }
            finally
            {
                Marshal.Release(result);
            }
        }
        finally
        {
            Com.Drop(eval);
        }
    }

    /// <summary>Runs a read against the innermost frame of the thread being looked at, while stopped.</summary>
    private T? InFrame<T>(Func<ICorDebugILFrame, T?> read)
        where T : class
    {
        ICorDebugThread? thread;
        int frameIndex;
        lock (_gate)
        {
            thread = State == DebugState.Stopped ? _selected ?? _stopped : null;
            frameIndex = _frame;
        }

        if (thread is null)
        {
            return null;
        }

        // The innermost managed frame by default, or the one picked in the call stack. Which frame
        // this is decides whose locals are read, so the pane and the arrow move together.
        IntPtr frame = frameIndex < 0 ? ManagedStack.FirstIlFrame(thread) : ManagedStack.IlFrameAt(thread, frameIndex);
        if (frame == IntPtr.Zero)
        {
            return null;
        }

        return Com.Owned<ICorDebugILFrame, T>(frame, read);
    }

    /// <summary>The frame's method as its metadata declares it, and the declared types of its locals.</summary>
    private (ManagedMethodShape? Method, IReadOnlyList<string> Locals) Shape(ICorDebugILFrame il)
    {
        if (il.GetFunction(out var function) < 0 || function is null)
        {
            return (null, Array.Empty<string>());
        }

        try
        {
            if (function.GetToken(out uint token) < 0)
            {
                return (null, Array.Empty<string>());
            }

            string? module = function.GetModule(out IntPtr owner) == 0
                ? Com.Owned<ICorDebugModule, string>(owner, m => Com.NameOf(m))
                : null;
            uint signature = function.GetLocalVarSigToken(out uint sig) == 0 ? sig : 0;

            return (_types.Method(module, token), _types.LocalTypes(module, token, signature));
        }
        finally
        {
            Com.Drop(function);
        }
    }

    /// <summary>
    /// What argument slot <paramref name="slot"/> is called. Slot zero of an instance method is
    /// <c>this</c>, and has no row in the metadata's parameter table, so every name after it is one
    /// slot further along than the table's numbering suggests.
    /// </summary>
    private static string ArgumentName(ManagedMethodShape? shape, uint slot)
    {
        string fallback = "A_" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (shape is null)
        {
            return fallback;
        }

        if (!shape.IsStatic)
        {
            if (slot == 0)
            {
                return "this";
            }

            slot--;
        }

        return slot < shape.ParameterNames.Count ? shape.ParameterNames[(int)slot] : fallback;
    }

    private static string ArgumentType(ManagedMethodShape? shape, uint slot)
    {
        if (shape is null)
        {
            return string.Empty;
        }

        if (!shape.IsStatic)
        {
            if (slot == 0)
            {
                return shape.DeclaringType;
            }

            slot--;
        }

        return slot < shape.ParameterTypes.Count ? shape.ParameterTypes[(int)slot] : string.Empty;
    }

    /// <summary>
    /// Every thread the runtime knows about, while the process is stopped.
    ///
    /// Only while stopped, because the answers are about where each thread is, and a running thread
    /// is somewhere else by the time the sentence is finished. A list taken at a stop is what a
    /// Threads window shows, and it is cleared when the process runs again.
    /// </summary>
    public IReadOnlyList<ManagedThread> Threads() => Interop(ThreadsCore);

    /// <summary>More threads than this is an enumeration that is not ending, not a process.</summary>
    private const int MaxThreads = 1024;

    private IReadOnlyList<ManagedThread> ThreadsCore()
    {
        ICorDebugProcess? process;
        ICorDebugThread? stopped;
        ICorDebugThread? selected;
        lock (_gate)
        {
            if (State != DebugState.Stopped || _process is null)
            {
                return Array.Empty<ManagedThread>();
            }

            process = _process;
            stopped = _stopped;
            selected = _selected;
        }

        uint stoppedId = stopped is not null && stopped.GetID(out uint s) == 0 ? s : 0;
        uint lookedId = selected is not null && selected.GetID(out uint l) == 0 ? l : stoppedId;

        if (process.EnumerateThreads(out IntPtr all) < 0 || all == IntPtr.Zero)
        {
            return Array.Empty<ManagedThread>();
        }

        return Com.Owned<ICorDebugThreadEnum, IReadOnlyList<ManagedThread>>(all, threads =>
        {
            var found = new List<ManagedThread>();
            for (int i = 0; i < MaxThreads; i++)
            {
                if (threads.Next(1, out IntPtr one, out uint fetched) < 0 || fetched == 0 || one == IntPtr.Zero)
                {
                    break;
                }

                if (Com.Owned<ICorDebugThread, ManagedThread>(one, thread => Describe(thread, stoppedId, lookedId)) is { } row)
                {
                    found.Add(row);
                }
            }

            return found;
        }) ?? Array.Empty<ManagedThread>();
    }

    private ManagedThread Describe(ICorDebugThread thread, uint stoppedId, uint lookedId)
    {
        uint id = thread.GetID(out uint osId) == 0 ? osId : 0;
        var (managedId, name) = Identity(thread);

        string location = Place(thread) is { } place ? Located(place) : "[not in managed code]";

        // The handle is the runtime's, borrowed for the question: it is not ours to close.
        string priority = thread.GetHandle(out IntPtr handle) == 0 && handle != IntPtr.Zero
            ? PriorityName(Native.GetThreadPriority(handle))
            : string.Empty;

        string domain = thread.GetAppDomain(out IntPtr appDomain) == 0 && appDomain != IntPtr.Zero
            ? Com.Owned<ICorDebugAppDomain, string>(appDomain, DomainName) ?? string.Empty
            : string.Empty;

        int user = thread.GetUserState(out int state) == 0 ? state : -1;

        // ManagedThreadId 1 is the thread the runtime started the program on, in every version of it.
        string category = managedId == 1 ? "Main Thread"
            : user >= 0 && (user & UserThreadPool) != 0 ? "Thread Pool"
            : managedId is null ? "Unknown"
            : "Worker Thread";

        return new ManagedThread(id, managedId, category, name, location, priority, domain, States(user), id == stoppedId, id == lookedId);
    }

    private string Located(Placed place)
    {
        string method = _types.Method(place.ModulePath, place.At.MethodToken)?.Display ?? $"0x{place.At.MethodToken:X8}";
        return $"{place.At.Module}!{method} (IL=0x{place.At.Offset:X4})";
    }

    /// <summary>
    /// A thread's managed id and name, read out of its <c>Thread</c> object's fields.
    ///
    /// The fields are called <c>_managedThreadId</c> and <c>_name</c> on .NET and
    /// <c>m_ManagedThreadId</c> and <c>m_Name</c> on .NET Framework, so they are matched with the
    /// prefix taken off rather than by either spelling.
    /// </summary>
    private (int? ManagedId, string Name) Identity(ICorDebugThread thread)
    {
        if (thread.GetObject(out IntPtr handle) < 0 || handle == IntPtr.Zero)
        {
            return (null, string.Empty);
        }

        try
        {
            int? managedId = null;
            string name = string.Empty;

            foreach (var field in ManagedVariables.Children(handle, ManagedValuePath.OfLocal(0), _types))
            {
                switch (Unprefixed(field.Name))
                {
                    case "managedthreadid" when int.TryParse(
                        field.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed):
                        managedId = parsed;
                        break;

                    case "name" when field.Kind == ManagedValueKind.Text && field.Value.Length >= 2:
                        name = field.Value[1..^1];
                        break;
                }
            }

            return (managedId, name);
        }
        finally
        {
            Marshal.Release(handle);
        }
    }

    private static string Unprefixed(string field)
        => (field.StartsWith("m_", StringComparison.Ordinal) ? field[2..] : field.TrimStart('_')).ToLowerInvariant();

    /// <summary>An app domain as <c>[1] Capture.exe</c>, in the usual two-call shape for its name.</summary>
    private static string? DomainName(ICorDebugAppDomain domain)
    {
        uint id = domain.GetID(out uint did) == 0 ? did : 0;
        if (domain.GetName(0, out uint length, IntPtr.Zero) < 0 || length == 0 || length > 0x8000)
        {
            return $"[{id}]";
        }

        IntPtr buffer = Marshal.AllocCoTaskMem((int)length * sizeof(char));
        try
        {
            string name = domain.GetName(length, out uint written, buffer) == 0
                ? (Marshal.PtrToStringUni(buffer, (int)Math.Min(written, length)) ?? string.Empty).TrimEnd('\0')
                : string.Empty;
            return $"[{id}] {name}";
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    private static string PriorityName(int priority) => priority switch
    {
        -15 => "Idle",
        -2 => "Lowest",
        -1 => "Below Normal",
        0 => "Normal",
        1 => "Above Normal",
        2 => "Highest",
        15 => "Time Critical",
        Native.THREAD_PRIORITY_ERROR_RETURN => string.Empty,
        _ => priority.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    // CorDebugUserState.
    private const int UserStopRequested = 0x01;
    private const int UserSuspendRequested = 0x02;
    private const int UserBackground = 0x04;
    private const int UserUnstarted = 0x08;
    private const int UserStopped = 0x10;
    private const int UserWaitSleepJoin = 0x20;
    private const int UserSuspended = 0x40;
    private const int UserThreadPool = 0x100;

    private static string States(int user)
    {
        if (user < 0)
        {
            return string.Empty;
        }

        var said = new List<string>();
        if ((user & UserBackground) != 0)
        {
            said.Add("Background");
        }

        if ((user & UserUnstarted) != 0)
        {
            said.Add("Unstarted");
        }

        if ((user & UserWaitSleepJoin) != 0)
        {
            said.Add("WaitSleepJoin");
        }

        if ((user & UserSuspended) != 0)
        {
            said.Add("Suspended");
        }

        if ((user & UserSuspendRequested) != 0)
        {
            said.Add("SuspendRequested");
        }

        if ((user & UserStopRequested) != 0)
        {
            said.Add("StopRequested");
        }

        if ((user & UserStopped) != 0)
        {
            said.Add("Stopped");
        }

        return string.Join(", ", said);
    }

    /// <summary>
    /// Looks at a different thread: its values, its place in the code, and the thread a step will
    /// step. The process stays stopped. Null when it worked.
    /// </summary>
    public string? SelectThread(uint threadId) => Interop(() => SelectThreadCore(threadId));

    private string? SelectThreadCore(uint threadId)
    {
        ICorDebugProcess? process;
        ICorDebugThread? stopped;
        lock (_gate)
        {
            if (State != DebugState.Stopped || _process is null)
            {
                return "it is not stopped, so there is no thread to look at";
            }

            process = _process;
            stopped = _stopped;
        }

        if (process.GetThread(threadId, out IntPtr pointer) < 0 || pointer == IntPtr.Zero)
        {
            return $"there is no managed thread {threadId}";
        }

        var chosen = Com.Keep<ICorDebugThread>(pointer);
        Marshal.Release(pointer);
        if (chosen is null)
        {
            return $"thread {threadId} could not be held on to";
        }

        bool isStopped = stopped is not null && stopped.GetID(out uint stoppedId) == 0 && stoppedId == threadId;
        var at = WhereOf(chosen);

        ICorDebugThread? previous;
        lock (_gate)
        {
            previous = _selected;
            _selected = isStopped ? null : chosen;
            _frame = -1;
            StoppedAt = at;
            Stops++;
        }

        Com.Drop(previous);
        if (isStopped)
        {
            Com.Drop(chosen);
        }

        // A stepper made on the thread looked at before would complete there, not here.
        Retire();

        Note(at is null
            ? $"looking at thread {threadId}, which is not in managed code"
            : $"looking at thread {threadId}, at {at}");
        return null;
    }

    /// <summary>
    /// Reads the stopped thread's innermost frame.
    ///
    /// The frame has to be asked while the debuggee is stopped — this is called from inside the
    /// callback for that reason — because the moment it continues there is no frame to read and the
    /// answer would be about a program that has moved on.
    /// </summary>
    /// <summary>
    /// Reads a property by running its getter in the debuggee.
    ///
    /// The one operation here that runs the program's own code, so it is the most fenced. It happens
    /// only when a person opens the object, never on its own; every other thread is suspended for the
    /// duration so nothing else moves; there is a timeout, and a getter still running at the end of it
    /// is aborted. A getter that throws reports what it threw rather than the value it never returned.
    ///
    /// Returns the value as a row, or a row whose value says why it could not be read. Null only when
    /// nothing is stopped.
    /// </summary>
    public ManagedVariable? EvaluateProperty(ManagedValuePath objectPath, ManagedPropertyGetter getter, string name, string declaredType)
    {
        ArgumentNullException.ThrowIfNull(objectPath);
        ArgumentNullException.ThrowIfNull(getter);

        return Interop(() => EvaluatePropertyCore(objectPath, getter, name, declaredType));
    }

    /// <summary>How long to let a getter run before deciding it is not going to return.</summary>
    private static readonly TimeSpan EvalPatience = TimeSpan.FromSeconds(3);

    private ManagedVariable? EvaluatePropertyCore(ManagedValuePath objectPath, ManagedPropertyGetter getter, string name, string declaredType)
    {
        ICorDebugThread? thread;
        ICorDebugProcess? process;
        ICorDebugModule? module;
        lock (_gate)
        {
            if (State != DebugState.Stopped)
            {
                return null;
            }

            thread = _selected ?? _stopped;
            process = _process;
            _loaded.TryGetValue(getter.Module, out module);
        }

        if (thread is null || process is null)
        {
            return null;
        }

        ManagedVariable Note(string why) => new(name, why, declaredType, ManagedValueKind.Property, false, objectPath, getter);

        if (module is null || module.GetFunctionFromToken(getter.Token, out var function) < 0 || function is null)
        {
            return Note("(getter not found)");
        }

        try
        {
            if (thread.CreateEval(out IntPtr evalPtr) < 0 || evalPtr == IntPtr.Zero)
            {
                return Note("(evaluation unavailable)");
            }

            var eval = Com.Keep<ICorDebugEval>(evalPtr);
            Marshal.Release(evalPtr);
            if (eval is null)
            {
                return Note("(evaluation unavailable)");
            }

            try
            {
                // The getter is armed while the target is in hand — which for an instance getter
                // means walking the path to it first — and the frame is let go before the process
                // runs, which is the one place a frame pointer must not still be held.
                object? outcome = getter.IsStatic
                    ? Arm(eval, function, IntPtr.Zero, getter)
                    : Resolved<object>(objectPath, (target, _) => Arm(eval, function, target, getter));
                int armed = outcome is int hr ? hr : int.MinValue;

                if (armed == int.MinValue)
                {
                    return Note("(no target)");
                }

                if (armed < 0)
                {
                    // The usual refusal is a getter on a generic type, which needs the type arguments
                    // passed too — CallParameterizedFunction, not built here yet.
                    return Note("(cannot evaluate)");
                }

                return Ran(process, thread, eval, name, declaredType, objectPath, getter);
            }
            finally
            {
                Com.Drop(eval);
            }
        }
        finally
        {
            Com.Drop(function);
        }
    }

    /// <summary>
    /// Runs an armed evaluation to completion: suspends the other threads, continues, waits, and
    /// reads the result — or aborts a getter that overstays and says so.
    /// </summary>
    private ManagedVariable Ran(
        ICorDebugProcess process,
        ICorDebugThread thread,
        ICorDebugEval eval,
        string name,
        string declaredType,
        ManagedValuePath objectPath,
        ManagedPropertyGetter getter)
    {
        ManagedVariable Note(string why) => new(name, why, declaredType, ManagedValueKind.Property, false, objectPath, getter);

        if (RunEval(process, thread, eval) is { } stopped)
        {
            return Note(stopped);
        }

        if (_evalFailed)
        {
            return eval.GetResult(out IntPtr thrown) == 0 && thrown != IntPtr.Zero
                ? Present(thrown, name, declaredType, objectPath, "threw ")
                : Note("(threw)");
        }

        return eval.GetResult(out IntPtr result) == 0 && result != IntPtr.Zero
            ? Present(result, name, declaredType, objectPath, string.Empty)
            : Note("(no value)");

        ManagedVariable Present(IntPtr value, string rowName, string declared, ManagedValuePath path, string prefix)
        {
            try
            {
                var read = ManagedVariables.Present(value, rowName, declared, path, _types);

                // An object a getter returned can be opened like any other — but only if it is kept.
                // It is reachable from no frame, so a handle on it is the only way back to it once
                // this returns, and the path it is given names that handle.
                if (prefix.Length == 0 && read.Expandable && Keep(value) is var id && id != 0)
                {
                    return read with { Path = ManagedValuePath.OfEvaluated(id), Getter = getter, CanSet = false };
                }

                return read with
                {
                    Value = prefix + read.Value,
                    Expandable = false,
                    Getter = getter,
                    Kind = prefix.Length > 0 ? ManagedValueKind.Property : read.Kind,
                };
            }
            finally
            {
                Marshal.Release(value);
            }
        }
    }

    /// <summary>
    /// Runs an armed evaluation and waits for it: every other thread suspended, the process
    /// continued, and a getter that overstays aborted — politely, then not. Null when it came back.
    /// </summary>
    private string? RunEval(ICorDebugProcess process, ICorDebugThread thread, ICorDebugEval eval)
    {
        lock (_gate)
        {
            _evaluating = true;
            _evalFailed = false;
            _evalDone.Reset();
        }

        // Only the evaluating thread runs. Otherwise the getter's work could be raced by the rest of
        // the program, which is running real code the analyst did not step to.
        IntPtr except = Marshal.GetIUnknownForObject(thread);
        try
        {
            _ = process.SetAllThreadsDebugState(ThreadSuspend, except);
        }
        finally
        {
            Marshal.Release(except);
        }

        _ = thread.SetDebugState(ThreadRun);

        try
        {
            if (process.Continue(0) < 0)
            {
                return "(could not run it)";
            }

            if (_evalDone.Wait(EvalPatience))
            {
                return null;
            }

            // Still running. Ask it to stop and wait again — the process is running, and nothing can
            // be read until it comes back to rest.
            try
            {
                _ = eval.Abort();
            }
            catch (COMException)
            {
            }

            if (_evalDone.Wait(EvalPatience))
            {
                return "(timed out)";
            }

            if (eval is ICorDebugEval2 rude)
            {
                try
                {
                    _ = rude.RudeAbort();
                }
                catch (COMException)
                {
                }

                if (_evalDone.Wait(EvalPatience))
                {
                    return "(timed out)";
                }
            }

            return "(timed out, and would not stop)";
        }
        finally
        {
            lock (_gate)
            {
                _evaluating = false;
            }

            _ = process.SetAllThreadsDebugState(ThreadRun, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Arms a getter: the target as its one argument, and the type arguments its declaring type was
    /// instantiated with. Boxed, because it is used through <see cref="Resolved{T}"/>.
    ///
    /// Always the parameterized call when the runtime offers it. A method on a generic type cannot be
    /// called without the type arguments, and for a type that has none it is the same call.
    /// </summary>
    private object Arm(ICorDebugEval eval, ICorDebugFunction function, IntPtr target, ManagedPropertyGetter getter)
    {
        var arguments = target == IntPtr.Zero ? new List<IntPtr>() : TypeArgumentsFor(target, getter);
        IntPtr types = IntPtr.Zero;
        IntPtr args = IntPtr.Zero;

        try
        {
            uint count = target == IntPtr.Zero ? 0u : 1u;
            if (count == 1)
            {
                args = Marshal.AllocCoTaskMem(IntPtr.Size);
                Marshal.WriteIntPtr(args, target);
            }

            if (arguments.Count > 0)
            {
                types = Marshal.AllocCoTaskMem(IntPtr.Size * arguments.Count);
                for (int i = 0; i < arguments.Count; i++)
                {
                    Marshal.WriteIntPtr(types, i * IntPtr.Size, arguments[i]);
                }
            }

            return eval is ICorDebugEval2 parameterized
                ? parameterized.CallParameterizedFunction(function, (uint)arguments.Count, types, count, args)
                : eval.CallFunction(function, count, args);
        }
        finally
        {
            if (args != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(args);
            }

            if (types != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(types);
            }

            foreach (IntPtr argument in arguments)
            {
                Marshal.Release(argument);
            }
        }
    }

    /// <summary>
    /// The type arguments of the class that declares the getter — <c>int</c> for a property of a
    /// <c>List&lt;int&gt;</c>. Empty for a type that is not generic, which is the ordinary case.
    /// </summary>
    private static List<IntPtr> TypeArgumentsFor(IntPtr target, ManagedPropertyGetter getter)
    {
        IntPtr held = Com.Borrow<ICorDebugReferenceValue, IntPtr?>(target, r =>
            r.IsNull(out int isNull) == 0 && isNull == 0 && r.Dereference(out IntPtr pointed) == 0 ? pointed : null) ?? IntPtr.Zero;
        if (held == IntPtr.Zero)
        {
            return new List<IntPtr>();
        }

        var chain = ManagedVariables.ChainOf(held);
        try
        {
            foreach (var link in chain)
            {
                if (link.Token == getter.Class
                    && string.Equals(System.IO.Path.GetFileName(link.Module ?? string.Empty), getter.Module, StringComparison.OrdinalIgnoreCase))
                {
                    return ManagedVariables.TypeArguments(link);
                }
            }

            return new List<IntPtr>();
        }
        finally
        {
            ManagedVariables.Release(chain);
            Marshal.Release(held);
        }
    }

    /// <summary>CorDebugThreadState: run every thread, or suspend it.</summary>
    private const int ThreadRun = 0;
    private const int ThreadSuspend = 1;


    /// <summary>
    /// The call stack of the thread being looked at, innermost first. Empty unless stopped.
    ///
    /// Only while stopped, and for the same reason the values are: a running thread's stack is a
    /// description of somewhere it has already left. The frame marked current is the one whose
    /// locals the pane is showing.
    /// </summary>
    public IReadOnlyList<ManagedFrame> Frames() => Interop(FramesCore);

    private IReadOnlyList<ManagedFrame> FramesCore()
    {
        ICorDebugThread? thread;
        uint token;
        uint offset;
        int chosen;
        lock (_gate)
        {
            thread = State == DebugState.Stopped ? _selected ?? _stopped : null;
            token = StoppedAt?.MethodToken ?? 0;
            offset = StoppedAt?.Offset ?? 0;
            chosen = _frame;
        }

        if (thread is null)
        {
            return Array.Empty<ManagedFrame>();
        }

        var frames = ManagedStack.Frames(thread, _types, token, offset);

        // The current marker follows the picked frame when one was picked, and otherwise the first
        // managed frame — the one the stack walk already marked.
        if (chosen >= 0)
        {
            var moved = new List<ManagedFrame>(frames.Count);
            foreach (var frame in frames)
            {
                moved.Add(frame with { IsCurrent = frame.Index == chosen });
            }

            return moved;
        }

        return frames;
    }

    /// <summary>
    /// Looks at a different frame of the same thread: its locals, and the arrow. The process stays
    /// stopped. Null when it worked; a message when the frame has no IL to read.
    /// </summary>
    public string? SelectFrame(int index) => Interop(() => SelectFrameCore(index));

    private string? SelectFrameCore(int index)
    {
        ICorDebugThread? thread;
        lock (_gate)
        {
            thread = State == DebugState.Stopped ? _selected ?? _stopped : null;
        }

        if (thread is null)
        {
            return "it is not stopped, so there is no frame to look at";
        }

        IntPtr frame = ManagedStack.IlFrameAt(thread, index);
        if (frame == IntPtr.Zero)
        {
            return "that frame has no IL to read";
        }

        var at = Com.Owned<ICorDebugILFrame, ManagedLocation>(frame, WhereOfFrame);

        ICorDebugStepper? retire;
        lock (_gate)
        {
            _frame = index;
            StoppedAt = at;
            retire = _stepper;
            _stepper = null;
        }

        // A stepper made against the frame looked at before would complete there, not here.
        if (retire is not null)
        {
            try
            {
                _ = retire.Deactivate();
            }
            catch (COMException)
            {
            }

            Com.Drop(retire);
        }

        Note(at is null ? $"looking at frame {index}" : $"looking at frame {index}, at {at}");
        return null;
    }

    /// <summary>Where an IL frame is, as a method and an offset. The per-frame half of <see cref="Place"/>.</summary>
    private static ManagedLocation? WhereOfFrame(ICorDebugILFrame il)
    {
        if (il.GetIP(out uint offset, out int mapping) < 0 || il.GetFunction(out var function) < 0 || function is null)
        {
            return null;
        }

        try
        {
            if (function.GetToken(out uint token) < 0)
            {
                return null;
            }

            string? module = function.GetModule(out IntPtr owner) == 0
                ? Com.Owned<ICorDebugModule, string>(owner, m => Com.NameOf(m))
                : null;

            return new ManagedLocation(
                module is null ? "(unknown)" : System.IO.Path.GetFileName(module), token, offset, Mapped(mapping));
        }
        finally
        {
            Com.Drop(function);
        }
    }

    private static ManagedLocation? Where(IntPtr thread)
        => Com.Borrow<ICorDebugThread, ManagedLocation>(thread, WhereOf);

    /// <summary>Where a thread is, as a method and an IL offset. Null when it is not in managed code.</summary>
    private static ManagedLocation? WhereOf(ICorDebugThread running) => Place(running)?.At;

    /// <summary>A place in the code, and the path of the module it is in.</summary>
    private sealed record Placed(ManagedLocation At, string? ModulePath);

    /// <summary>Where a thread is, with the path of its module, which names are read out of.</summary>
    private static Placed? Place(ICorDebugThread running)
    {
        IntPtr frame = ManagedStack.FirstIlFrame(running);
        if (frame == IntPtr.Zero)
        {
            return null;   // a thread in no managed code at all, which is not a fault
        }

        return Com.Owned<ICorDebugILFrame, Placed>(frame, il =>
        {
            if (il.GetIP(out uint offset, out int mapping) < 0)
            {
                return null;
            }

            if (il.GetFunction(out var function) < 0 || function is null)
            {
                return null;
            }

            try
            {
                if (function.GetToken(out uint token) < 0)
                {
                    return null;
                }

                string? module = function.GetModule(out IntPtr owner) == 0
                    ? Com.Owned<ICorDebugModule, string>(owner, m => Com.NameOf(m))
                    : null;

                return new Placed(
                    new ManagedLocation(
                        module is null ? "(unknown)" : System.IO.Path.GetFileName(module),
                    token,
                    offset,
                    Mapped(mapping)),
                    module);
            }
            finally
            {
                Com.Drop(function);
            }
        });
    }

    /// <summary>
    /// Whether the IL offset is exact.
    ///
    /// It is not always. A frame stopped in code the JIT reordered, or in a prologue, maps to an
    /// approximate offset or to none at all, and a listing that highlighted that line as though it
    /// were the current one would be confidently wrong. The word travels with the number.
    ///
    /// <c>CorDebugMappingResult</c> is a flag set rather than a list — 0x10 is exact, not 0x00 — and
    /// reading it as one made every ordinary stop report "unmapped". The number was right each time,
    /// so the only sign was the word beside it warning the reader off a line the arrow was correctly
    /// sitting on. A combination is reported as itself rather than guessed at.
    /// </summary>
    private static string Mapped(int mapping) => mapping switch
    {
        Exact => "exact",
        Approximate => "approximate",
        Prolog => "prologue",
        Epilog => "epilogue",
        NoInfo => "no mapping",
        UnmappedAddress => "unmapped address",
        _ => $"mapping 0x{mapping:X}",
    };

    private const int Prolog = 0x01;
    private const int Epilog = 0x02;
    private const int NoInfo = 0x04;
    private const int UnmappedAddress = 0x08;
    private const int Exact = 0x10;
    private const int Approximate = 0x20;

    void IManagedEvents.Exited() => Exited();

    void IManagedEvents.Failed(string problem)
    {
        Note(problem);
        Exited();
    }

    void IManagedEvents.Note(string text) => Note(text);

    private void Exited()
    {
        lock (_gate)
        {
            if (State == DebugState.Exited)
            {
                return;
            }

            State = DebugState.Exited;
            Status = "it has exited";
            StoppedBy = ManagedStopKind.Exited;
        }

        Note("the process exited");
        _settled.Set();
    }

    private void Note(string text)
    {
        var reported = new DebugEvent("managed", text);
        _log.Enqueue(reported);
        while (_log.Count > Remembered && _log.TryDequeue(out _))
        {
            // keeping only the tail
        }

        Reported?.Invoke(this, reported);
    }

    private void Release() => Interop(ReleaseCore);

    private void ReleaseCore()
    {
        if (_unregister != IntPtr.Zero)
        {
            _ = DbgShim.UnregisterForRuntimeStartup(_unregister);
            _unregister = IntPtr.Zero;
        }

        // Everything taken during the run, given back in the order it was taken. Breakpoints and
        // modules are held deliberately — a breakpoint released is a breakpoint removed, and a
        // module is what later breakpoints are planted into — so they are the session's to drop,
        // and dropping them is what lets the debugging interface shut down rather than hang on to a
        // process nobody is watching.
        lock (_gate)
        {
            foreach (var breakpoint in _planted)
            {
                Marshal.Release(breakpoint.Pointer);
            }

            _planted.Clear();

            foreach (var module in _loaded.Values)
            {
                Com.Drop(module);
            }

            _loaded.Clear();
            DisposeHandles();

            Com.Drop(_stopped);
            _stopped = null;

            Com.Drop(_selected);
            _selected = null;
            _frame = -1;

            Com.Drop(_stepper);
            _stepper = null;
        }

        if (_process is not null)
        {
            Marshal.ReleaseComObject(_process);
            _process = null;
        }

        if (_debug is not null)
        {
            try
            {
                _debug.Terminate();
            }
            catch (COMException)
            {
                // Nothing useful follows from a failure to shut down an interface we are dropping.
            }

            Marshal.ReleaseComObject(_debug);
            _debug = null;
        }

        if (_launched != IntPtr.Zero)
        {
            _ = Native.CloseHandle(_launched);
            _launched = IntPtr.Zero;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }

        _callback = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        Release();
        _attached.Dispose();
        _settled.Dispose();
        _evalDone.Dispose();
        _ready.Dispose();
        _types.Dispose();
    }
}
