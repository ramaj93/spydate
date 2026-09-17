using System.Runtime.InteropServices;

namespace Spydate.Debugger;

/// <summary>
/// One frame up a native stack: the return address of the function a thread is stopped in, for a
/// "step out". DbgHelp's <c>StackWalk64</c> is Windows' own unwinder — it reads each function's
/// <c>.pdata</c> unwind info the way <c>RtlVirtualUnwind</c> does — so it gets the caller's return
/// address on x64, where the frame pointer is usually omitted and neither reading <c>[rsp]</c> nor an
/// <c>ebp</c> chain would find it.
///
/// Called with the debuggee stopped and its threads suspended, which is what makes it safe from
/// whichever thread asks: nothing here drives the debug loop, it only reads the process's memory and
/// one thread's registers. DbgHelp is single-threaded, so the calls are serialised by <see cref="Gate"/>
/// against each other. Symbols are not needed — only the unwind tables — so the process is initialised
/// without invading it and the one module the stop is in is loaded by hand.
/// </summary>
internal static unsafe class NativeStackWalk
{
    private static readonly Lock Gate = new();

    // The helper routines StackWalk64 calls to find a function's unwind table and its module base.
    // Passed by address; the read routine is left null, which makes StackWalk64 read the debuggee with
    // ReadProcessMemory on the handle it is given.
    private static readonly IntPtr FunctionTableAccess;
    private static readonly IntPtr GetModuleBase;

    /// <summary>Why the last walk returned 0, so a refused step out can say which it was rather than
    /// only that it failed. Read straight after a zero answer, under the same stop.</summary>
    internal static string? LastDiag;

    // Byte offsets into STACKFRAME64. Each ADDRESS64 is { DWORD64 Offset; WORD Segment; ADDRESS_MODE
    // Mode; } — Offset at +0, Mode (an int) at +12 after alignment — and the frame's five addresses
    // run at a 16-byte stride from the top of the structure.
    private const int AddrPcOffset = 0;
    private const int AddrFrameOffset = 32;
    private const int AddrStackOffset = 48;
    private const int ModeDelta = 12;
    private const int AddrModeFlat = 3;
    private const int StackFrameSize = 512;   // the real thing is ~264 bytes; zeroed slack is harmless

    static NativeStackWalk()
    {
        try
        {
            IntPtr dbghelp = NativeLibrary.Load("dbghelp.dll");
            FunctionTableAccess = NativeLibrary.GetExport(dbghelp, "SymFunctionTableAccess64");
            GetModuleBase = NativeLibrary.GetExport(dbghelp, "SymGetModuleBase64");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            FunctionTableAccess = IntPtr.Zero;
            GetModuleBase = IntPtr.Zero;
        }
    }

    /// <summary>
    /// The return address of the frame thread <paramref name="threadId"/> is stopped in — where a step
    /// out should run to — or 0 when it cannot be found (no unwind info, the top of the stack, DbgHelp
    /// unavailable). <paramref name="modulePath"/>/<paramref name="moduleBase"/> name the module the
    /// stop is in, so its unwind tables can be loaded; a missing path just lowers the odds, it is not
    /// fatal.
    /// </summary>
    public static ulong CallerReturn(IntPtr process, uint threadId, bool wow64, string? modulePath, ulong moduleBase)
    {
        LastDiag = null;
        if (FunctionTableAccess == IntPtr.Zero || GetModuleBase == IntPtr.Zero || process == IntPtr.Zero)
        {
            LastDiag = "dbghelp unavailable";
            return 0;
        }

        IntPtr thread = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, threadId);
        if (thread == IntPtr.Zero)
        {
            LastDiag = $"OpenThread failed ({Marshal.GetLastPInvokeError()})";
            return 0;
        }

        using var context = ThreadContext.For(wow64);
        try
        {
            if (!context.Read(thread))
            {
                LastDiag = "could not read thread context";
                return 0;
            }

            lock (Gate)
            {
                // Named for the process handle, since that is what DbgHelp keys on; cleaned up at the
                // end so a later walk of the same or a different process starts fresh.
                // Not SYMOPT_DEFERRED_LOADS: the unwind tables are the whole point, and a deferred
                // module is a placeholder whose image is never read, so SymFunctionTableAccess64 finds
                // no .pdata and the walk cannot get past the frame it starts in.
                Native.SymSetOptions(Native.SYMOPT_NO_PROMPTS | Native.SYMOPT_FAIL_CRITICAL_ERRORS);
                if (!Native.SymInitializeW(process, IntPtr.Zero, invadeProcess: false))
                {
                    LastDiag = $"SymInitialize failed ({Marshal.GetLastPInvokeError()})";
                    return 0;
                }

                try
                {
                    ulong loaded = 0;
                    if (moduleBase != 0 && !string.IsNullOrEmpty(modulePath))
                    {
                        // Size 0 lets DbgHelp read the image's own headers for its extent. A failure
                        // here (already loaded, or unreadable) is not fatal to the walk that follows.
                        loaded = Native.SymLoadModuleExW(process, IntPtr.Zero, modulePath, IntPtr.Zero, moduleBase, 0, IntPtr.Zero, 0);
                    }

                    return Walk(process, thread, wow64, context, modulePath, moduleBase, loaded);
                }
                finally
                {
                    Native.SymCleanup(process);
                }
            }
        }
        finally
        {
            Native.CloseHandle(thread);
        }
    }

    private static ulong Walk(IntPtr process, IntPtr thread, bool wow64, ThreadContext context, string? modulePath, ulong moduleBase, ulong loaded)
    {
        byte* frame = stackalloc byte[StackFrameSize];
        NativeMemory.Clear(frame, StackFrameSize);

        ulong rip = context.InstructionPointer;
        Seed(frame, AddrPcOffset, rip);
        Seed(frame, AddrFrameOffset, context.FramePointer);
        Seed(frame, AddrStackOffset, context.StackPointer);

        uint machine = wow64 ? Native.IMAGE_FILE_MACHINE_I386 : Native.IMAGE_FILE_MACHINE_AMD64;

        // Each call advances one frame. The first settles on the frame the thread is in now (its
        // AddrPC is the current rip); the second unwinds to the caller, and that frame's AddrPC is the
        // address the current function returns to — where a step out should stop. The context is
        // StackWalk64's to read and update; it is a fresh copy, so letting it be modified costs nothing.
        bool first = Native.StackWalk64(machine, process, thread, frame, context.Raw, IntPtr.Zero, FunctionTableAccess, GetModuleBase, IntPtr.Zero);
        if (!first)
        {
            LastDiag = $"the frame at 0x{rip:X} could not be unwound"
                       + (loaded == 0 && moduleBase != 0 ? $" ({System.IO.Path.GetFileName(modulePath)} has no unwind information here)" : string.Empty);
            return 0;
        }

        bool second = Native.StackWalk64(machine, process, thread, frame, context.Raw, IntPtr.Zero, FunctionTableAccess, GetModuleBase, IntPtr.Zero);
        ulong ret = *(ulong*)(frame + AddrPcOffset);
        if (!second || ret == 0 || ret == rip)
        {
            // The outermost frame has no caller to unwind to; a step out of it has nowhere to land.
            LastDiag = "there is no caller frame to return to";
            return 0;
        }

        return ret;
    }

    private static void Seed(byte* frame, int addressOffset, ulong value)
    {
        *(ulong*)(frame + addressOffset) = value;
        *(int*)(frame + addressOffset + ModeDelta) = AddrModeFlat;
    }
}
