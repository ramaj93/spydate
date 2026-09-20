using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Spydate.Agent.Text;

namespace Spydate.App.Views.Chat;

/// <summary>
/// Draws a parsed answer into WPF controls, and records the flat-text slice every text block covers
/// as it goes. It is an <see cref="IMarkdownSink"/>, so it is the same walk that produces the flat
/// text a line reads as — the offsets it registers are offsets into that text, not a second count
/// that has to be kept in step with it.
///
/// Deliberately thin, like the renderer it replaces: it decides fonts, spacing and colour and
/// nothing else. What is drawn where is <see cref="MarkdownWalk"/>'s to say.
/// </summary>
internal sealed class MarkdownView : IMarkdownSink
{
    private const double Left = 10;
    private const double Right = 22;

    private readonly StackPanel _root = new();
    private readonly List<TextSlice> _slices = [];
    private readonly Stack<Panel> _hosts = new();
    private readonly Stack<DockPanel> _items = new();
    private readonly Stack<TableFrame> _tables = new();

    private TextBlock? _inline;
    private int _inlineBase;
    private int _offset;

    private MarkdownView() => _hosts.Push(_root);

    /// <summary>Renders the blocks, giving the panel to show and the slices to select and highlight over.</summary>
    public static (FrameworkElement Root, IReadOnlyList<TextSlice> Slices) Render(IReadOnlyList<MarkdownBlock> blocks)
    {
        var view = new MarkdownView();
        MarkdownWalk.Walk(blocks, view);
        return (view._root, view._slices);
    }

    public void Open(BlockKind kind, BlockInfo info)
    {
        switch (kind)
        {
            case BlockKind.Paragraph:
                StartInline(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(Left, 0, Right, 0) });
                break;

            case BlockKind.Heading:
                StartInline(new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(Left, 8, Right, 2),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = info.Level <= 2 ? 15 : 13,
                });
                break;

            case BlockKind.Code:
                OpenCode(info.Language);
                break;

            case BlockKind.Quote:
                OpenQuote();
                break;

            case BlockKind.Rule:
                Host.Children.Add(new Border
                {
                    Height = 1,
                    Background = Res("Chrome.Border") ?? Brushes.Gray,
                    Margin = new Thickness(Left, 6, Right, 6),
                });
                break;

            case BlockKind.List:
                var list = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
                Host.Children.Add(list);
                _hosts.Push(list);
                break;

            case BlockKind.Item:
                OpenItem();
                break;

            case BlockKind.Table:
                OpenTable();
                break;

            case BlockKind.Row:
                _tables.Peek().StartRow();
                break;

            case BlockKind.Cell:
                OpenCell(info);
                break;
        }
    }

    public void Close(BlockKind kind)
    {
        switch (kind)
        {
            case BlockKind.Paragraph:
            case BlockKind.Heading:
            case BlockKind.Code:
            case BlockKind.Cell:
                CommitInline();
                break;

            case BlockKind.Quote:
            case BlockKind.List:
                _hosts.Pop();
                break;

            case BlockKind.Item:
                CloseItem();
                break;

            case BlockKind.Table:
                _tables.Pop();
                break;
        }
    }

    public void Text(string text, SpanInfo style)
    {
        if (_inline is null)
        {
            // Text outside any block: keep the offset true and drop nothing, even if nothing draws it.
            _offset += text.Length;
            return;
        }

        if (style.Is(SpanStyle.Link))
        {
            _offset += AppendLink(_inline, text, style);
            return;
        }

        _offset += ChatText.Append(_inline, text, run => Style(run, style));
    }

    public void Marker(string text)
    {
        var marker = new TextBlock
        {
            Foreground = Res("Text.Secondary") ?? Brushes.Gray,
            Margin = new Thickness(Left, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };

        int at = _offset;
        _offset += ChatText.Append(marker, text);
        _slices.Add(new TextSlice(marker, at, _offset - at));

        DockPanel.SetDock(marker, Dock.Left);
        _items.Peek().Children.Add(marker);
    }

    public void Glue(string text) => _offset += text.Length;

    private Panel Host => _hosts.Peek();

    private void StartInline(TextBlock block)
    {
        block.Foreground = Res("Text.Primary") ?? Brushes.White;
        Host.Children.Add(block);
        _inline = block;
        _inlineBase = _offset;
    }

    private void CommitInline()
    {
        if (_inline is { } block && _offset > _inlineBase)
        {
            _slices.Add(new TextSlice(block, _inlineBase, _offset - _inlineBase));
        }

        _inline = null;
    }

    private void OpenQuote()
    {
        var inner = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
        var border = new Border
        {
            Child = inner,
            BorderBrush = Res("Chrome.Border") ?? Brushes.Gray,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Margin = new Thickness(Left, 2, Right, 2),
        };

        Host.Children.Add(border);
        _hosts.Push(inner);
    }

    private void OpenItem()
    {
        var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 2) };
        Host.Children.Add(dock);

        var content = new StackPanel();
        _items.Push(dock);
        _hosts.Push(content);
    }

    private void CloseItem()
    {
        var content = (StackPanel)_hosts.Pop();
        var dock = _items.Pop();

        // A list item's own paragraph carries the block left inset; drop it so the words sit right
        // after the marker instead of a full indent past it.
        foreach (var child in content.Children.OfType<TextBlock>())
        {
            if (child.Margin.Left == Left)
            {
                child.Margin = new Thickness(0, child.Margin.Top, child.Margin.Right, child.Margin.Bottom);
            }
        }

        // The content is the fill child, added after the marker that was docked left, so the item is
        // "marker | its blocks" and a nested list sits under the words rather than beside them.
        dock.Children.Add(content);
    }

    private void OpenCode(string? language)
    {
        var listing = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = Mono,
            FontSize = 12,
            Foreground = Res("Text.Primary") ?? Brushes.White,
        };

        var stack = new StackPanel();
        stack.Children.Add(Strip(language, listing));
        stack.Children.Add(listing);

        Host.Children.Add(new Border
        {
            Child = stack,
            Background = Res("Control.Background") ?? new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(Left, 4, Right, 8),
        });

        _inline = listing;
        _inlineBase = _offset;
    }

    private void OpenTable()
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        var scroll = new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(Left, 2, Right, 2),
        };

        // A table wide enough to scroll must not eat the wheel the transcript is trying to move by.
        Wheel.ForwardFrom(scroll);
        Host.Children.Add(scroll);
        _tables.Push(new TableFrame(grid));
    }

    private void OpenCell(BlockInfo info)
    {
        var frame = _tables.Peek();
        var cell = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(6, 2, 6, 2),
            Foreground = Res("Text.Primary") ?? Brushes.White,
            FontWeight = info.IsHeader ? FontWeights.SemiBold : FontWeights.Normal,
            TextAlignment = info.Align switch
            {
                TableAlign.Center => TextAlignment.Center,
                TableAlign.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            },
        };

        var border = new Border
        {
            Child = cell,
            BorderBrush = Res("Grid.Line") ?? Brushes.Gray,
            BorderThickness = new Thickness(0, 0, 1, 1),
            Background = info.IsHeader ? Res("Grid.Header.Background") : null,
        };

        frame.Place(border);
        Grid.SetRow(border, frame.Row);
        Grid.SetColumn(border, frame.Column);

        _inline = cell;
        _inlineBase = _offset;
    }

    private static void Style(Run run, SpanInfo style)
    {
        if (style.Is(SpanStyle.Strong))
        {
            run.FontWeight = FontWeights.SemiBold;
        }

        if (style.Is(SpanStyle.Emphasis))
        {
            run.FontStyle = FontStyles.Italic;
        }

        if (style.Is(SpanStyle.Strike))
        {
            run.TextDecorations = TextDecorations.Strikethrough;
        }

        if (style.Is(SpanStyle.Code))
        {
            run.FontFamily = Mono;
            run.Background = Res("Control.Background") ?? new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
        }
    }

    private static int AppendLink(TextBlock block, string text, SpanInfo style)
    {
        var run = new Run(text);
        Style(run, style);

        var link = new Hyperlink(run)
        {
            Foreground = Res("Accent") ?? Brushes.SteelBlue,
            ToolTip = style.Url,
        };

        if (Uri.TryCreate(style.Url, UriKind.Absolute, out var uri))
        {
            link.NavigateUri = uri;
            link.RequestNavigate += OnNavigate;
        }

        block.Inlines.Add(link);
        return text.Length;
    }

    /// <summary>
    /// Follows a link, but only a web or mail one. A model can write any URL it likes into an
    /// answer; a <c>file:</c> or a custom scheme opened with the shell is a way to make the tool run
    /// something, so those are left inert with the address on their tooltip.
    /// </summary>
    private static void OnNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        var uri = e.Uri;
        if (uri.Scheme is "http" or "https" or "mailto")
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A browser that will not open is not worth taking the panel down for.
            }
        }
    }

    private static DockPanel Strip(string? language, TextBlock listing)
    {
        var name = new TextBlock
        {
            Text = language ?? string.Empty,
            Foreground = Res("Text.Tertiary") ?? Brushes.Gray,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var copy = new Button
        {
            Content = Glyph(),
            Style = Res<Style>("ToolButton"),
            Padding = new Thickness(4, 0, 4, 0),
            MinHeight = 0,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res("Text.Tertiary") ?? Brushes.Gray,
            ToolTip = "Copy this block",
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        System.Windows.Automation.AutomationProperties.SetName(copy, "Copy code");
        copy.Click += (_, _) => Copy(listing.Text, copy);

        var strip = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(copy, Dock.Right);
        strip.Children.Add(copy);
        strip.Children.Add(name);
        return strip;
    }

    private static Wpf.Ui.Controls.SymbolIcon Glyph() =>
        new() { Symbol = Wpf.Ui.Controls.SymbolRegular.Copy20, FontSize = 13 };

    private static void Copy(string text, Button button)
    {
        try
        {
            Clipboard.SetText(text);
            button.Content = new TextBlock { Text = "Copied", FontSize = 11 };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            button.Content = new TextBlock { Text = "Busy", FontSize = 11 };
            button.ToolTip = "Another program is holding the clipboard. Try again.";
        }

        var settle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            button.Content = Glyph();
            button.ToolTip = "Copy this block";
        };

        settle.Start();
    }

    private static FontFamily Mono => Res<FontFamily>("Mono.FontFamily") ?? new FontFamily("Consolas");

    private static Brush? Res(string key) => Res<Brush>(key);

    private static T? Res<T>(string key) where T : class => Application.Current?.TryFindResource(key) as T;

    /// <summary>A grid mid-build: the next row and column to place a cell in.</summary>
    private sealed class TableFrame(Grid grid)
    {
        public int Row { get; private set; } = -1;

        public int Column { get; private set; } = -1;

        public void StartRow()
        {
            Row++;
            Column = -1;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        public void Place(UIElement cell)
        {
            Column++;
            while (grid.ColumnDefinitions.Count <= Column)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            grid.Children.Add(cell);
        }
    }
}
