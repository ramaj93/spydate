using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using Spydate.App.Services;
using Spydate.Core.Text;

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

    /// <summary>Line to move to and mark, 1-based. Zero leaves the editor alone.</summary>
    public static readonly DependencyProperty RevealLineProperty = DependencyProperty.Register(
        nameof(RevealLine), typeof(int), typeof(CodeEditor),
        new FrameworkPropertyMetadata(0, OnRevealLineChanged));

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

        TextArea.Caret.PositionChanged += (_, _) => UpdateCaretContext();
        PreviewMouseRightButtonDown += MoveCaretToClick;
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

        var target = editor.Document.GetLineByNumber(line);
        if (editor.TextArea.Caret.Line != line)
        {
            editor.TextArea.Caret.Offset = target.Offset;
        }

        editor.ScrollToLine(line);
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
        if (editor.Text != text)
        {
            editor.Text = text;
            editor.ScrollToHome();
            editor.UpdateCaretContext();
        }
    }

    private static void OnHighlightingNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (CodeEditor)d;
        editor.SyntaxHighlighting = HighlightingService.Get(e.NewValue as string ?? string.Empty);
    }
}
