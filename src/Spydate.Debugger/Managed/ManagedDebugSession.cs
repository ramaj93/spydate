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

    private GCHandle _self;
    private IntPtr _unregister;
    private ICorDebug? _debug;
    private ICorDebugProcess? _process;
    private ManagedCallback? _callback;
    private string? _startupProblem;
    private uint _pid;
    private bool _holdAtStart;
    private bool _disposed;

    /// <summary>How many events to keep. Enough to explain a stop, not a transcript of the run.</summary>
    private const int Remembered = 60;

    public event EventHandler<DebugEvent>? Reported;

    public DebugState State { get; private set; } = DebugState.NotStarted;

    /// <summary>A line saying what it is doing, in the same shape the panel shows for a native run.</summary>
    public string Status { get; private set; } = "nothing is running";

    /// <summary>The operating system id of the debuggee, once it exists.</summary>
    public uint ProcessId { get; private set; }

    /// <summary>Why the last stop happened.</summary>
    public ManagedStopKind StoppedBy { get; private set; }

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
    private readonly List<IntPtr> _planted = new();

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

        string command = arguments is { Length: > 0 } ? $"\"{path}\" {arguments}" : $"\"{path}\"";
        _holdAtStart = holdAtStart;
        _callback = new ManagedCallback(this);
        _self = GCHandle.Alloc(this);
        _startupProblem = null;
        _ready.Reset();
        _attached.Reset();
        _settled.Reset();

        int hr = DbgShim.CreateProcessForLaunch(command, true, IntPtr.Zero, workingDirectory, out uint pid, out IntPtr resume);
        _pid = pid;
        if (hr < 0)
        {
            Release();
            return $"could not launch it: 0x{hr:X8}";
        }

        unsafe
        {
            hr = DbgShim.RegisterForRuntimeStartup(pid, &Started, GCHandle.ToIntPtr(_self), out _unregister);
        }

        if (hr < 0)
        {
            _ = DbgShim.CloseResumeHandle(resume);
            Release();
            return $"could not register for the runtime starting: 0x{hr:X8}";
        }

        _ = DbgShim.ResumeProcess(resume);
        _ = DbgShim.CloseResumeHandle(resume);

        Note($"launched {System.IO.Path.GetFileName(path)} as process {pid}, waiting for its runtime");

        var patience = timeout == default ? TimeSpan.FromSeconds(30) : timeout;
        if (!_ready.Wait(patience))
        {
            Release();
            return $"its runtime did not become debuggable within {patience.TotalSeconds:0}s. "
                   + "A program that is not .NET, or one that exits before the runtime starts, looks like this.";
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
                _planted.Add(breakpoint);
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

    /// <summary>Lets it run on. Does nothing unless it is stopped.</summary>
    public void Continue()
    {
        lock (_gate)
        {
            if (State != DebugState.Stopped || _process is null)
            {
                return;
            }

            State = DebugState.Running;
            Status = "running";
            StoppedBy = ManagedStopKind.None;
            _settled.Reset();
        }

        int hr = _process!.Continue(0);
        if (hr < 0)
        {
            Note($"could not continue: 0x{hr:X8}");
        }
    }

    /// <summary>Ends the process and the session with it.</summary>
    public void Stop()
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
            _process?.Terminate(0);
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
        finally
        {
            _attached.Set();
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
        // never break on a library that loads later — which is most of them.
        var pending = Waiting(name);
        if (pending.Count == 0)
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

            keeper.Continue(0);
            Com.Drop(keeper);
        });

        return true;
    }

    bool IManagedEvents.Stopped(ManagedStopKind kind, IntPtr controller, IntPtr thread, string text)
    {
        var at = Where(thread);

        lock (_gate)
        {
            State = DebugState.Stopped;
            StoppedBy = kind;
            StoppedAt = at;
            Status = at is null ? text : $"{text} at {at}";
        }

        Note(Status);
        _settled.Set();
        return true;
    }

    /// <summary>Where the program is, once it has stopped: a method and an offset into its IL.</summary>
    public ManagedLocation? StoppedAt { get; private set; }

    /// <summary>
    /// Reads the stopped thread's innermost frame.
    ///
    /// The frame has to be asked while the debuggee is stopped — this is called from inside the
    /// callback for that reason — because the moment it continues there is no frame to read and the
    /// answer would be about a program that has moved on.
    /// </summary>
    private static ManagedLocation? Where(IntPtr thread)
        => Com.Borrow<ICorDebugThread, ManagedLocation>(thread, running =>
        {
            if (running.GetActiveFrame(out IntPtr frame) < 0 || frame == IntPtr.Zero)
            {
                return null;   // a thread in native code has no managed frame, which is not a fault
            }

            return Com.Owned<ICorDebugILFrame, ManagedLocation>(frame, il =>
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

                    return new ManagedLocation(
                        module is null ? "(unknown)" : System.IO.Path.GetFileName(module),
                        token,
                        offset,
                        Mapped(mapping));
                }
                finally
                {
                    Com.Drop(function);
                }
            });
        });

    /// <summary>
    /// Whether the IL offset is exact.
    ///
    /// It is not always. A frame stopped in code the JIT reordered, or in a prologue, maps to an
    /// approximate offset or to none at all, and a listing that highlighted that line as though it
    /// were the current one would be confidently wrong. The word travels with the number.
    /// </summary>
    private static string Mapped(int mapping) => mapping switch
    {
        0 => "exact",
        1 => "approximate",
        2 => "prologue",
        3 => "epilogue",
        4 => "no mapping",
        _ => "unmapped",
    };

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

    private void Release()
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
            foreach (IntPtr breakpoint in _planted)
            {
                Marshal.Release(breakpoint);
            }

            _planted.Clear();

            foreach (var module in _loaded.Values)
            {
                Com.Drop(module);
            }

            _loaded.Clear();
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
        _ready.Dispose();
    }
}
