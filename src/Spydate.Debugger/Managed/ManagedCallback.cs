using System.Runtime.InteropServices;

namespace Spydate.Debugger.Managed;

/// <summary>What the runtime reported, reduced to the few kinds a debugger acts on.</summary>
public enum ManagedStopKind
{
    None,
    Created,
    Breakpoint,
    Step,
    Break,
    Exception,
    Exited,
    Error,
}

/// <summary>
/// The object the runtime calls back on.
///
/// Every method here ends one of two ways and there is no third. Either it continues the debuggee,
/// or it hands the stop to the session and the debuggee stays where it is until something else
/// continues it. Forgetting is not a bug that degrades: the process never runs again, with no error
/// anywhere, and the only symptom is that nothing happens.
///
/// It is a separate object from the session because the runtime holds a reference to it for the
/// lifetime of the debugging interface, and the session's own lifetime is shorter and messier.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ManagedCallback : ICorDebugManagedCallback, ICorDebugManagedCallback2, ICorDebugManagedCallback3
{
    private readonly IManagedEvents _events;

    internal ManagedCallback(IManagedEvents events) => _events = events;

    /// <summary>
    /// Lets the debuggee run on.
    ///
    /// The pointer handed to a callback is already an <c>ICorDebugController</c> — both an app
    /// domain and a process are one — so this is a cast rather than a lookup. The wrapper is
    /// released straight away: the pointer is borrowed for the length of the call, and holding a
    /// reference past it is how a debugger ends up keeping a dead app domain alive.
    /// </summary>
    private static int Go(IntPtr controller)
    {
        if (controller == IntPtr.Zero)
        {
            return 0;
        }

        object? wrapper = null;
        try
        {
            wrapper = Marshal.GetObjectForIUnknown(controller);
            return wrapper is ICorDebugController control ? control.Continue(0) : 0;
        }
        catch (Exception ex) when (ex is InvalidCastException or COMException)
        {
            return 0;
        }
        finally
        {
            if (wrapper is not null)
            {
                Marshal.ReleaseComObject(wrapper);
            }
        }
    }

    /// <summary>Hands a stop to the session, and continues only if it does not want it.</summary>
    private int Stop(ManagedStopKind kind, IntPtr controller, IntPtr thread, string text)
        => _events.Stopped(kind, controller, thread, text) ? 0 : Go(controller);

    // ------------------------------------------------------------------
    // The stops that matter
    // ------------------------------------------------------------------

    public int Breakpoint(IntPtr pAppDomain, IntPtr pThread, IntPtr pBreakpoint)
        => Stop(ManagedStopKind.Breakpoint, pAppDomain, pThread, "breakpoint");

    public int StepComplete(IntPtr pAppDomain, IntPtr pThread, IntPtr pStepper, int reason)
        => Stop(ManagedStopKind.Step, pAppDomain, pThread, "step complete");

    public int Break(IntPtr pAppDomain, IntPtr thread)
        => Stop(ManagedStopKind.Break, pAppDomain, thread, "Debugger.Break()");

    public int Exception(IntPtr pAppDomain, IntPtr pThread, int unhandled)
        => unhandled != 0
            ? Stop(ManagedStopKind.Exception, pAppDomain, pThread, "unhandled exception")
            : Continued(pAppDomain, "exception (handled)");

    public int CreateProcess(IntPtr pProcess)
    {
        _events.Created(pProcess);
        return Go(pProcess);
    }

    public int ExitProcess(IntPtr pProcess)
    {
        // Nothing is continued here. The process is gone, and calling Continue on it is an error
        // that presents as a failing HRESULT nobody reads.
        _events.Exited();
        return 0;
    }

    public int DebuggerError(IntPtr pProcess, int errorHR, uint errorCode)
    {
        _events.Failed($"the runtime reported a debugger error: 0x{errorHR:X8} (code {errorCode})");
        return 0;
    }

    // ------------------------------------------------------------------
    // Reported, then continued
    // ------------------------------------------------------------------

    public int CreateAppDomain(IntPtr pProcess, IntPtr pAppDomain)
    {
        // Attaching to the app domain is what makes its modules and breakpoints reachable. Older
        // runtimes required it; newer ones attach on their own and answer harmlessly.
        object? wrapper = null;
        try
        {
            wrapper = Marshal.GetObjectForIUnknown(pAppDomain);
            if (wrapper is ICorDebugAppDomain domain)
            {
                domain.Attach();
            }
        }
        catch (Exception ex) when (ex is InvalidCastException or COMException)
        {
            // An app domain that will not attach is reported by whatever later needs it.
        }
        finally
        {
            if (wrapper is not null)
            {
                Marshal.ReleaseComObject(wrapper);
            }
        }

        return Continued(pProcess, "app domain created");
    }

    public int LoadModule(IntPtr pAppDomain, IntPtr pModule)
    {
        _events.ModuleLoaded(pModule);
        return Go(pAppDomain);
    }

    public int LoadAssembly(IntPtr pAppDomain, IntPtr pAssembly) => Continued(pAppDomain, null);

    public int CreateThread(IntPtr pAppDomain, IntPtr thread) => Continued(pAppDomain, null);

    public int ExitThread(IntPtr pAppDomain, IntPtr thread) => Continued(pAppDomain, null);

    public int UnloadModule(IntPtr pAppDomain, IntPtr pModule) => Continued(pAppDomain, null);

    public int UnloadAssembly(IntPtr pAppDomain, IntPtr pAssembly) => Continued(pAppDomain, null);

    public int ExitAppDomain(IntPtr pProcess, IntPtr pAppDomain) => Continued(pProcess, null);

    public int LoadClass(IntPtr pAppDomain, IntPtr c) => Continued(pAppDomain, null);

    public int UnloadClass(IntPtr pAppDomain, IntPtr c) => Continued(pAppDomain, null);

    public int EvalComplete(IntPtr pAppDomain, IntPtr pThread, IntPtr pEval) => Continued(pAppDomain, null);

    public int EvalException(IntPtr pAppDomain, IntPtr pThread, IntPtr pEval) => Continued(pAppDomain, null);

    public int LogMessage(IntPtr pAppDomain, IntPtr pThread, int lLevel, IntPtr pLogSwitchName, IntPtr pMessage)
        => Continued(pAppDomain, null);

    public int LogSwitch(IntPtr pAppDomain, IntPtr pThread, int lLevel, uint ulReason, IntPtr pLogSwitchName, IntPtr pParentName)
        => Continued(pAppDomain, null);

    public int ControlCTrap(IntPtr pProcess) => Continued(pProcess, null);

    public int NameChange(IntPtr pAppDomain, IntPtr pThread) => Continued(pAppDomain, null);

    public int UpdateModuleSymbols(IntPtr pAppDomain, IntPtr pModule, IntPtr pSymbolStream) => Continued(pAppDomain, null);

    public int EditAndContinueRemap(IntPtr pAppDomain, IntPtr pThread, IntPtr pFunction, int fAccurate)
        => Continued(pAppDomain, null);

    public int BreakpointSetError(IntPtr pAppDomain, IntPtr pThread, IntPtr pBreakpoint, uint dwError)
    {
        _events.Note($"a breakpoint could not be set: error {dwError}");
        return Go(pAppDomain);
    }

    // ------------------------------------------------------------------
    // ICorDebugManagedCallback2 and 3
    // ------------------------------------------------------------------

    public int FunctionRemapOpportunity(IntPtr pAppDomain, IntPtr pThread, IntPtr pOldFunction, IntPtr pNewFunction, uint oldILOffset)
        => Continued(pAppDomain, null);

    public int CreateConnection(IntPtr pProcess, uint dwConnectionId, IntPtr pConnName) => Continued(pProcess, null);

    public int ChangeConnection(IntPtr pProcess, uint dwConnectionId) => Continued(pProcess, null);

    public int DestroyConnection(IntPtr pProcess, uint dwConnectionId) => Continued(pProcess, null);

    public int Exception(IntPtr pAppDomain, IntPtr pThread, IntPtr pFrame, uint nOffset, int dwEventType, uint dwFlags)
    {
        // The richer form of the same event. Only the unhandled case stops, on the same reasoning as
        // the first-generation one: stopping on every caught exception in a program that uses them
        // for control flow is a debugger that never gets anywhere.
        const int Unhandled = 2;
        return dwEventType == Unhandled
            ? Stop(ManagedStopKind.Exception, pAppDomain, pThread, "unhandled exception")
            : Go(pAppDomain);
    }

    public int ExceptionUnwind(IntPtr pAppDomain, IntPtr pThread, int dwEventType, uint dwFlags) => Continued(pAppDomain, null);

    public int FunctionRemapComplete(IntPtr pAppDomain, IntPtr pThread, IntPtr pFunction) => Continued(pAppDomain, null);

    public int MDANotification(IntPtr pController, IntPtr pThread, IntPtr pMDA) => Continued(pController, null);

    public int CustomNotification(IntPtr pThread, IntPtr pAppDomain) => Continued(pAppDomain, null);

    private int Continued(IntPtr controller, string? note)
    {
        if (note is not null)
        {
            _events.Note(note);
        }

        return Go(controller);
    }
}

/// <summary>What the callback tells the session. Kept narrow so the callback stays about dispatch.</summary>
internal interface IManagedEvents
{
    /// <summary>The process exists and its runtime is debuggable.</summary>
    void Created(IntPtr process);

    /// <summary>A module was loaded, as a raw <c>ICorDebugModule</c>.</summary>
    void ModuleLoaded(IntPtr module);

    /// <summary>Something stopped the debuggee. True to stay stopped, false to carry on.</summary>
    bool Stopped(ManagedStopKind kind, IntPtr controller, IntPtr thread, string text);

    void Exited();

    void Failed(string problem);

    void Note(string text);
}
