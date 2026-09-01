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
    internal const uint CREATE_NEW_CONSOLE = 0x00000010;

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

    /// <summary>CONTEXT_AMD64 | CONTROL | INTEGER | SEGMENTS | FLOATING_POINT.</summary>
    internal const uint CONTEXT_AMD64_FULL = 0x0010000B;

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
    internal static partial bool CloseHandle(IntPtr hObject);

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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenThread(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwThreadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetThreadContext(IntPtr hThread, byte* lpContext);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool SetThreadContext(IntPtr hThread, byte* lpContext);

    internal const uint THREAD_ALL_ACCESS = 0x1FFFFF;
}
