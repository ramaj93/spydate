using System.Runtime.InteropServices;

namespace Spydate.Debugger;

/// <summary>
/// The Win32 debugging API, as much of it as this needs.
///
/// The layouts are the part to be careful with. <c>DEBUG_EVENT</c> is a union whose payload starts
/// after the header at the platform's pointer alignment — 16 bytes into the struct on x64, not 12 —
/// and reading it at the wrong offset yields addresses that look plausible and are wrong. The union
/// is kept as raw bytes and the few fields actually wanted are read out by name below, which is both
/// shorter than declaring every payload struct and harder to get quietly wrong.
/// </summary>
internal static partial class Native
{
    internal const uint DEBUG_ONLY_THIS_PROCESS = 0x00000002;
    internal const uint CREATE_SUSPENDED = 0x00000004;
    internal const uint CREATE_NEW_CONSOLE = 0x00000010;

    /// <summary>
    /// Give the debuggee a console it can write to, but no window for it.
    ///
    /// Only console allocation is affected: a program with windows of its own still shows them. It
    /// is what makes a debugger runnable from a test — a suite that starts thirty processes with
    /// <see cref="CREATE_NEW_CONSOLE"/> throws thirty windows in front of whoever is working.
    /// </summary>
    internal const uint CREATE_NO_WINDOW = 0x08000000;

    internal const uint DBG_CONTINUE = 0x00010002;
    internal const uint DBG_EXCEPTION_NOT_HANDLED = 0x80010001;

    internal const uint EXCEPTION_DEBUG_EVENT = 1;
    internal const uint CREATE_THREAD_DEBUG_EVENT = 2;
    internal const uint CREATE_PROCESS_DEBUG_EVENT = 3;
    internal const uint EXIT_THREAD_DEBUG_EVENT = 4;
    internal const uint EXIT_PROCESS_DEBUG_EVENT = 5;
    internal const uint LOAD_DLL_DEBUG_EVENT = 6;
    internal const uint UNLOAD_DLL_DEBUG_EVENT = 7;
    internal const uint OUTPUT_DEBUG_STRING_EVENT = 8;

    internal const uint EXCEPTION_BREAKPOINT = 0x80000003;
    internal const uint EXCEPTION_SINGLE_STEP = 0x80000004;
    internal const uint EXCEPTION_ACCESS_VIOLATION = 0xC0000005;

    /// <summary>
    /// A breakpoint in 32-bit code. Under WOW64 this is how <em>every</em> int3 is reported - the one
    /// the debugger planted as much as one the program has of its own - and not, as this once said, a
    /// greeting from the 32-bit loader. A debugger that does not know it hands each breakpoint back
    /// to the program unhandled, and the program dies of it with this as its exit code.
    /// </summary>
    internal const uint EXCEPTION_WX86_BREAKPOINT = 0x4000001F;

    /// <summary>The trap flag going off in 32-bit code. Every 32-bit step arrives as this.</summary>
    internal const uint EXCEPTION_WX86_SINGLE_STEP = 0x4000001E;

    /// <summary>CONTEXT_AMD64 | CONTROL | INTEGER | SEGMENTS | FLOATING_POINT.</summary>
    internal const uint CONTEXT_AMD64_FULL = 0x0010000B;

    /// <summary>WOW64_CONTEXT_i386 | CONTROL | INTEGER | SEGMENTS | FLOATING_POINT.</summary>
    internal const uint CONTEXT_WOW64_FULL = 0x0001000F;

    /// <summary>The trap flag: set it and the processor faults after one instruction.</summary>
    internal const uint TrapFlag = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFO
    {
        public uint cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    /// <summary>
    /// The header, then the union as bytes. <c>Payload</c> begins at 16 rather than 12: the union's
    /// widest members start with pointers, so the whole struct is pointer-aligned on x64.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal unsafe struct DEBUG_EVENT
    {
        [FieldOffset(0)] public uint dwDebugEventCode;
        [FieldOffset(4)] public uint dwProcessId;
        [FieldOffset(8)] public uint dwThreadId;
        [FieldOffset(16)] public fixed byte Payload[176];

        /// <summary>EXCEPTION_RECORD.ExceptionCode, first field of the exception payload.</summary>
        public uint ExceptionCode
        {
            get { fixed (byte* p = Payload) { return *(uint*)p; } }
        }

        /// <summary>EXCEPTION_RECORD.ExceptionAddress — after code, flags and the chained record.</summary>
        public ulong ExceptionAddress
        {
            get { fixed (byte* p = Payload) { return *(ulong*)(p + 16); } }
        }

        /// <summary>Whether the debugger is being offered it before the program's own handlers.</summary>
        public bool FirstChance
        {
            get { fixed (byte* p = Payload) { return *(uint*)(p + 152) != 0; } }
        }

        /// <summary>CREATE_PROCESS_DEBUG_INFO.lpBaseOfImage — after hFile, hProcess, hThread.</summary>
        public ulong CreateProcessImageBase
        {
            get { fixed (byte* p = Payload) { return *(ulong*)(p + 24); } }
        }

        /// <summary>CREATE_PROCESS_DEBUG_INFO.hThread, needed to read registers at the first stop.</summary>
        public IntPtr CreateProcessThread
        {
            get { fixed (byte* p = Payload) { return *(IntPtr*)(p + 16); } }
        }

        /// <summary>LOAD_DLL_DEBUG_INFO.lpBaseOfDll — after hFile.</summary>
        public ulong LoadDllBase
        {
            get { fixed (byte* p = Payload) { return *(ulong*)(p + 8); } }
        }

        /// <summary>
        /// LOAD_DLL_DEBUG_INFO.hFile, the first field. It is the only reliable way to find out which
        /// module was loaded: the event carries a name pointer as well, but it is optional, is often
        /// null, and points into the debuggee. This handle is owned by the debugger and must be
        /// closed, or every module a long run loads is leaked.
        /// </summary>
        public IntPtr LoadDllFile
        {
            get { fixed (byte* p = Payload) { return *(IntPtr*)p; } }
        }

        /// <summary>CREATE_PROCESS_DEBUG_INFO.hFile, the first field. Also the debugger's to close.</summary>
        public IntPtr CreateProcessFile
        {
            get { fixed (byte* p = Payload) { return *(IntPtr*)p; } }
        }

        /// <summary>UNLOAD_DLL_DEBUG_INFO.lpBaseOfDll, its only field.</summary>
        public ulong UnloadDllBase
        {
            get { fixed (byte* p = Payload) { return *(ulong*)p; } }
        }

        /// <summary>
        /// CREATE_THREAD_DEBUG_INFO.lpStartAddress — after hThread and lpThreadLocalBase. What the
        /// thread was made to go and do, which is the only thing that tells one apart from another
        /// before it has run anywhere.
        /// </summary>
        public ulong CreateThreadStartAddress
        {
            get { fixed (byte* p = Payload) { return *(ulong*)(p + 16); } }
        }

        /// <summary>CREATE_PROCESS_DEBUG_INFO.lpStartAddress, for the thread the process starts on.</summary>
        public ulong CreateProcessStartAddress
        {
            get { fixed (byte* p = Payload) { return *(ulong*)(p + 48); } }
        }

        /// <summary>EXIT_PROCESS_DEBUG_INFO.dwExitCode.</summary>
        public uint ExitCode
        {
            get { fixed (byte* p = Payload) { return *(uint*)p; } }
        }
    }

    // CreateProcessW may write into the command line, so it is passed as a mutable buffer. The
    // source-generated marshaller has no StringBuilder support, which is just as well: a char span
    // says what the API actually does with it.
    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool CreateProcess(
        string? lpApplicationName,
        char* lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WaitForDebugEvent(out DEBUG_EVENT lpDebugEvent, uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ContinueDebugEvent(uint dwProcessId, uint dwThreadId, uint dwContinueStatus);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DebugActiveProcessStop(uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    /// <summary>What <see cref="GetExitCodeProcess"/> reports for a process that has not exited.</summary>
    internal const uint STILL_ACTIVE = 259;

    /// <summary>Breaks into a running process: Windows starts a thread in it that executes an int3.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DebugBreakProcess(IntPtr hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static unsafe partial uint GetFinalPathNameByHandle(IntPtr hFile, char* lpszFilePath, uint cchFilePath, uint dwFlags);

    /// <summary>
    /// The path a module was loaded from, or null. Takes the handle the debug event carried and does
    /// not close it — the caller does, because it has to be closed whether or not this succeeds.
    ///
    /// The returned path is the normalised one, so it arrives with a <c>\\?\</c> prefix that no
    /// other part of the program would recognise. It is stripped here rather than everywhere else.
    /// </summary>
    internal static unsafe string? PathOf(IntPtr file)
    {
        if (file == IntPtr.Zero || file == new IntPtr(-1))
        {
            return null;
        }

        const int max = 32768;
        char* buffer = stackalloc char[512];
        uint length = GetFinalPathNameByHandle(file, buffer, 512, 0);

        if (length is 0 or > max)
        {
            return null;
        }

        string path = length <= 512
            ? new string(buffer, 0, (int)length)
            : LongPath(file, length);

        if (path.Length == 0)
        {
            return null;
        }

        return path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
            : path;
    }

    private static unsafe string LongPath(IntPtr file, uint length)
    {
        char[] big = new char[length + 1];
        fixed (char* p = big)
        {
            uint written = GetFinalPathNameByHandle(file, p, (uint)big.Length, 0);
            return written == 0 || written >= big.Length ? string.Empty : new string(p, 0, (int)written);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool ReadProcessMemory(
        IntPtr hProcess, ulong lpBaseAddress, byte* lpBuffer, nuint nSize, out nuint lpNumberOfBytesRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool WriteProcessMemory(
        IntPtr hProcess, ulong lpBaseAddress, byte* lpBuffer, nuint nSize, out nuint lpNumberOfBytesWritten);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FlushInstructionCache(IntPtr hProcess, ulong lpBaseAddress, nuint dwSize);

    /// <summary>Page protection constants for <see cref="VirtualProtectEx"/>.</summary>
    internal const uint PageReadWrite = 0x04;

    /// <summary>
    /// Writable and still executable, which is what code has to be made before it can be patched.
    /// <see cref="PageReadWrite"/> would do for data and is wrong here: the thread is about to run
    /// these bytes, and a page it may no longer execute is a crash rather than a breakpoint.
    /// </summary>
    internal const uint PageExecuteReadWrite = 0x40;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool VirtualProtectEx(
        IntPtr hProcess, ulong lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenThread(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwThreadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetThreadContext(IntPtr hThread, byte* lpContext);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool SetThreadContext(IntPtr hThread, byte* lpContext);

    /// <summary>
    /// The 32-bit registers of a thread in a WOW64 process. The plain calls answer for such a thread
    /// with its 64-bit context, which is the wow64 layer's own state rather than the program's, and
    /// answer it successfully - so asking the wrong one is not refused, it is quietly believed.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool Wow64GetThreadContext(IntPtr hThread, byte* lpContext);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool Wow64SetThreadContext(IntPtr hThread, byte* lpContext);

    /// <summary>True when the process is 32-bit code running on 64-bit Windows.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWow64Process(IntPtr hProcess, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);

    internal const uint THREAD_ALL_ACCESS = 0x1FFFFF;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint SuspendThread(IntPtr hThread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint ResumeThread(IntPtr hThread);

    /// <summary>A thread's scheduling priority. <c>THREAD_PRIORITY_ERROR_RETURN</c> when it cannot say.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int GetThreadPriority(IntPtr hThread);

    internal const int THREAD_PRIORITY_ERROR_RETURN = 0x7FFFFFFF;
}
