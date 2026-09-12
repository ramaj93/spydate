using System.Runtime.InteropServices;

namespace Spydate.Debugger.Managed;

/// <summary>
/// The parts of the CLR debugging API this drives.
///
/// Hand-written, because there is no package that supplies them: the usable wrapper everyone
/// remembers is <c>MdbgCore</c>, which is .NET Framework-era, and CLRMD reads a process rather than
/// controlling one. What is here is the subset a debugger needs to launch, stop, look and step —
/// not the interface set, which runs to dozens more.
///
/// Two rules hold throughout and neither is stylistic.
///
/// Every method is <c>PreserveSig</c> and returns its HRESULT. The alternative lets the marshaller
/// throw, and these are called from inside callbacks the runtime is waiting on: an exception
/// crossing back into native code there leaves the debuggee stopped forever, which presents as a
/// hang with nothing to read.
///
/// Every interface pointer in a <em>callback</em> is an <c>IntPtr</c>. The objects handed to a
/// callback are borrowed for the duration of the call, and typing them as interfaces would have the
/// marshaller build and release wrappers for twenty-six methods' worth of parameters that are
/// mostly unused. Taking them as raw pointers means nothing is released that was not first claimed.
/// </summary>
internal static class CorDebugGuids
{
    internal const string CorDebug = "3d6f5f61-7538-11d3-8d5b-00104b35e7ef";
    internal const string ManagedCallback = "3d6f5f60-7538-11d3-8d5b-00104b35e7ef";
    internal const string ManagedCallback2 = "250E5EEA-DB5C-4C76-B6F3-8C46F12E3203";
    internal const string ManagedCallback3 = "264EA0FC-2591-49AA-868E-835E6515323F";
    internal const string Controller = "3d6f5f62-7538-11d3-8d5b-00104b35e7ef";
    internal const string Process = "3d6f5f64-7538-11d3-8d5b-00104b35e7ef";
    internal const string AppDomain = "3d6f5f63-7538-11d3-8d5b-00104b35e7ef";
}

[ComImport]
[Guid(CorDebugGuids.CorDebug)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebug
{
    [PreserveSig] int Initialize();

    [PreserveSig] int Terminate();

    [PreserveSig] int SetManagedHandler([MarshalAs(UnmanagedType.Interface)] ICorDebugManagedCallback pCallback);

    [PreserveSig] int SetUnmanagedHandler(IntPtr pCallback);

    [PreserveSig] int CreateProcess(
        string? lpApplicationName,
        string? lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        int bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        IntPtr lpStartupInfo,
        IntPtr lpProcessInformation,
        int debuggingFlags,
        out IntPtr ppProcess);

    [PreserveSig] int DebugActiveProcess(uint id, int win32Attach, out IntPtr ppProcess);

    [PreserveSig] int EnumerateProcesses(out IntPtr ppProcess);

    [PreserveSig] int GetProcess(uint dwProcessId, out IntPtr ppProcess);

    [PreserveSig] int CanLaunchOrAttach(uint dwProcessId, int win32DebuggingEnabled);
}

/// <summary>
/// Everything the runtime reports, in the order the vtable expects it.
///
/// All twenty-six have to be here and in this order whether or not they are interesting: the runtime
/// calls them by slot. Every one of them must also end in a <c>Continue</c> or the debuggee stays
/// stopped — there is no timeout, and the symptom of forgetting is a process that never runs again.
/// </summary>
[ComImport]
[Guid(CorDebugGuids.ManagedCallback)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebugManagedCallback
{
    [PreserveSig] int Breakpoint(IntPtr pAppDomain, IntPtr pThread, IntPtr pBreakpoint);

    [PreserveSig] int StepComplete(IntPtr pAppDomain, IntPtr pThread, IntPtr pStepper, int reason);

    [PreserveSig] int Break(IntPtr pAppDomain, IntPtr thread);

    [PreserveSig] int Exception(IntPtr pAppDomain, IntPtr pThread, int unhandled);

    [PreserveSig] int EvalComplete(IntPtr pAppDomain, IntPtr pThread, IntPtr pEval);

    [PreserveSig] int EvalException(IntPtr pAppDomain, IntPtr pThread, IntPtr pEval);

    [PreserveSig] int CreateProcess(IntPtr pProcess);

    [PreserveSig] int ExitProcess(IntPtr pProcess);

    [PreserveSig] int CreateThread(IntPtr pAppDomain, IntPtr thread);

    [PreserveSig] int ExitThread(IntPtr pAppDomain, IntPtr thread);

    [PreserveSig] int LoadModule(IntPtr pAppDomain, IntPtr pModule);

    [PreserveSig] int UnloadModule(IntPtr pAppDomain, IntPtr pModule);

    [PreserveSig] int LoadClass(IntPtr pAppDomain, IntPtr c);

    [PreserveSig] int UnloadClass(IntPtr pAppDomain, IntPtr c);

    [PreserveSig] int DebuggerError(IntPtr pProcess, int errorHR, uint errorCode);

    [PreserveSig] int LogMessage(IntPtr pAppDomain, IntPtr pThread, int lLevel, IntPtr pLogSwitchName, IntPtr pMessage);

    [PreserveSig] int LogSwitch(IntPtr pAppDomain, IntPtr pThread, int lLevel, uint ulReason, IntPtr pLogSwitchName, IntPtr pParentName);

    [PreserveSig] int CreateAppDomain(IntPtr pProcess, IntPtr pAppDomain);

    [PreserveSig] int ExitAppDomain(IntPtr pProcess, IntPtr pAppDomain);

    [PreserveSig] int LoadAssembly(IntPtr pAppDomain, IntPtr pAssembly);

    [PreserveSig] int UnloadAssembly(IntPtr pAppDomain, IntPtr pAssembly);

    [PreserveSig] int ControlCTrap(IntPtr pProcess);

    [PreserveSig] int NameChange(IntPtr pAppDomain, IntPtr pThread);

    [PreserveSig] int UpdateModuleSymbols(IntPtr pAppDomain, IntPtr pModule, IntPtr pSymbolStream);

    [PreserveSig] int EditAndContinueRemap(IntPtr pAppDomain, IntPtr pThread, IntPtr pFunction, int fAccurate);

    [PreserveSig] int BreakpointSetError(IntPtr pAppDomain, IntPtr pThread, IntPtr pBreakpoint, uint dwError);
}

/// <summary>
/// The second generation of callbacks. Not optional in practice: the runtime queries for it during
/// startup, and a debugger that does not answer gets a different and worse set of exception events.
/// </summary>
[ComImport]
[Guid(CorDebugGuids.ManagedCallback2)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebugManagedCallback2
{
    [PreserveSig] int FunctionRemapOpportunity(IntPtr pAppDomain, IntPtr pThread, IntPtr pOldFunction, IntPtr pNewFunction, uint oldILOffset);

    [PreserveSig] int CreateConnection(IntPtr pProcess, uint dwConnectionId, IntPtr pConnName);

    [PreserveSig] int ChangeConnection(IntPtr pProcess, uint dwConnectionId);

    [PreserveSig] int DestroyConnection(IntPtr pProcess, uint dwConnectionId);

    [PreserveSig] int Exception(IntPtr pAppDomain, IntPtr pThread, IntPtr pFrame, uint nOffset, int dwEventType, uint dwFlags);

    [PreserveSig] int ExceptionUnwind(IntPtr pAppDomain, IntPtr pThread, int dwEventType, uint dwFlags);

    [PreserveSig] int FunctionRemapComplete(IntPtr pAppDomain, IntPtr pThread, IntPtr pFunction);

    [PreserveSig] int MDANotification(IntPtr pController, IntPtr pThread, IntPtr pMDA);
}

[ComImport]
[Guid(CorDebugGuids.ManagedCallback3)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebugManagedCallback3
{
    [PreserveSig] int CustomNotification(IntPtr pThread, IntPtr pAppDomain);
}

/// <summary>
/// Stopping and starting, which <c>ICorDebugProcess</c> and <c>ICorDebugAppDomain</c> both are.
///
/// Declared here and then <em>declared again</em> in each of them, rather than inherited. That is
/// not duplication anybody wanted: for a <c>[ComImport]</c> interface the runtime assigns vtable
/// slots from the methods written in that interface alone, and a base interface contributes
/// nothing to the offset. Writing <c>ICorDebugProcess : ICorDebugController</c> compiles, reads
/// correctly, and puts <c>GetID</c> on slot three — which is <c>Stop</c>. It cost an afternoon: the
/// symptom was a debugger that attached perfectly, reported process id zero, and would not
/// terminate anything, because every call on it was landing seven slots early.
/// </summary>
[ComImport]
[Guid(CorDebugGuids.Controller)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebugController
{
    [PreserveSig] int Stop(uint dwTimeoutIgnored);

    [PreserveSig] int Continue(int fIsOutOfBand);

    [PreserveSig] int IsRunning(out int pbRunning);

    [PreserveSig] int HasQueuedCallbacks(IntPtr pThread, out int pbQueued);

    [PreserveSig] int EnumerateThreads(out IntPtr ppThreads);

    [PreserveSig] int SetAllThreadsDebugState(int state, IntPtr pExceptThisThread);

    [PreserveSig] int Detach();

    [PreserveSig] int Terminate(uint exitCode);

    [PreserveSig] int CanCommitChanges(uint cSnapshots, IntPtr pSnapshots, out IntPtr pError);

    [PreserveSig] int CommitChanges(uint cSnapshots, IntPtr pSnapshots, out IntPtr pError);
}

[ComImport]
[Guid(CorDebugGuids.Process)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebugProcess
{
    // ICorDebugController, repeated because the slots are counted from here. See above.
    [PreserveSig] int Stop(uint dwTimeoutIgnored);

    [PreserveSig] int Continue(int fIsOutOfBand);

    [PreserveSig] int IsRunning(out int pbRunning);

    [PreserveSig] int HasQueuedCallbacks(IntPtr pThread, out int pbQueued);

    [PreserveSig] int EnumerateThreads(out IntPtr ppThreads);

    [PreserveSig] int SetAllThreadsDebugState(int state, IntPtr pExceptThisThread);

    [PreserveSig] int Detach();

    [PreserveSig] int Terminate(uint exitCode);

    [PreserveSig] int CanCommitChanges(uint cSnapshots, IntPtr pSnapshots, out IntPtr pError);

    [PreserveSig] int CommitChanges(uint cSnapshots, IntPtr pSnapshots, out IntPtr pError);

    // ICorDebugProcess itself.
    [PreserveSig] int GetID(out uint pdwProcessId);

    [PreserveSig] int GetHandle(out IntPtr phProcessHandle);

    [PreserveSig] int GetThread(uint dwThreadId, out IntPtr ppThread);

    [PreserveSig] int EnumerateObjects(out IntPtr ppObjects);

    [PreserveSig] int IsTransitionStub(ulong address, out int pbTransitionStub);

    [PreserveSig] int IsOSSuspended(uint threadID, out int pbSuspended);

    [PreserveSig] int GetThreadContext(uint threadID, uint contextSize, IntPtr context);

    [PreserveSig] int SetThreadContext(uint threadID, uint contextSize, IntPtr context);

    [PreserveSig] int ReadMemory(ulong address, uint size, byte[] buffer, out IntPtr read);

    [PreserveSig] int WriteMemory(ulong address, uint size, byte[] buffer, out IntPtr written);

    [PreserveSig] int ClearCurrentException(uint threadID);

    [PreserveSig] int EnableLogMessages(int fOnOff);

    [PreserveSig] int ModifyLogSwitch(string pLogSwitchName, int lLevel);

    [PreserveSig] int EnumerateAppDomains(out IntPtr ppAppDomains);

    [PreserveSig] int GetObject(out IntPtr ppObject);

    [PreserveSig] int ThreadForFiberCookie(uint fiberCookie, out IntPtr ppThread);

    [PreserveSig] int GetHelperThreadID(out uint pThreadID);
}

[ComImport]
[Guid(CorDebugGuids.AppDomain)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebugAppDomain
{
    // ICorDebugController, repeated for the same reason ICorDebugProcess repeats it.
    [PreserveSig] int Stop(uint dwTimeoutIgnored);

    [PreserveSig] int Continue(int fIsOutOfBand);

    [PreserveSig] int IsRunning(out int pbRunning);

    [PreserveSig] int HasQueuedCallbacks(IntPtr pThread, out int pbQueued);

    [PreserveSig] int EnumerateThreads(out IntPtr ppThreads);

    [PreserveSig] int SetAllThreadsDebugState(int state, IntPtr pExceptThisThread);

    [PreserveSig] int Detach();

    [PreserveSig] int Terminate(uint exitCode);

    [PreserveSig] int CanCommitChanges(uint cSnapshots, IntPtr pSnapshots, out IntPtr pError);

    [PreserveSig] int CommitChanges(uint cSnapshots, IntPtr pSnapshots, out IntPtr pError);

    // ICorDebugAppDomain itself.
    [PreserveSig] int GetProcess(out IntPtr ppProcess);

    [PreserveSig] int EnumerateAssemblies(out IntPtr ppAssemblies);

    [PreserveSig] int GetModuleFromMetaDataInterface(IntPtr pIMetaData, out IntPtr ppModule);

    [PreserveSig] int EnumerateBreakpoints(out IntPtr ppBreakpoints);

    [PreserveSig] int EnumerateSteppers(out IntPtr ppSteppers);

    [PreserveSig] int IsAttached(out int pbAttached);

    [PreserveSig] int GetName(uint cchName, out uint pcchName, [Out, MarshalAs(UnmanagedType.LPArray)] char[]? szName);

    [PreserveSig] int GetObject(out IntPtr ppObject);

    [PreserveSig] int Attach();

    [PreserveSig] int GetID(out uint pId);
}
