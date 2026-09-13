using System.Runtime.InteropServices;

namespace Spydate.Debugger.Managed;

/// <summary>
/// One frame of a stopped thread's call stack, as a Call Stack window shows it.
/// </summary>
/// <param name="Index">Its position in the stack, innermost first. What <c>SelectFrame</c> takes.</param>
/// <param name="Module">The module the method is in, or empty for a native or internal frame.</param>
/// <param name="MethodToken">Its method's metadata token, or 0 when there is none.</param>
/// <param name="Offset">The IL offset within it, for a managed frame.</param>
/// <param name="Display">The whole line: method signature, or a note for a non-managed frame.</param>
/// <param name="IsManaged">Whether it has IL — whether its locals can be read and it can be selected.</param>
public sealed record ManagedFrame(
    int Index,
    string Module,
    uint MethodToken,
    uint Offset,
    string Display,
    bool IsManaged,
    bool IsCurrent);

/// <summary>
/// Walking a thread's stack: the chains, and the frames within them.
///
/// A stack is a list of chains, and a chain is a run of frames of one kind. The distinction is not
/// pedantry — it is the fix for a waiting thread reading "[not in managed code]". A thread blocked in
/// a wait has a native chain innermost (the syscall) and its managed frames one chain down; asking
/// only for the active frame finds the native one and gives up. Walking the chains finds the code.
/// </summary>
internal static class ManagedStack
{
    /// <summary>A stack deeper than this is a walk that is not ending.</summary>
    private const int MaxFrames = 512;

    /// <summary>
    /// Every frame of a thread, innermost first, as rows. A native or internal frame is listed too,
    /// so the shape of the stack is honest, but only a managed frame carries a method to read.
    /// </summary>
    internal static IReadOnlyList<ManagedFrame> Frames(ICorDebugThread thread, ManagedTypes types, uint currentToken, uint currentOffset)
    {
        var frames = new List<ManagedFrame>();
        bool current = true;

        Each(thread, (frame, il) =>
        {
            var described = Describe(frame, il, types, frames.Count, current && il != IntPtr.Zero);
            if (described is { } row)
            {
                frames.Add(row);
                if (row.IsManaged)
                {
                    current = false;
                }
            }

            return frames.Count < MaxFrames;
        });

        return frames;
    }

    /// <summary>
    /// The Nth frame of a thread as an IL frame, owned by the caller, or zero when frame N has no IL
    /// (a native or internal frame) or there is no frame N. Used to read a selected frame's values.
    /// </summary>
    internal static IntPtr IlFrameAt(ICorDebugThread thread, int index)
    {
        IntPtr found = IntPtr.Zero;
        int seen = 0;

        Each(thread, (frame, il) =>
        {
            if (seen++ == index)
            {
                if (il != IntPtr.Zero)
                {
                    Marshal.AddRef(il);
                    found = il;
                }

                return false;
            }

            return true;
        });

        return found;
    }

    /// <summary>
    /// The innermost frame that has IL, owned by the caller, or zero when the thread is in no managed
    /// code at all. This is where a waiting thread's location comes from: its active frame is the
    /// native wait, and the code is a chain down.
    /// </summary>
    internal static IntPtr FirstIlFrame(ICorDebugThread thread)
    {
        IntPtr found = IntPtr.Zero;
        Each(thread, (frame, il) =>
        {
            if (il != IntPtr.Zero)
            {
                Marshal.AddRef(il);
                found = il;
                return false;
            }

            return true;
        });

        return found;
    }

    /// <summary>
    /// Calls <paramref name="visit"/> for each frame, giving it the frame and — when the frame has IL
    /// — a borrowed <see cref="ICorDebugILFrame"/> pointer. Both are released after the call. Stops
    /// when the visitor returns false.
    /// </summary>
    private static void Each(ICorDebugThread thread, Func<IntPtr, IntPtr, bool> visit)
    {
        if (thread.EnumerateChains(out IntPtr chainEnum) < 0 || chainEnum == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var chains = Com.Keep<ICorDebugChainEnum>(chainEnum);
            if (chains is null)
            {
                return;
            }

            try
            {
                while (chains.Next(1, out IntPtr chainPtr, out uint gotChain) == 0 && gotChain == 1 && chainPtr != IntPtr.Zero)
                {
                    bool keepGoing = Chain(chainPtr, visit);
                    Marshal.Release(chainPtr);
                    if (!keepGoing)
                    {
                        return;
                    }
                }
            }
            finally
            {
                Com.Drop(chains);
            }
        }
        finally
        {
            Marshal.Release(chainEnum);
        }
    }

    private static bool Chain(IntPtr chainPtr, Func<IntPtr, IntPtr, bool> visit)
    {
        var chain = Com.Keep<ICorDebugChain>(chainPtr);
        if (chain is null)
        {
            return true;
        }

        try
        {
            if (chain.EnumerateFrames(out IntPtr frameEnum) < 0 || frameEnum == IntPtr.Zero)
            {
                return true;
            }

            var frames = Com.Keep<ICorDebugFrameEnum>(frameEnum);
            Marshal.Release(frameEnum);
            if (frames is null)
            {
                return true;
            }

            try
            {
                while (frames.Next(1, out IntPtr framePtr, out uint gotFrame) == 0 && gotFrame == 1 && framePtr != IntPtr.Zero)
                {
                    // An IL frame answers to ICorDebugILFrame; a native or internal one does not, and
                    // the borrowed pointer is zero for it.
                    IntPtr il = Com.QueryInterface(framePtr, CorDebugGuids.IlFrame);
                    bool keepGoing;
                    try
                    {
                        keepGoing = visit(framePtr, il);
                    }
                    finally
                    {
                        if (il != IntPtr.Zero)
                        {
                            Marshal.Release(il);
                        }

                        Marshal.Release(framePtr);
                    }

                    if (!keepGoing)
                    {
                        return false;
                    }
                }
            }
            finally
            {
                Com.Drop(frames);
            }
        }
        finally
        {
            Com.Drop(chain);
        }

        return true;
    }

    private static ManagedFrame? Describe(IntPtr framePtr, IntPtr il, ManagedTypes types, int index, bool isCurrent)
    {
        if (il == IntPtr.Zero)
        {
            // A frame with no IL: the runtime's own plumbing between managed calls, or native code
            // called through P/Invoke. Worth a row so the stack is not silently shortened.
            return new ManagedFrame(index, string.Empty, 0, 0, "[native or runtime code]", false, false);
        }

        return Com.Borrow<ICorDebugILFrame, ManagedFrame?>(il, frame =>
        {
            uint offset = frame.GetIP(out uint ip, out int mapping) == 0 ? ip : 0;
            _ = mapping;

            if (frame.GetFunction(out var function) < 0 || function is null)
            {
                return new ManagedFrame(index, string.Empty, 0, offset, "[unreadable frame]", false, false);
            }

            try
            {
                uint token = function.GetToken(out uint t) == 0 ? t : 0;
                string? module = function.GetModule(out IntPtr owner) == 0
                    ? Com.Owned<ICorDebugModule, string>(owner, m => Com.NameOf(m))
                    : null;

                string name = module is not null && types.Method(module, token)?.Display is { } display
                    ? display
                    : $"0x{token:X8}";
                string file = module is null ? "(unknown)" : System.IO.Path.GetFileName(module);

                return new ManagedFrame(index, file, token, offset, $"{file}!{name}", true, isCurrent);
            }
            finally
            {
                Com.Drop(function);
            }
        });
    }
}
