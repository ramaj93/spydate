using System.Runtime.InteropServices;

namespace Spydate.Debugger.Managed;

/// <summary>
/// The runtime-startup shim, which is how a process gets an <c>ICorDebug</c> at all.
///
/// The handshake it performs is the reason this is a dependency rather than something written here.
/// A managed process is not debuggable at the moment it is created: the CLR has to be far enough up
/// to have published the data structures a debugger reads, and which CLR that is cannot be known
/// before the process picks one. dbgshim launches the process suspended, waits on the event the
/// runtime signals when it is ready, works out the version, loads <em>that</em> runtime's own
/// <c>mscordbi</c>, and hands back the interface. None of that is a documented contract, and a
/// reimplementation that is subtly wrong attaches to some processes and hangs on others.
///
/// It is loaded by name from beside this assembly; the csproj copies it there.
/// </summary>
internal static partial class DbgShim
{
    private const string Library = "dbgshim.dll";

    /// <summary>The debugger interface version to ask for. 4 is <c>CorDebugVersion_4_0</c>.</summary>
    internal const int DebuggerVersion = 4;

    /// <summary>
    /// Called on a thread of dbgshim's own once the runtime is ready, or with a failing HRESULT when
    /// it never will be.
    /// </summary>
    internal unsafe delegate void RuntimeStartup(IntPtr cordb, IntPtr parameter, int hr);

    /// <summary>Creates a process suspended, so a debugger can be registered before it runs.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CreateProcessForLaunch(
        string lpCommandLine,
        [MarshalAs(UnmanagedType.Bool)] bool bSuspendProcess,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        out uint pProcessId,
        out IntPtr pResumeHandle);

    [LibraryImport(Library)]
    internal static partial int ResumeProcess(IntPtr hResumeHandle);

    [LibraryImport(Library)]
    internal static partial int CloseResumeHandle(IntPtr hResumeHandle);

    /// <summary>
    /// Asks to be called back when the target's runtime is ready.
    ///
    /// A raw function pointer rather than a delegate: the callback arrives on a native thread that
    /// dbgshim owns, and a delegate handed across that boundary has to be kept alive by hand for
    /// exactly as long as the registration lasts. The pointer form makes the lifetime the caller's
    /// explicit business instead of a field nobody must remove.
    /// </summary>
    [LibraryImport(Library)]
    internal static unsafe partial int RegisterForRuntimeStartup(
        uint dwProcessId,
        delegate* unmanaged<IntPtr, IntPtr, int, void> pfnCallback,
        IntPtr parameter,
        out IntPtr ppUnregisterToken);

    [LibraryImport(Library)]
    internal static partial int UnregisterForRuntimeStartup(IntPtr pUnregisterToken);
}
