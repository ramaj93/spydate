using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Spydate.App.Views.Controls;

namespace Spydate.App.Views.Chat;

/// <summary>
/// Paints the transcript's selection and its current find match over whatever the list has realized.
///
/// One adorner over the scrolling surface, not a highlight per message: the selection is a pair of
/// flat-text offsets the <see cref="ChatTranscript"/> owns, and this asks it to paint the rectangles
/// those offsets cover on the blocks currently on screen. The fills are translucent so the text shows
/// through, and the selection dims when the list does not have focus, the way a text control's does.
///
/// It repaints only when told to — a scroll, a line re-rendering, the selection moving. It used to
/// repaint on <c>LayoutUpdated</c>, which fires for every layout pass anywhere in the window: with a
/// selection on screen, every streamed token and every opening menu re-walked every realized block's
/// text, and the whole application slowed to match.
/// </summary>
internal sealed class SelectionAdorner : Adorner
{
    private readonly ChatTranscript _transcript;

    private static readonly Brush Match = Frozen(Color.FromArgb(0x99, 0xF2, 0xC7, 0x44));

    public SelectionAdorner(UIElement surface, ChatTranscript transcript)
        : base(surface)
    {
        _transcript = transcript;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        // Clip to the viewport: a block that is partly scrolled in must not paint over the toolbar.
        drawingContext.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        _transcript.Paint(drawingContext, _transcript.IsKeyboardFocusWithin ? Active : Inactive, Match);
        drawingContext.Pop();
    }

    private static Brush Active => Wash("Selection.Background", Color.FromArgb(0x66, 0x09, 0x47, 0x71));

    private static Brush Inactive => Wash("Selection.Inactive", Color.FromArgb(0x66, 0x3F, 0x3F, 0x46));

    private static Brush Wash(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
        {
            var colour = brush.Color;
            return Frozen(Color.FromArgb(0x66, colour.R, colour.G, colour.B));
        }

        return Frozen(fallback);
    }

    private static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
