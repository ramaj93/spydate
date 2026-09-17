using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using Spydate.Core.Text;

namespace Spydate.App.Views.Controls;

/// <summary>
/// The strip down the left of a listing where breakpoints live.
///
/// A marker in the text was the first attempt and it was the wrong one: an asterisk among hex reads
/// as punctuation, and it shifted the address column of every line to make room. A margin costs the
/// same space whether or not anything is in it, is scanned down one column rather than read, and —
/// the part that matters most — can be clicked. Setting a breakpoint by clicking beside the
/// instruction is how every debugger does it, and it needs no menu, no shortcut and no caret.
/// </summary>
public sealed class BreakpointMargin : AbstractMargin
{
    private const double StripWidth = 18;
    private const double DotRadius = 5;

    private readonly CodeEditor _editor;

    public BreakpointMargin(CodeEditor editor)
    {
        _editor = editor;
        Cursor = Cursors.Hand;
        ToolTip = "Click to set or clear a breakpoint";
    }

    protected override Size MeasureOverride(Size availableSize) => new(StripWidth, 0);

    private LineAddressMap _map = LineAddressMap.Empty;
    private int _mapped = -1;

    /// <summary>
    /// The document's addresses, rebuilt only when the text changes. Rendering happens on every
    /// scroll and every caret move; parsing a few thousand lines each time would be felt.
    /// </summary>
    private LineAddressMap Map(ICSharpCode.AvalonEdit.Document.TextDocument document)
    {
        if (_mapped != document.Version?.GetHashCode())
        {
            _map = LineAddressMap.Build(document.Text);
            _mapped = document.Version?.GetHashCode() ?? -1;
        }

        return _map;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        var view = TextView;
        if (view?.VisualLinesValid != true || Document is null)
        {
            return;
        }

        // A quiet background, so the strip reads as part of the gutter rather than as empty page.
        drawingContext.DrawRectangle(Brush("Editor.Background", Brushes.Transparent), null, new Rect(0, 0, StripWidth, ActualHeight));

        var breakpoints = _editor.BreakpointAddresses;
        var disabled = _editor.DisabledBreakpointAddresses;
        ulong? current = _editor.ExecutionAddress;

        // Which line the arrow belongs on, asked of the whole document rather than matched against
        // each line as it is drawn. Most stops have a line of their own and the two agree; the ones
        // that do not are the reason this exists. A `for` header is three statements in IL — the
        // initialiser, the condition compiled after the body, the increment — and only the first of
        // them has a line to itself, so stepping round a loop used to blank the arrow for two
        // presses out of three. The last line at or before the address is the statement that
        // instruction ended up inside, which is what the reader means by "where it is".
        int? arrow = current is { } stopped ? Map(Document).LineFor(stopped) : null;

        foreach (var line in view.VisualLines)
        {
            ulong? address = AddressText.FromLine(Document.GetText(line.FirstDocumentLine.Offset, line.FirstDocumentLine.Length));
            if (address is not { } va)
            {
                continue;
            }

            double middle = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextMiddle) - view.VerticalOffset;

            if (arrow == line.FirstDocumentLine.LineNumber)
            {
                // The instruction about to run, called out before the breakpoint on the same line:
                // where execution actually is matters more than what put it there.
                DrawArrow(drawingContext, middle);
            }

            if (breakpoints?.Contains(va) == true)
            {
                // A disabled breakpoint draws hollow — outline only, no fill — so it reads as present
                // but not firing, the way dnSpy shows one that is switched off. An enabled one is the
                // filled dot it has always been.
                bool off = disabled?.Contains(va) == true;
                drawingContext.DrawEllipse(
                    off ? Brushes.Transparent : Brush("Debug.Breakpoint", Brushes.Firebrick),
                    new Pen(Brush("Debug.BreakpointEdge", Brushes.DarkRed), off ? 1.5 : 1),
                    new Point(StripWidth / 2, middle),
                    DotRadius,
                    DotRadius);
            }
        }
    }

    /// <summary>A filled triangle pointing at the line, the way every debugger marks the next instruction.</summary>
    private void DrawArrow(DrawingContext drawingContext, double middle)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(2, middle - 5), isFilled: true, isClosed: true);
            context.LineTo(new Point(StripWidth - 3, middle), isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(2, middle + 5), isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(Brush("Debug.Current", Brushes.Gold), null, geometry);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseDown(e);

        if (e.ChangedButton != MouseButton.Left || TextView?.VisualLinesValid != true || Document is null)
        {
            return;
        }

        double y = e.GetPosition(this).Y + TextView.VerticalOffset;
        var line = TextView.GetVisualLineFromVisualTop(y);
        if (line is null)
        {
            return;
        }

        string text = Document.GetText(line.FirstDocumentLine.Offset, line.FirstDocumentLine.Length);
        if (AddressText.FromLine(text) is { } va && _editor.ToggleBreakpointCommand is { } command && command.CanExecute(va))
        {
            command.Execute(va);
            e.Handled = true;
        }
    }

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null)
        {
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        }

        if (newTextView is not null)
        {
            newTextView.VisualLinesChanged += OnVisualLinesChanged;
        }

        base.OnTextViewChanged(oldTextView, newTextView);
        InvalidateVisual();
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    private static Brush Brush(string key, Brush fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? fallback;
}
