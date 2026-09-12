using System.Runtime.InteropServices;

namespace Spydate.Debugger.Managed;

/// <summary>
/// Borrowing and owning COM pointers, kept in one place because the two are easy to confuse and the
/// confusion is expensive.
///
/// A pointer handed to a callback is <em>borrowed</em>: it is valid for that call and no longer, and
/// nothing must be released that was not first claimed. A pointer returned through an out parameter
/// is <em>owned</em>: the callee has already added a reference and the caller must drop it. Getting
/// the first wrong keeps a dead app domain alive; getting the second wrong leaks a reference per
/// stop, which nothing notices until a long session will not shut down.
/// </summary>
internal static class Com
{
    /// <summary>
    /// Uses a borrowed pointer as an interface and lets it go again.
    ///
    /// The wrapper adds a reference of its own and this takes it back off, so the caller's borrowed
    /// pointer is left exactly as it was found.
    /// </summary>
    internal static TResult? Borrow<TInterface, TResult>(IntPtr pointer, Func<TInterface, TResult?> use)
        where TInterface : class
    {
        if (pointer == IntPtr.Zero)
        {
            return default;
        }

        object? wrapper = null;
        try
        {
            wrapper = Marshal.GetObjectForIUnknown(pointer);
            return wrapper is TInterface typed ? use(typed) : default;
        }
        catch (Exception ex) when (ex is InvalidCastException or COMException)
        {
            return default;
        }
        finally
        {
            if (wrapper is not null)
            {
                Marshal.ReleaseComObject(wrapper);
            }
        }
    }

    /// <summary>
    /// Uses a pointer that came back from a call, and releases it. The reference belongs to us.
    /// </summary>
    internal static TResult? Owned<TInterface, TResult>(IntPtr pointer, Func<TInterface, TResult?> use)
        where TInterface : class
    {
        if (pointer == IntPtr.Zero)
        {
            return default;
        }

        object? wrapper = null;
        try
        {
            wrapper = Marshal.GetObjectForIUnknown(pointer);
            return wrapper is TInterface typed ? use(typed) : default;
        }
        catch (Exception ex) when (ex is InvalidCastException or COMException)
        {
            return default;
        }
        finally
        {
            if (wrapper is not null)
            {
                Marshal.ReleaseComObject(wrapper);
            }

            // The wrapper's reference and the one the call gave us are two separate things.
            Marshal.Release(pointer);
        }
    }

    /// <summary>Takes lasting ownership of a borrowed pointer, for the few objects worth keeping.</summary>
    internal static TInterface? Keep<TInterface>(IntPtr pointer)
        where TInterface : class
    {
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.GetObjectForIUnknown(pointer) as TInterface;
        }
        catch (Exception ex) when (ex is InvalidCastException or COMException)
        {
            return null;
        }
    }

    /// <summary>Lets go of something <see cref="Keep{TInterface}"/> took.</summary>
    internal static void Drop(object? wrapper)
    {
        if (wrapper is not null)
        {
            try
            {
                Marshal.ReleaseComObject(wrapper);
            }
            catch (ArgumentException)
            {
                // Not a wrapper any more; nothing to give back.
            }
        }
    }

    /// <summary>
    /// A module's path, which the API reports in the usual two-call shape: ask for the length, then
    /// ask again with somewhere to put it.
    /// </summary>
    internal static string? NameOf(ICorDebugModule module)
    {
        if (module.GetName(0, out uint length, IntPtr.Zero) < 0 || length == 0 || length > 0x8000)
        {
            return null;
        }

        // Unmanaged, because the buffer goes to a COM method on a [ComImport] interface, where an
        // array parameter would be marshalled as a SAFEARRAY and the callee is writing plain
        // characters. Allocating it here keeps the marshaller out of a path that runs on the
        // runtime's callback thread, where a fault is not an exception but a missing process.
        IntPtr buffer = Marshal.AllocCoTaskMem((int)length * sizeof(char));
        try
        {
            if (module.GetName(length, out uint written, buffer) < 0 || written == 0)
            {
                return null;
            }

            // The count includes the terminator, which is not part of the name.
            int used = (int)Math.Min(written, length);
            string name = Marshal.PtrToStringUni(buffer, used) ?? string.Empty;
            return name.TrimEnd('\0') is { Length: > 0 } trimmed ? trimmed : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }
}

/// <summary>
/// Turning a breakpoint on, through the vtable rather than through an interface.
///
/// <c>Activate</c> is the first method of <c>ICorDebugBreakpoint</c>, which every kind of breakpoint
/// derives from, so it sits immediately after <c>IUnknown</c> on all of them — slot three. Calling
/// it there needs no IID and so cannot be defeated by having one wrong, which is exactly how this
/// arrived: a typed out-parameter had the marshaller ask a perfectly good function breakpoint to
/// prove it was one, and it answered E_NOINTERFACE because the GUID written here was not its.
/// </summary>
internal static class Activation
{
    /// <summary>Slots zero to two are IUnknown; the interface's own methods start after them.</summary>
    private const int ActivateSlot = 3;

    internal static unsafe int Set(IntPtr breakpoint, bool on)
    {
        if (breakpoint == IntPtr.Zero)
        {
            return unchecked((int)0x80004003);   // E_POINTER
        }

        void** vtable = *(void***)breakpoint;
        var activate = (delegate* unmanaged<IntPtr, int, int>)vtable[ActivateSlot];
        return activate(breakpoint, on ? 1 : 0);
    }
}
