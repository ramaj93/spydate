using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using Spydate.App.Services;
using Spydate.Core.Text;
using Spydate.Decompiler.Managed;

namespace Spydate.App.Views.Controls;

/// <summary>
/// Read-only AvalonEdit editor with bindable <see cref="BoundText"/> and <see cref="HighlightingName"/>.
/// Colours come from the application palette (Editor.* keys); metrics are deliberately dense.
/// </summary>
public sealed class CodeEditor : TextEditor
{
    public static readonly DependencyProperty BoundTextProperty = DependencyProperty.Register(
        nameof(BoundText), typeof(string), typeof(CodeEditor),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundTextChanged));

    public static readonly DependencyProperty HighlightingNameProperty = DependencyProperty.Register(
        nameof(HighlightingName), typeof(string), typeof(CodeEditor),
        new FrameworkPropertyMetadata(string.Empty, OnHighlightingNameChanged));

    /// <summary>Address of the line the caret is on, when the text carries one.</summary>
    public static readonly DependencyProperty CaretAddressProperty = DependencyProperty.Register(
        nameof(CaretAddress), typeof(ulong?), typeof(CodeEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>Identifier under the caret, so a name can be acted on where it is read.</summary>
    public static readonly DependencyProperty CaretWordProperty = DependencyProperty.Register(
        nameof(CaretWord), typeof(string), typeof(CodeEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>Breakpoints to draw in the margin, by the address the listing states.</summary>
    public static readonly DependencyProperty BreakpointAddressesProperty = DependencyProperty.Register(
        nameof(BreakpointAddresses), typeof(IReadOnlySet<ulong>), typeof(CodeEditor),
        new FrameworkPropertyMetadata(null, OnMarginDataChanged));

    /// <summary>Where execution is stopped, marked with an arrow. Null when nothing is running.</summary>
    public static readonly DependencyProperty ExecutionAddressProperty = DependencyProperty.Register(
        nameof(ExecutionAddress), typeof(ulong?), typeof(CodeEditor),
        new FrameworkPropertyMetadata(null, OnMarginDataChanged));

    /// <summary>
    /// Bumped by the view model whenever the breakpoint set changes. A set is not observable, so
    /// without a value that actually changes the margin has no way to know it should redraw.
    /// </summary>
    public static readonly DependencyProperty BreakpointsVersionProperty = DependencyProperty.Register(
        nameof(BreakpointsVersion), typeof(int), typeof(CodeEditor),
        new FrameworkPropertyMetadata(0, OnMarginDataChanged));

    /// <summary>Invoked with the address of the clicked line.</summary>
    public static readonly DependencyProperty ToggleBreakpointCommandProperty = DependencyProperty.Register(
        nameof(ToggleBreakpointCommand), typeof(System.Windows.Input.ICommand), typeof(CodeEditor),
        new FrameworkPropertyMetadata(null));

    /// <summary>Line to move to and mark, 1-based. Zero leaves the editor alone.</summary>
    public static readonly DependencyProperty RevealLineProperty = DependencyProperty.Register(
        nameof(RevealLine), typeof(int), typeof(CodeEditor),
        new FrameworkPropertyMetadata(0, OnRevealLineChanged));

    /// <summary>Identifiers in the text that name a type or member, for click-to-definition.</summary>
    public static readonly DependencyProperty ReferencesProperty = DependencyProperty.Register(
        nameof(References), typeof(IReadOnlyList<SourceReference>), typeof(CodeEditor),
        new FrameworkPropertyMetadata(null));

    /// <summary>Invoked with the <see cref="SourceReference"/> under a click.</summary>
    public static readonly DependencyProperty GoToDefinitionCommandProperty = DependencyProperty.Register(
        nameof(GoToDefinitionCommand), typeof(System.Windows.Input.ICommand), typeof(CodeEditor),
        new FrameworkPropertyMetadata(null));

    public CodeEditor()
    {
        IsReadOnly = true;
        ShowLineNumbers = true;
        WordWrap = false;
        FontFamily = Resource<FontFamily>("Mono.FontFamily") ?? new FontFamily("Consolas");
        FontSize = 12.5;
        Padding = new Thickness(4, 2, 4, 2);
        BorderThickness = new Thickness(0);
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        SetResourceReference(BackgroundProperty, "Editor.Background");
        SetResourceReference(ForegroundProperty, "Editor.Foreground");
        SetResourceReference(LineNumbersForegroundProperty, "Editor.LineNumbers");

        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        Options.ConvertTabsToSpaces = true;
        Options.HighlightCurrentLine = true;
        Options.AllowScrollBelowDocument = false;
        Options.EnableRectangularSelection = true;
        Options.ShowBoxForControlCharacters = false;

        var view = TextArea.TextView;
        view.CurrentLineBackground = Resource<Brush>("Editor.CurrentLine") ?? Brushes.Transparent;
        view.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        view.LinkTextForegroundBrush = Resource<Brush>("Accent.Hover") ?? Brushes.SteelBlue;
        view.ElementGenerators.Clear();

        TextArea.SelectionBrush = Resource<Brush>("Editor.Selection") ?? Brushes.SteelBlue;
        TextArea.SelectionBorder = null;
        TextArea.SelectionCornerRadius = 0;
        TextArea.SelectionForeground = null;
        TextArea.Caret.CaretBrush = Resource<Brush>("Text.Primary") ?? Brushes.White;
        TextArea.LeftMargins.CollectionChanged += (_, _) => StyleLineNumberMargin();
        StyleLineNumberMargin();

        TextArea.LeftMargins.Insert(0, new BreakpointMargin(this));
        TextArea.Caret.PositionChanged += (_, _) => UpdateCaretContext();
        PreviewMouseRightButtonDown += MoveCaretToClick;
        PreviewMouseLeftButtonDown += NoteReferenceUnderPress;
        PreviewMouseLeftButtonUp += FollowReferenceOnClick;

        // handledEventsToo, because the text view sets the I-beam by handling QueryCursor itself, and
        // a plain += would never see the event once it had. This runs after and overrides it, but only
        // where there is a reference to follow — everywhere else the I-beam stands.
        AddHandler(Mouse.QueryCursorEvent, new QueryCursorEventHandler(ShowHandOverReference), handledEventsToo: true);
    }

    public IReadOnlyList<SourceReference>? References
    {
        get => (IReadOnlyList<SourceReference>?)GetValue(ReferencesProperty);
        set => SetValue(ReferencesProperty, value);
    }

    public System.Windows.Input.ICommand? GoToDefinitionCommand
    {
        get => (System.Windows.Input.ICommand?)GetValue(GoToDefinitionCommandProperty);
        set => SetValue(GoToDefinitionCommandProperty, value);
    }

    /// <summary>The reference the left button went down on, and where, so the release can follow it.</summary>
    private SourceReference? _pressedReference;
    private Point _pressedPoint;

    /// <summary>
    /// Clicking an identifier that names a type or member follows it to its definition — a plain click,
    /// the way dnSpy navigates, with no modifier.
    ///
    /// The press over a reference is handled here, which stops the text view from starting a selection
    /// and taking the mouse: a reference is a link, not text to sweep over, and letting the selection
    /// begin was what left the editor stuck in select mode once the release was swallowed. The follow
    /// happens on release so a press-and-hold that wanders off does not navigate; a press on ordinary
    /// text is left alone, so selecting and copying code still works everywhere but on a link.
    /// </summary>
    private void NoteReferenceUnderPress(object sender, MouseButtonEventArgs e)
    {
        _pressedPoint = e.GetPosition(this);
        _pressedReference = ReferenceAt(_pressedPoint);
        if (_pressedReference is not null)
        {
            e.Handled = true;   // a link: do not let the text view select or capture the mouse
        }
    }

    private void FollowReferenceOnClick(object sender, MouseButtonEventArgs e)
    {
        if (_pressedReference is not { } reference || GoToDefinitionCommand is not { } command)
        {
            _pressedReference = null;
            return;
        }

        _pressedReference = null;
        e.Handled = true;

        var released = e.GetPosition(this);
        if (Math.Abs(released.X - _pressedPoint.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(released.Y - _pressedPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            return;   // the press wandered off the link before releasing — not a click
        }

        if (ReferenceAt(released) == reference && command.CanExecute(reference))
        {
            command.Execute(reference);
        }
    }

    /// <summary>
    /// A hand cursor over a navigable identifier, so it reads as a link rather than editable text.
    /// Handled on the editor so it wins over the text view's own I-beam, and suppressed mid-drag so a
    /// selection sweeping across identifiers does not flicker.
    /// </summary>
    private void ShowHandOverReference(object sender, QueryCursorEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Released && ReferenceAt(e.GetPosition(this)) is not null)
        {
            e.Cursor = Cursors.Hand;
            e.Handled = true;
        }
    }

    /// <summary>
    /// The reference covering a point, or null. A click maps to a line and column, and a reference
    /// covering that column on that line is the one under it — the same 1-based counting the
    /// decompiler recorded them in.
    /// </summary>
    private SourceReference? ReferenceAt(Point point)
    {
        if (References is not { Count: > 0 } references || GetPositionFromPoint(point) is not { } position)
        {
            return null;
        }

        foreach (var reference in references)
        {
            if (reference.Line == position.Line
                && position.Column >= reference.Column
                && position.Column < reference.Column + reference.Length)
            {
                return reference;
            }
        }

        return null;
    }

    public string BoundText
    {
        get => (string)GetValue(BoundTextProperty);
        set => SetValue(BoundTextProperty, value);
    }

    public string HighlightingName
    {
        get => (string)GetValue(HighlightingNameProperty);
        set => SetValue(HighlightingNameProperty, value);
    }

    public IReadOnlySet<ulong>? BreakpointAddresses
    {
        get => (IReadOnlySet<ulong>?)GetValue(BreakpointAddressesProperty);
        set => SetValue(BreakpointAddressesProperty, value);
    }

    public ulong? ExecutionAddress
    {
        get => (ulong?)GetValue(ExecutionAddressProperty);
        set => SetValue(ExecutionAddressProperty, value);
    }

    public int BreakpointsVersion
    {
        get => (int)GetValue(BreakpointsVersionProperty);
        set => SetValue(BreakpointsVersionProperty, value);
    }

    public System.Windows.Input.ICommand? ToggleBreakpointCommand
    {
        get => (System.Windows.Input.ICommand?)GetValue(ToggleBreakpointCommandProperty);
        set => SetValue(ToggleBreakpointCommandProperty, value);
    }

    public ulong? CaretAddress
    {
        get => (ulong?)GetValue(CaretAddressProperty);
        set => SetValue(CaretAddressProperty, value);
    }

    public string? CaretWord
    {
        get => (string?)GetValue(CaretWordProperty);
        set => SetValue(CaretWordProperty, value);
    }

    public int RevealLine
    {
        get => (int)GetValue(RevealLineProperty);
        set => SetValue(RevealLineProperty, value);
    }

    /// <summary>
    /// Scrolls a line into view and puts the caret on it, which is also what marks it: the editor already
    /// paints the caret's line, so the two panes agree without a second kind of highlight.
    /// </summary>
    private static void OnRevealLineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (CodeEditor)d;
        int line = (int)e.NewValue;
        if (line <= 0 || editor.Document is null || line > editor.Document.LineCount)
        {
            return;
        }

        editor.Reveal(line);
    }

    /// <summary>
    /// Puts the caret where the right button went down. WPF opens a context menu without moving the
    /// caret, so without this the menu would act on wherever the caret was last left.
    /// </summary>
    private void MoveCaretToClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var position = GetPositionFromPoint(e.GetPosition(this));
        if (position is { } location)
        {
            TextArea.Caret.Position = location;
        }
    }

    /// <summary>Publishes what the caret is on, so commands can act on the address the user is looking at.</summary>
    private void UpdateCaretContext()
    {
        var line = Document?.GetLineByOffset(Math.Min(CaretOffset, Document.TextLength));
        if (line is null)
        {
            CaretAddress = null;
            CaretWord = null;
            return;
        }

        string text = Document!.GetText(line.Offset, line.Length);
        CaretAddress = AddressText.FromLine(text);
        CaretWord = AddressText.WordAt(text, CaretOffset - line.Offset);
    }

    /// <summary>Gives the line-number gutter a separator line, the way IDE editors draw it.</summary>
    private void StyleLineNumberMargin()
    {
        foreach (var margin in TextArea.LeftMargins)
        {
            if (margin is System.Windows.Shapes.Line line)
            {
                line.Stroke = Resource<Brush>("Chrome.Border") ?? Brushes.Gray;
            }
            else if (margin is FrameworkElement element)
            {
                element.Margin = new Thickness(2, 0, 4, 0);
            }
        }
    }

    private T? Resource<T>(string key) where T : class => TryFindResource(key) as T ?? Application.Current?.TryFindResource(key) as T;

    private static void OnBoundTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (CodeEditor)d;
        string text = e.NewValue as string ?? string.Empty;
        if (editor.Text == text)
        {
            return;
        }

        editor.Text = text;
        editor.ScrollToHome();

        // Back to the line the document is about, if it has one. New text resets the view to the
        // top, and a listing is re-read whenever a patch or a breakpoint changes how it is marked —
        // so without this, every toggle throws away the place the analyst was reading and puts the
        // caret on a header comment, where the very commands they were using go grey.
        editor.Reveal(editor.RevealLine);
        editor.UpdateCaretContext();
    }

    /// <summary>Moves the caret to a 1-based line and scrolls it into view. Zero leaves things alone.</summary>
    private void Reveal(int line)
    {
        if (line <= 0 || Document is null || line > Document.LineCount)
        {
            return;
        }

        TextArea.Caret.Offset = Document.GetLineByNumber(line).Offset;
        ScrollToLine(line);
    }

    private static void OnMarginDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        foreach (var margin in ((CodeEditor)d).TextArea.LeftMargins)
        {
            if (margin is BreakpointMargin breakpoints)
            {
                breakpoints.InvalidateVisual();
            }
        }
    }

    private static void OnHighlightingNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (CodeEditor)d;
        editor.SyntaxHighlighting = HighlightingService.Get(e.NewValue as string ?? string.Empty);
    }
}
