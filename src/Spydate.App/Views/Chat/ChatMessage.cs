using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Spydate.Agent.Text;
using Spydate.App.ViewModels;

namespace Spydate.App.Views.Chat;

/// <summary>
/// One line of the transcript, drawn from its <see cref="AssistantLine"/>. It is the template for
/// every item in <see cref="Controls.ChatTranscript"/>, so the list makes one per visible line and
/// recycles it — which is why it rebuilds itself when its <see cref="Line"/> changes under it.
///
/// It also publishes <see cref="Slices"/>: for each text block it drew, the slice of the line's flat
/// text that block shows. The transcript's selection is kept in those flat-text offsets, so it can
/// draw, hit and copy a selection over whatever is realized without the line itself needing to be.
/// </summary>
public sealed class ChatMessage : Decorator
{
    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(
        nameof(Line),
        typeof(AssistantLine),
        typeof(ChatMessage),
        new PropertyMetadata(null, OnLineChanged));

    private AssistantLine? _line;
    private TextBlock? _stream;
    private int _drawn;
    private List<TextSlice> _slices = [];

    public AssistantLine? Line
    {
        get => (AssistantLine?)GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    /// <summary>The flat-text slices this line drew, in order. Empty until it is rendered.</summary>
    internal IReadOnlyList<TextSlice> Slices => _slices;

    /// <summary>Raised when the drawn content changed, so the transcript can refresh its highlight.</summary>
    internal event EventHandler? Rendered;

    private static void OnLineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var message = (ChatMessage)d;
        if (message._line is { } old)
        {
            old.PropertyChanged -= message.OnLinePropertyChanged;
        }

        message._line = e.NewValue as AssistantLine;
        if (message._line is { } line)
        {
            line.PropertyChanged += message.OnLinePropertyChanged;
        }

        message.Rebuild();
    }

    private void OnLinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_line is null)
        {
            return;
        }

        // A streaming answer grows a character at a time; appending the delta keeps it from being
        // reparsed and rebuilt on every token, the way it did as a FlowDocument.
        if (e.PropertyName == nameof(AssistantLine.Text) && _line.IsStreaming && _stream is not null)
        {
            Grow();
            return;
        }

        if (e.PropertyName is nameof(AssistantLine.Text) or nameof(AssistantLine.IsStreaming))
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        _slices = [];
        _stream = null;
        _drawn = 0;

        if (_line is null)
        {
            Child = null;
            return;
        }

        Child = _line.Kind switch
        {
            "you" => Bubble(_line.Text),
            "assistant" when _line.IsStreaming => Streaming(_line.Text),
            "assistant" => Answer(_line.Text),
            "tool" => Plain(_line.Text, mono: true, Res("Text.Tertiary"), size: 11),
            "note" => Plain(_line.Text, mono: false, Res("Text.Tertiary"), italic: true),
            "problem" => Plain(_line.Text, mono: false, Res("Semantic.Error")),
            _ => Plain(_line.Text, mono: false, Res("Text.Primary")),
        };

        Rendered?.Invoke(this, EventArgs.Empty);
    }

    private void Grow()
    {
        string text = _line!.Text;
        if (text.Length < _drawn)
        {
            Rebuild();
            return;
        }

        if (text.Length == _drawn || _stream is null)
        {
            return;
        }

        ChatText.Append(_stream, text[_drawn..]);
        _drawn = text.Length;
        _slices = [new TextSlice(_stream, 0, text.Length)];
        Rendered?.Invoke(this, EventArgs.Empty);
    }

    private FrameworkElement Answer(string text)
    {
        var (root, slices) = MarkdownView.Render(Markdown.Parse(text));
        _slices = [.. slices];
        root.Margin = new Thickness(0, 2, 0, 6);
        return root;
    }

    private FrameworkElement Streaming(string text)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("Text.Primary") ?? Brushes.White,
            Margin = new Thickness(10, 2, 22, 6),
        };

        ChatText.Append(block, text);
        _stream = block;
        _drawn = text.Length;
        _slices = [new TextSlice(block, 0, text.Length)];
        return block;
    }

    private FrameworkElement Plain(string text, bool mono, Brush? foreground, double size = 12, bool italic = false)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = foreground ?? Brushes.White,
            FontSize = size,
            Margin = new Thickness(10, mono ? 0 : 6, 22, mono ? 4 : 6),
        };

        if (mono)
        {
            block.FontFamily = Res<FontFamily>("Mono.FontFamily") ?? new FontFamily("Consolas");
        }

        if (italic)
        {
            block.FontStyle = FontStyles.Italic;
        }

        int len = ChatText.Append(block, text);
        _slices = [new TextSlice(block, 0, len)];
        return block;
    }

    private FrameworkElement Bubble(string text)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("Text.Primary") ?? Brushes.White,
        };

        int len = ChatText.Append(block, text);
        _slices = [new TextSlice(block, 0, len)];

        return new Border
        {
            Child = block,
            Background = Res("Chat.You.Background") ?? Res("Control.Background"),
            BorderBrush = Res("Chat.You.Border") ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 620,
            Margin = new Thickness(60, 4, 22, 6),
        };
    }

    private static Brush? Res(string key) => Res<Brush>(key);

    private static T? Res<T>(string key) where T : class => Application.Current?.TryFindResource(key) as T;
}
