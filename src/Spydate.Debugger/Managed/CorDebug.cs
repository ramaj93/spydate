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
    internal const string Module = "dba2d8c1-e5c5-4069-8c13-10a7c6abf43d";
    internal const string Function = "CC7BCAF3-8A68-11d2-983C-0000F808342D";
    internal const string Code = "CC7BCAF4-8A68-11d2-983C-0000F808342D";
    internal const string Breakpoint = "CC7BCAF9-8A68-11d2-983C-0000F808342D";
    internal const string FunctionBreakpoint = "CC7BCAFA-8A68-11d2-983C-0000F808342D";
    internal const string Thread = "938c6d66-7fb6-4f69-b389-425b8987329b";
    internal const string Frame = "CC7BCAEF-8A68-11d2-983C-0000F808342D";
    internal const string IlFrame = "03E26311-4F76-11d3-88C6-006097945418";
    internal const string Stepper = "CC7BCAEC-8A68-11d2-983C-0000F808342D";
    internal const string Value = "CC7BCAF7-8A68-11d2-983C-0000F808342D";
    internal const string GenericValue = "CC7BCAF8-8A68-11d2-983C-0000F808342D";
    internal const string ReferenceValue = "CC7BCAF9-8A68-11d2-983C-0000F808342D";
    internal const string StringValue = "CC7BCAFD-8A68-11d2-983C-0000F808342D";

    // The four below are pinned by CorDebugIidTests the same way as the rest: a live array and a
    // live object are asked what they support, because an id written from memory fails as
    // "not available" rather than as a mistake.
    internal const string Class = "CC7BCAF5-8A68-11d2-983C-0000F808342D";
    internal const string BoxValue = "CC7BCAF6-8A68-11d2-983C-0000F808342D";
    internal const string ObjectValue = "18AD3D6E-B7D2-11d2-BD04-0000F80849BD";
    internal const string ArrayValue = "0405B0DF-A660-11d2-BD02-0000F80849BD";
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

    /// <summary>
    /// Launches a program under this debugger. The .NET Framework route, and only that one —
    /// CoreCLR answers <c>E_NOTIMPL</c>, which is the whole reason dbgshim exists.
    ///
    /// The strings are spelled out as <c>LPWStr</c> rather than left to the default, which for a COM
    /// interface is <c>BSTR</c>: the runtime passes these straight to <c>CreateProcessW</c>, and a
    /// BSTR arriving there is a pointer four bytes past a length the callee does not know about. The
    /// command line is a pointer for a second reason — <c>CreateProcessW</c> may write into that
    /// buffer, and a marshalled string is not ours to have written in.
    /// </summary>
    [PreserveSig] int CreateProcess(
        [MarshalAs(UnmanagedType.LPWStr)] string? lpApplicationName,
        IntPtr lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        int bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpCurrentDirectory,
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

    [PreserveSig] int GetName(uint cchName, out uint pcchName, IntPtr szName);

    [PreserveSig] int GetObject(out IntPtr ppObject);

    [PreserveSig] int Attach();

    [PreserveSig] int GetID(out uint pId);
}

/// <summary>
/// One assembly as the runtime has it loaded. This is where a breakpoint starts: a method token
/// means nothing on its own, and the module is what turns it into something with code.
/// </summary>
[ComImport]
[Guid(CorDebugGuids.Module)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebugModule
{
    [PreserveSig] int GetProcess(out IntPtr ppProcess);

    [PreserveSig] int GetBaseAddress(out ulong pAddress);

    [PreserveSig] int GetAssembly(out IntPtr ppAssembly);

    /// <summary>
    /// The module's path, into a buffer the caller supplies.
    ///
    /// <c>IntPtr</c> rather than <c>char[]</c>, and that is not a preference. Array parameters on a
    /// <c>[ComImport]</c> interface default to <c>SafeArray</c>, so a plain <c>char[]</c> here has
    /// the marshaller read a SAFEARRAY header out of a flat buffer of characters — which does not
    /// fault politely, it takes the process down. It ran on the runtime's own callback thread, so
    /// what the test showed was the whole host disappearing mid-run.
    /// </summary>
    [PreserveSig] int GetName(uint cchName, out uint pcchName, IntPtr szName);

    [PreserveSig] int EnableJITDebugging(int bTrackJITInfo, int bAllowJitOpts);

    [PreserveSig] int EnableClassLoadCallbacks(int bClassLoadCallbacks);

    [PreserveSig] int GetFunctionFromToken(uint methodDef, out ICorDebugFunction? ppFunction);

    [PreserveSig] int GetFunctionFromRVA(ulong rva, out IntPtr ppFunction);

    [PreserveSig] int GetClassFromToken(uint typeDef, out IntPtr ppClass);

    [PreserveSig] int CreateBreakpoint(out IntPtr ppBreakpoint);

    [PreserveSig] int GetEditAndContinueSnapshot(out IntPtr ppEditAndContinueSnapshot);

    [PreserveSig] int GetMetaDataInterface(ref Guid riid, out IntPtr ppObj);

    [PreserveSig] int GetToken(out uint pToken);

    [PreserveSig] int IsDynamic(out int pDynamic);

    [PreserveSig] int GetGlobalVariableValue(uint fieldDef, out IntPtr ppValue);

    [PreserveSig] int GetSize(out uint pcBytes);

    [PreserveSig] int IsInMemory(out int pInMemory);
}

[ComImport]
[Guid(CorDebugGuids.Function)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebugFunction
{
    [PreserveSig] int GetModule(out IntPtr ppModule);

    [PreserveSig] int GetClass(out IntPtr ppClass);

    [PreserveSig] int GetToken(out uint pMethodDef);

    [PreserveSig] int GetILCode(out ICorDebugCode? ppCode);

    [PreserveSig] int GetNativeCode(out IntPtr ppCode);

    [PreserveSig] int CreateBreakpoint(out IntPtr ppBreakpoint);

    [PreserveSig] int GetLocalVarSigToken(out uint pmdSig);

    [PreserveSig] int GetCurrentVersionNumber(out uint pnCurrentVersion);
}

/// <summary>
/// A method's code. The IL form is the one that matters here: a breakpoint on it is placed at an IL
/// offset, which is the same number the listing prints, and the runtime works out where that landed
/// after the JIT rather than making anybody guess at an address.
/// </summary>
[ComImport]
[Guid(CorDebugGuids.Code)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebugCode
{
    [PreserveSig] int IsIL(out int pbIL);

    [PreserveSig] int GetFunction(out IntPtr ppFunction);

    [PreserveSig] int GetAddress(out ulong pStart);

    [PreserveSig] int GetSize(out uint pcBytes);

    /// <summary>
    /// A breakpoint at an IL offset, returned as a raw pointer.
    ///
    /// Not typed, because typing it makes the marshaller QueryInterface for an IID, and an IID that
    /// is one digit wrong fails with E_NOINTERFACE on an object that is in fact exactly what was
    /// asked for. The pointer already is an <c>ICorDebugFunctionBreakpoint</c>; nothing is gained by
    /// asking it to prove that, and <see cref="Activation.Set"/> reaches the one method needed
    /// through the vtable it certainly has.
    /// </summary>
    [PreserveSig] int CreateBreakpoint(uint offset, out IntPtr ppBreakpoint);

    [PreserveSig] int GetCode(uint startOffset, uint endOffset, uint cBufferAlloc, IntPtr buffer, out uint pcBufferSize);

    [PreserveSig] int GetVersionNumber(out uint nVersion);

    [PreserveSig] int GetILToNativeMapping(uint cMap, out uint pcMap, IntPtr map);

    [PreserveSig] int GetEHClauses(uint cClauses, out uint pcClauses, IntPtr clauses);
}

[ComImport]
[Guid(CorDebugGuids.FunctionBreakpoint)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebugFunctionBreakpoint
{
    // ICorDebugBreakpoint, repeated: slots come from this interface alone.
    [PreserveSig] int Activate(int bActive);

    [PreserveSig] int IsActive(out int pbActive);

    [PreserveSig] int GetFunction(out IntPtr ppFunction);

    [PreserveSig] int GetOffset(out uint pnOffset);
}

[ComImport]
[Guid(CorDebugGuids.Thread)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebugThread
{
    [PreserveSig] int GetProcess(out IntPtr ppProcess);

    [PreserveSig] int GetID(out uint pdwThreadId);

    [PreserveSig] int GetHandle(out IntPtr phThreadHandle);

    [PreserveSig] int GetAppDomain(out IntPtr ppAppDomain);

    [PreserveSig] int SetDebugState(int state);

    [PreserveSig] int GetDebugState(out int pState);

    [PreserveSig] int GetUserState(out int pState);

    [PreserveSig] int GetCurrentException(out IntPtr ppExceptionObject);

    [PreserveSig] int ClearCurrentException();

    [PreserveSig] int CreateStepper(out ICorDebugStepper? ppStepper);

    [PreserveSig] int EnumerateChains(out IntPtr ppChains);

    [PreserveSig] int GetActiveChain(out IntPtr ppChain);

    [PreserveSig] int GetActiveFrame(out IntPtr ppFrame);

    [PreserveSig] int GetRegisterSet(out IntPtr ppRegisters);

    [PreserveSig] int CreateEval(out IntPtr ppEval);

    [PreserveSig] int GetObject(out IntPtr ppObject);
}

/// <summary>
/// A frame as the runtime sees it, which is the point of debugging managed code this way: where the
/// program is, is a method and an offset into its IL, not an address that happens to be inside some
/// JIT output.
/// </summary>
[ComImport]
[Guid(CorDebugGuids.IlFrame)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebugILFrame
{
    // ICorDebugFrame, repeated: slots come from this interface alone.
    [PreserveSig] int GetChain(out IntPtr ppChain);

    [PreserveSig] int GetCode(out IntPtr ppCode);

    [PreserveSig] int GetFunction(out ICorDebugFunction? ppFunction);

    [PreserveSig] int GetFunctionToken(out uint pToken);

    [PreserveSig] int GetStackRange(out ulong pStart, out ulong pEnd);

    [PreserveSig] int GetCaller(out IntPtr ppFrame);

    [PreserveSig] int GetCallee(out IntPtr ppFrame);

    [PreserveSig] int CreateStepper(out IntPtr ppStepper);

    // ICorDebugILFrame itself.
    [PreserveSig] int GetIP(out uint pnOffset, out int pMappingResult);

    [PreserveSig] int SetIP(uint nOffset);

    [PreserveSig] int EnumerateLocalVariables(out IntPtr ppValueEnum);

    [PreserveSig] int GetLocalVariable(uint dwIndex, out IntPtr ppValue);

    [PreserveSig] int EnumerateArguments(out IntPtr ppValueEnum);

    [PreserveSig] int GetArgument(uint dwIndex, out IntPtr ppValue);

    [PreserveSig] int GetStackDepth(out uint pDepth);

    [PreserveSig] int GetStackValue(uint dwIndex, out IntPtr ppValue);

    [PreserveSig] int CanSetIP(uint nOffset);
}

/// <summary>
/// One step in progress.
///
/// A stepper is armed while the debuggee is stopped and then does its work when it is continued:
/// the runtime reports <c>StepComplete</c> when the step lands. Stepping by IL rather than by
/// machine instruction is the point — one IL instruction is a unit of the program, where one
/// machine instruction is a unit of whatever the JIT decided to emit for it this time.
/// </summary>
[ComImport]
[Guid(CorDebugGuids.Stepper)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebugStepper
{
    [PreserveSig] int IsActive(out int pbActive);

    [PreserveSig] int Deactivate();

    [PreserveSig] int SetInterceptMask(int mask);

    [PreserveSig] int SetUnmappedStopMask(int mask);

    [PreserveSig] int Step(int bStepIn);

    [PreserveSig] int StepRange(int bStepIn, IntPtr ranges, uint cRangeCount);

    [PreserveSig] int StepOut();

    [PreserveSig] int SetRangeIL(int bIL);
}

/// <summary>
/// A value in the debuggee: a local, an argument, a field.
///
/// The method is called <c>GetKind</c> here and <c>GetType</c> in the IDL. Only the slot matters to
/// COM, and <c>GetType</c> on an interface-typed variable in C# reads as <c>object.GetType</c> at a
/// glance, which is a confusion worth not having in code that is already full of them.
/// </summary>
[ComImport]
[Guid(CorDebugGuids.Value)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebugValue
{
    [PreserveSig] int GetKind(out int pType);

    [PreserveSig] int GetSize(out uint pSize);

    [PreserveSig] int GetAddress(out ulong pAddress);

    [PreserveSig] int CreateBreakpoint(out IntPtr ppBreakpoint);
}

/// <summary>A value whose bytes can be copied out: everything that is not a reference.</summary>
[ComImport]
[Guid(CorDebugGuids.GenericValue)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebugGenericValue
{
    // ICorDebugValue, repeated: slots come from this interface alone.
    [PreserveSig] int GetKind(out int pType);

    [PreserveSig] int GetSize(out uint pSize);

    [PreserveSig] int GetAddress(out ulong pAddress);

    [PreserveSig] int CreateBreakpoint(out IntPtr ppBreakpoint);

    [PreserveSig] int GetValue(IntPtr pTo);

    [PreserveSig] int SetValue(IntPtr pFrom);
}

/// <summary>A reference, which is either null or points at something worth following.</summary>
[ComImport]
[Guid(CorDebugGuids.ReferenceValue)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebugReferenceValue
{
    // ICorDebugValue, repeated.
    [PreserveSig] int GetKind(out int pType);

    [PreserveSig] int GetSize(out uint pSize);

    [PreserveSig] int GetAddress(out ulong pAddress);

    [PreserveSig] int CreateBreakpoint(out IntPtr ppBreakpoint);

    [PreserveSig] int IsNull(out int pbNull);

    [PreserveSig] int GetValue(out ulong pValue);

    [PreserveSig] int SetValue(ulong value);

    [PreserveSig] int Dereference(out IntPtr ppValue);

    [PreserveSig] int DereferenceStrong(out IntPtr ppValue);
}

/// <summary>A string on the debuggee's heap, which is worth reading rather than counting.</summary>
[ComImport]
[Guid(CorDebugGuids.StringValue)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebugStringValue
{
    // ICorDebugValue, then ICorDebugHeapValue, repeated.
    [PreserveSig] int GetKind(out int pType);

    [PreserveSig] int GetSize(out uint pSize);

    [PreserveSig] int GetAddress(out ulong pAddress);

    [PreserveSig] int CreateBreakpoint(out IntPtr ppBreakpoint);

    [PreserveSig] int IsValid(out int pbValid);

    [PreserveSig] int CreateRelocBreakpoint(out IntPtr ppBreakpoint);

    [PreserveSig] int GetLength(out uint pcchString);

    [PreserveSig] int GetString(uint cchString, out uint pcchString, IntPtr szString);
}

/// <summary>
/// An object on the debuggee's heap, or a value class.
///
/// Derives from <c>ICorDebugValue</c> and not from <c>ICorDebugHeapValue</c>, which matters here
/// more than it looks: the slots are counted from the methods declared in this interface alone, so
/// two extra declarations would put <c>GetClass</c> on <c>GetFieldValue</c>'s slot and every field
/// read would be a call to something else with the same argument count.
/// </summary>
[ComImport]
[Guid(CorDebugGuids.ObjectValue)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebugObjectValue
{
    // ICorDebugValue, repeated.
    [PreserveSig] int GetKind(out int pType);

    [PreserveSig] int GetSize(out uint pSize);

    [PreserveSig] int GetAddress(out ulong pAddress);

    [PreserveSig] int CreateBreakpoint(out IntPtr ppBreakpoint);

    // ICorDebugObjectValue itself.
    [PreserveSig] int GetClass(out IntPtr ppClass);

    [PreserveSig] int GetFieldValue(IntPtr pClass, uint fieldDef, out IntPtr ppValue);

    [PreserveSig] int GetVirtualMethod(uint memberRef, out IntPtr ppFunction);

    [PreserveSig] int GetContext(out IntPtr ppContext);

    [PreserveSig] int IsValueClass(out int pbIsValueClass);

    [PreserveSig] int GetManagedCopy(out IntPtr ppObject);

    [PreserveSig] int SetFromManagedCopy(IntPtr pObject);
}

/// <summary>An array on the debuggee's heap.</summary>
[ComImport]
[Guid(CorDebugGuids.ArrayValue)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebugArrayValue
{
    // ICorDebugValue, then ICorDebugHeapValue, repeated.
    [PreserveSig] int GetKind(out int pType);

    [PreserveSig] int GetSize(out uint pSize);

    [PreserveSig] int GetAddress(out ulong pAddress);

    [PreserveSig] int CreateBreakpoint(out IntPtr ppBreakpoint);

    [PreserveSig] int IsValid(out int pbValid);

    [PreserveSig] int CreateRelocBreakpoint(out IntPtr ppBreakpoint);

    // ICorDebugArrayValue itself. Every array parameter is an IntPtr: the default marshalling for
    // one is a SAFEARRAY, and handing a flat buffer to something expecting that takes the process
    // down rather than failing.
    [PreserveSig] int GetElementType(out int pType);

    [PreserveSig] int GetRank(out uint pnRank);

    [PreserveSig] int GetCount(out uint pnCount);

    [PreserveSig] int GetDimensions(uint cdim, IntPtr dims);

    [PreserveSig] int HasBaseIndicies(out int pbHasBaseIndicies);

    [PreserveSig] int GetBaseIndicies(uint cdim, IntPtr indicies);

    [PreserveSig] int GetElement(uint cdim, IntPtr indices, out IntPtr ppValue);

    [PreserveSig] int GetElementAtPosition(uint nPosition, out IntPtr ppValue);
}

/// <summary>A type, as the runtime holds it: which module declares it, and under which token.</summary>
[ComImport]
[Guid(CorDebugGuids.Class)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebugClass
{
    [PreserveSig] int GetModule(out IntPtr pModule);

    [PreserveSig] int GetToken(out uint pTypeDef);

    [PreserveSig] int GetStaticFieldValue(uint fieldDef, IntPtr pFrame, out IntPtr ppValue);
}

/// <summary>A boxed value type. The thing worth reading is inside it.</summary>
[ComImport]
[Guid(CorDebugGuids.BoxValue)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICorDebugBoxValue
{
    // ICorDebugValue, then ICorDebugHeapValue, repeated.
    [PreserveSig] int GetKind(out int pType);

    [PreserveSig] int GetSize(out uint pSize);

    [PreserveSig] int GetAddress(out ulong pAddress);

    [PreserveSig] int CreateBreakpoint(out IntPtr ppBreakpoint);

    [PreserveSig] int IsValid(out int pbValid);

    [PreserveSig] int CreateRelocBreakpoint(out IntPtr ppBreakpoint);

    [PreserveSig] int GetObject(out IntPtr ppObject);
}
