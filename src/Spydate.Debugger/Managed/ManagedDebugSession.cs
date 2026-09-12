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

    /// <summary>The last things that happened, oldest first.</summary>
    public IReadOnlyList<string> Recent => _log.Select(e => e.Text).ToList();

    /// <summary>
    /// Launches the program and waits for its runtime to become debuggable. Null on success.
    ///
    /// Launched suspended and resumed only once the debugger is registered. Doing it the other way
    /// round is a race that is usually won and occasionally lost, and losing it means the runtime
    /// starts, publishes nothing, and the debugger waits for an event that has already gone past.
    /// </summary>
    public string? Start(string path, string? arguments = null, string? workingDirectory = null, TimeSpan timeout = default)
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
            State = DebugState.Running;
            Status = "running";
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

    void IManagedEvents.Created(IntPtr process)
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
    }

    void IManagedEvents.ModuleLoaded(IntPtr module)
    {
        // Named, not held. A module's name is worth having in the log; the object itself belongs to
        // the app domain that loaded it and outliving that is how a debugger keeps dead memory alive.
        lock (_gate)
        {
            _modules.Add($"module {_modules.Count + 1}");
        }
    }

    bool IManagedEvents.Stopped(ManagedStopKind kind, IntPtr controller, IntPtr thread, string text)
    {
        lock (_gate)
        {
            State = DebugState.Stopped;
            StoppedBy = kind;
            Status = text;
        }

        Note(text);
        _settled.Set();
        return true;
    }

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
