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

    /// <summary>
    /// How far every block is held off the left and right edges of the panel.
    ///
    /// Here rather than on the document, because a RichTextBox manages its own document's
    /// PagePadding and a value set there does not reliably survive. The right-hand gap went missing
    /// for exactly that reason: it was asked for in the one place that could be overruled, so the
    /// text ran under the scrollbar while the left side kept its margin.
    ///
    /// The right is wider than the left because the scrollbar stands in it. Equal numbers look
    /// unequal on a control whose scrollbar sits inside its own content area.
    /// </summary>
    private const double Left = 10;

    private const double Right = 22;

    /// <summary>The standard block inset, with whatever vertical spacing that block wants.</summary>
    private static Thickness Inset(double top, double bottom) => new(Left, top, Right, bottom);

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
        yield return PlainParagraph(text);
    }

    /// <summary>
    /// The same thing as <see cref="Plain"/>, for a caller that keeps hold of the paragraph.
    ///
    /// A streaming answer is appended to rather than redrawn, so the view needs the paragraph
    /// itself and not a sequence it has to go looking through. See MainWindow's RedrawStreamingLine.
    /// </summary>
    public static System.Windows.Documents.Paragraph PlainParagraph(string text)
    {
        var paragraph = new System.Windows.Documents.Paragraph { Margin = Inset(0, 6) };
        AppendWithLineBreaks(paragraph.Inlines, text);
        return paragraph;
    }

    /// <summary>
    /// Adds more plain text to the end of a paragraph that is already on screen.
    ///
    /// Appending chunk by chunk gives the same inlines as one call on the whole string: a chunk that
    /// ends on a newline has already had its break added, and one that starts mid-line continues the
    /// Run before it. That equivalence is what lets an answer be drawn once as it arrives instead of
    /// once per token.
    /// </summary>
    public static void AppendPlain(InlineCollection inlines, string text) =>
        AppendWithLineBreaks(inlines, text);

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
            Margin = new Thickness(60, 2, Right, 2),
        };

        yield return new BlockUIContainer(border) { Margin = new Thickness(0, 4, 0, 6) };
    }

    /// <summary>An aside from the panel itself, not from anyone in the conversation.</summary>
    public static IEnumerable<Block> Note(string text)
    {
        var paragraph = new System.Windows.Documents.Paragraph
        {
            Margin = Inset(6, 6),
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
            Margin = Inset(0, 4),
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
        paragraph.Margin = Inset(8, 4);
        return paragraph;
    }

    /// <summary>
    /// A fenced block: the language it was tagged with, a button that copies it, and the code.
    ///
    /// The code itself stays a Paragraph in the document's own flow, so it can still be selected
    /// along with the prose around it and copied a few lines at a time. Only the strip above it is
    /// a UI container. Putting the code in a container too would have made the button the only way
    /// to get at it, which is a poorer trade than it sounds — a listing is often wanted in part.
    /// </summary>
    private static Block Code(CodeBlock code)
    {
        // The tint sits on the Section so that it covers the strip and the code as one block.
        // The inset then goes on the pieces inside it, equally on both sides, rather than on the
        // Section itself: padding there would move the tint's edges instead of the text's.
        var section = new Section
        {
            Background = CodeBackground,
            Margin = Inset(4, 8),
            Padding = default,
        };

        section.Blocks.Add(Strip(code));

        var paragraph = new System.Windows.Documents.Paragraph
        {
            FontFamily = Mono,
            FontSize = 12,
            Margin = new Thickness(10, 0, 10, 8),
        };

        AppendWithLineBreaks(paragraph.Inlines, code.Text);
        section.Blocks.Add(paragraph);
        return section;
    }

    /// <summary>The strip above a code block: which language it is, and a button to copy it.</summary>
    private static Block Strip(CodeBlock code)
    {
        var language = new TextBlock
        {
            Text = code.Language ?? string.Empty,
            Foreground = Dim,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var copy = new Button
        {
            Content = Glyph(),
            Style = Resource("ToolButton") as Style,
            Padding = new Thickness(5, 1, 5, 1),
            Foreground = Dim,
            ToolTip = "Copy this block",
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        System.Windows.Automation.AutomationProperties.SetName(copy, "Copy code");
        copy.Click += (_, _) => Copy(code.Text, copy);

        // The same inset as the code below it, so the icon lines up with the code's right edge
        // rather than standing proud of it.
        var strip = new DockPanel { Margin = new Thickness(10, 4, 10, 2), LastChildFill = false };
        DockPanel.SetDock(copy, Dock.Right);
        strip.Children.Add(copy);
        strip.Children.Add(language);

        return new BlockUIContainer(strip) { Margin = default };
    }

    private static Wpf.Ui.Controls.SymbolIcon Glyph() =>
        new() { Symbol = Wpf.Ui.Controls.SymbolRegular.Copy20, FontSize = 13 };

    /// <summary>
    /// Copies the block, and says on the button what happened.
    ///
    /// The clipboard is shared with every other program running, and any of them can be holding it
    /// when this is called, so this fails from time to time through no fault of the caller. A silent
    /// failure would be read as a copy that worked, and whatever was in the clipboard already would
    /// be pasted somewhere else in the belief that it was this code.
    /// </summary>
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

        // Back to an icon afterwards, so the strip does not end up a row of stale words in a
        // conversation where several blocks have been copied.
        var settle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            button.Content = Glyph();
            button.ToolTip = "Copy this block";
        };

        settle.Start();
    }

    private static Block Bulleted(MdList block)
    {
        var list = new System.Windows.Documents.List
        {
            MarkerStyle = block.Ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(Left + 16, 0, Right, 6),
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
        var paragraph = new System.Windows.Documents.Paragraph { Margin = Inset(0, 6) };

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
