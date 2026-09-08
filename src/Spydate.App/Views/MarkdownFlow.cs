using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Spydate.Agent.Text;
using MdList = Spydate.Agent.Text.ListBlock;
using MdListItem = Spydate.Agent.Text.ListItem;

namespace Spydate.App.Views;

/// <summary>
/// Turns parsed Markdown into the blocks a <see cref="System.Windows.Controls.RichTextBox"/> shows.
///
/// The parsing is in <see cref="Markdown"/>, which has no idea WPF exists and is tested on its own;
/// everything here is the part that cannot be tested without a window, and it is kept deliberately
/// thin for that reason — it decides fonts and spacing and nothing else.
/// </summary>
internal static class MarkdownFlow
{
    private static object? Resource(string key) => Application.Current?.TryFindResource(key);

    private static FontFamily Mono =>
        Resource("Mono.FontFamily") as FontFamily ?? new FontFamily("Consolas");

    private static Brush Dim =>
        Resource("Text.Tertiary") as Brush ?? Brushes.Gray;

    private static Brush CodeBackground =>
        Resource("Control.Background") as Brush ?? new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));

    /// <summary>Rendered Markdown. Used for a finished answer.</summary>
    public static IEnumerable<Block> Render(string text)
    {
        foreach (var block in Markdown.Parse(text))
        {
            yield return block switch
            {
                HeadingBlock heading => Heading(heading),
                CodeBlock code => Code(code),
                MdList list => Bulleted(list),
                ParagraphBlock paragraph => Paragraph(paragraph.Spans),
                _ => Paragraph([new TextSpan(string.Empty)]),
            };
        }
    }

    /// <summary>
    /// The same text with no interpretation at all, for a line still being written. Re-parsing a
    /// growing answer on every token would rebuild the document dozens of times a second, and half
    /// a Markdown construct renders as something other than what it will become — a fence that has
    /// only had its opening written would turn the rest of the answer into a code block as it
    /// arrives, then turn it back. It is re-rendered properly once the turn ends.
    /// </summary>
    public static IEnumerable<Block> Plain(string text)
    {
        var paragraph = new System.Windows.Documents.Paragraph { Margin = new Thickness(0, 0, 0, 6) };
        AppendWithLineBreaks(paragraph.Inlines, text);
        yield return paragraph;
    }

    /// <summary>
    /// What somebody typed, in a bubble along the right.
    ///
    /// Alignment is what actually separates the two voices. A transcript of one column, distinguished
    /// only by colour, has to be read to be navigated; questions on the right and answers on the left
    /// can be found by their shape, which is the whole reason every chat interface does this.
    ///
    /// It is a Border in a BlockUIContainer rather than a Paragraph, because a Paragraph's background
    /// spans the whole column whatever its text does — a bubble has to end where the words end. The
    /// cost is that this text is outside the document's own flow and so cannot be selected with the
    /// answers around it. That is the right way round: an answer is what gets copied out, and a
    /// question is the thing the reader typed and already has.
    /// </summary>
    public static IEnumerable<Block> Bubble(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Resource("Text.Primary") as Brush ?? Brushes.White,
        };

        var border = new Border
        {
            Child = block,
            Background = Resource("Chat.You.Background") as Brush ?? CodeBackground,
            BorderBrush = Resource("Chat.You.Border") as Brush ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            HorizontalAlignment = HorizontalAlignment.Right,

            // Room to be a bubble rather than a full-width band, and a floor so a one-word question
            // does not shrink to a stub. The document is narrow when the panel is, so this is a
            // fraction of it rather than a fixed width.
            MaxWidth = 620,
            Margin = new Thickness(60, 2, 0, 2),
        };

        yield return new BlockUIContainer(border) { Margin = new Thickness(0, 4, 0, 6) };
    }

    /// <summary>An aside from the panel itself, not from anyone in the conversation.</summary>
    public static IEnumerable<Block> Note(string text)
    {
        var paragraph = new System.Windows.Documents.Paragraph
        {
            Margin = new Thickness(0, 6, 0, 6),
            FontStyle = FontStyles.Italic,
            Foreground = Dim,
        };

        paragraph.Inlines.Add(new Run(text));
        yield return paragraph;
    }

    /// <summary>A tool call: quiet, monospaced, and out of the way of the prose.</summary>
    public static IEnumerable<Block> Tool(string text)
    {
        var paragraph = new System.Windows.Documents.Paragraph
        {
            Margin = new Thickness(0, 0, 0, 4),
            FontFamily = Mono,
            FontSize = 11,
            Foreground = Dim,
        };

        paragraph.Inlines.Add(new Run(text));
        yield return paragraph;
    }

    private static Block Heading(HeadingBlock heading)
    {
        var paragraph = Paragraph(heading.Spans);
        paragraph.FontWeight = FontWeights.SemiBold;
        paragraph.FontSize = heading.Level <= 2 ? 15 : 13;
        paragraph.Margin = new Thickness(0, 8, 0, 4);
        return paragraph;
    }

    private static Block Code(CodeBlock code)
    {
        var paragraph = new System.Windows.Documents.Paragraph
        {
            FontFamily = Mono,
            FontSize = 12,
            Background = CodeBackground,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 4, 0, 8),
        };

        AppendWithLineBreaks(paragraph.Inlines, code.Text);
        return paragraph;
    }

    private static Block Bulleted(MdList block)
    {
        var list = new System.Windows.Documents.List
        {
            MarkerStyle = block.Ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(16, 0, 0, 6),
            Padding = default,
        };

        foreach (MdListItem item in block.Items)
        {
            var paragraph = Paragraph(item.Spans);
            paragraph.Margin = new Thickness(0, 0, 0, 2);
            list.ListItems.Add(new System.Windows.Documents.ListItem(paragraph));
        }

        return list;
    }

    private static System.Windows.Documents.Paragraph Paragraph(IReadOnlyList<MarkdownSpan> spans)
    {
        var paragraph = new System.Windows.Documents.Paragraph { Margin = new Thickness(0, 0, 0, 6) };

        foreach (var span in spans)
        {
            switch (span)
            {
                case StrongSpan strong:
                    paragraph.Inlines.Add(new Bold(new Run(strong.Text)));
                    break;

                case EmphasisSpan emphasis:
                    paragraph.Inlines.Add(new Italic(new Run(emphasis.Text)));
                    break;

                case CodeSpan code:
                    paragraph.Inlines.Add(new Run(code.Text)
                    {
                        FontFamily = Mono,
                        Background = CodeBackground,
                    });
                    break;

                case TextSpan text:
                    AppendWithLineBreaks(paragraph.Inlines, text.Text);
                    break;
            }
        }

        return paragraph;
    }

    /// <summary>A Run cannot hold a newline, so the breaks have to become real ones.</summary>
    private static void AppendWithLineBreaks(InlineCollection inlines, string text)
    {
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                inlines.Add(new LineBreak());
            }

            if (lines[i].Length > 0)
            {
                inlines.Add(new Run(lines[i]));
            }
        }
    }
}
