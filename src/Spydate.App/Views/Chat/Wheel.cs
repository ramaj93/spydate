using System.Windows;
using System.Windows.Input;

namespace Spydate.App.Views.Chat;

/// <summary>
/// Hands the mouse wheel back to the transcript when an inner scroller has nothing to do with it.
///
/// A <see cref="System.Windows.Controls.ScrollViewer"/> swallows the wheel even when it cannot
/// scroll in the direction asked, so the panel stopped moving whenever the pointer was over a wide
/// table. The event is raised again on the parent, which is where it would have gone.
/// </summary>
internal static class Wheel
{
    public static void ForwardFrom(UIElement element)
    {
        element.PreviewMouseWheel += (sender, e) =>
        {
            if (e.Handled || sender is not UIElement source || source is not FrameworkElement { Parent: UIElement parent })
            {
                return;
            }

            e.Handled = true;
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = source,
            });
        };
    }
}
