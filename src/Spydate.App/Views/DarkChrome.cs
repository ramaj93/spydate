using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Spydate.App.Views;

/// <summary>
/// Asks Windows to draw a window's title bar dark.
///
/// The main window does not need this: it is a <c>FluentWindow</c> with
/// <c>ExtendsContentIntoTitleBar</c>, so Wpf.Ui draws the title bar itself, inside the client area,
/// and themes it like everything else. The dialogs are plain <see cref="Window"/>s, and a plain
/// window's title bar is the non-client area — drawn by Windows, in the system's own light chrome,
/// however the client area underneath it is themed. So every dialog in the application had a white
/// title bar over a dark body.
///
/// This is the small half of the fix. The other half would be making the dialogs
/// <c>FluentWindow</c>s too, which is the tidier end state and a real change to each one's layout:
/// the title bar becomes content, so everything below it shifts. This asks DWM for a dark title bar
/// instead and leaves the layout alone.
/// </summary>
internal static class DarkChrome
{
    /// <summary>
    /// <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c>. It was 19 while the feature was undocumented, and became
    /// 20 in Windows 10 build 18985. Both are tried rather than the build being interrogated: the
    /// call is cheap, it either takes or it does not, and a title bar is not worth a version check.
    /// </summary>
    private const int DarkMode = 20;

    private const int DarkModeBefore18985 = 19;

    /// <summary>
    /// <c>DllImport</c> rather than the <c>LibraryImport</c> used elsewhere, and not an oversight.
    /// The source generator emits an unsafe context, and <c>Spydate.App</c> does not allow unsafe
    /// code — turning that on for the whole application to colour a title bar would be a permanent
    /// loosening for a cosmetic call. This one is a blittable <c>ref int</c>, which the old marshaller
    /// handles without any of that.
    /// </summary>
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>
    /// Makes this window's title bar dark once it has a handle to ask about.
    ///
    /// Hooked to <see cref="Window.SourceInitialized"/> rather than done in the caller's constructor,
    /// because there is no window handle until then and the call would silently do nothing. It is also
    /// before the first paint, so the title bar comes up dark rather than turning dark.
    /// </summary>
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        window.SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            int on = 1;
            if (DwmSetWindowAttribute(handle, DarkMode, ref on, sizeof(int)) != 0)
            {
                // Older Windows 10. Nothing is done about a second failure: this is what the window
                // looks like, not whether it works, and a dialog that will not go dark is still a
                // dialog.
                DwmSetWindowAttribute(handle, DarkModeBefore18985, ref on, sizeof(int));
            }
        };
    }
}
