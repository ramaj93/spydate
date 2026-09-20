using System.Text;

namespace Spydate.Agent.Text;

public enum BlockKind
{
    Paragraph,
    Heading,
    Code,
    Quote,
    List,
    Item,
    Table,
    Row,
    Cell,
    Rule,
}

/// <summary>What a block opened with. Only the fields that kind of block has mean anything.</summary>
public readonly record struct BlockInfo
{
    public static readonly BlockInfo None = new();

    public int Level { get; init; }

    public string? Language { get; init; }

    public bool Ordered { get; init; }

    public int Start { get; init; }

    /// <summary>How many lists this one is inside, for a list; the item's box, for an item.</summary>
    public int Depth { get; init; }

    public bool? Checked { get; init; }

    public bool IsHeader { get; init; }

    public TableAlign Align { get; init; }
}

[Flags]
public enum SpanStyle
{
    None = 0,
    Strong = 1,
    Emphasis = 2,
    Code = 4,
    Strike = 8,
    Link = 16,
}

/// <summary>How a run of text is drawn, and where it points if it is a link.</summary>
public readonly record struct SpanInfo(SpanStyle Style, string? Url = null, string? Title = null)
{
    public static readonly SpanInfo Plain = new(SpanStyle.None);

    public bool Is(SpanStyle style) => (Style & style) != 0;
}

/// <summary>
/// What <see cref="MarkdownWalk"/> tells about a document, in order. Every character of the
/// document's flat text arrives through exactly one of <see cref="Text"/>, <see cref="Marker"/> and
/// <see cref="Glue"/>, so a sink that counts what it is given knows the offset of everything.
/// </summary>
public interface IMarkdownSink
{
    void Open(BlockKind kind, BlockInfo info);

    void Close(BlockKind kind);

    /// <summary>Characters drawn as text in the block that is open. A newline in them is a line break.</summary>
    void Text(string text, SpanInfo style);

    /// <summary>A list item's bullet, number or box, including the indent for its depth.</summary>
    void Marker(string text);

    /// <summary>
    /// Characters that are in the flat text but are structure on screen: the blank line between two
    /// blocks, the tab between two cells, the line between two rows.
    /// </summary>
    void Glue(string text);
}

/// <summary>
/// One walk over a parsed answer that every consumer shares. The window's renderer and the text
/// that copying and searching read are two sinks fed by the same walk, so an offset means the same
/// character to both of them by construction, not by two pieces of code agreeing.
/// </summary>
public static class MarkdownWalk
{
    public static void Walk(IReadOnlyList<MarkdownBlock> blocks, IMarkdownSink sink) => Blocks(blocks, sink, depth: 0, "\n\n");

    private static void Blocks(IReadOnlyList<MarkdownBlock> blocks, IMarkdownSink sink, int depth, string between)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            if (i > 0)
            {
                sink.Glue(between);
            }

            Block(blocks[i], sink, depth);
        }
    }

    private static void Block(MarkdownBlock block, IMarkdownSink sink, int depth)
    {
        switch (block)
        {
            case ParagraphBlock paragraph:
                sink.Open(BlockKind.Paragraph, BlockInfo.None);
                Spans(paragraph.Spans, sink, SpanInfo.Plain);
                sink.Close(BlockKind.Paragraph);
                break;

            case HeadingBlock heading:
                sink.Open(BlockKind.Heading, new BlockInfo { Level = heading.Level });
                Spans(heading.Spans, sink, SpanInfo.Plain);
                sink.Close(BlockKind.Heading);
                break;

            case CodeBlock code:
                sink.Open(BlockKind.Code, new BlockInfo { Language = code.Language });
                sink.Text(code.Text, new SpanInfo(SpanStyle.Code));
                sink.Close(BlockKind.Code);
                break;

            case QuoteBlock quote:
                sink.Open(BlockKind.Quote, BlockInfo.None);
                Blocks(quote.Blocks, sink, depth, "\n\n");
                sink.Close(BlockKind.Quote);
                break;

            case RuleBlock:
                sink.Open(BlockKind.Rule, BlockInfo.None);
                sink.Glue("---");
                sink.Close(BlockKind.Rule);
                break;

            case ListBlock list:
                List(list, sink, depth);
                break;

            case TableBlock table:
                Table(table, sink);
                break;
        }
    }

    private static void List(ListBlock list, IMarkdownSink sink, int depth)
    {
        sink.Open(BlockKind.List, new BlockInfo { Ordered = list.Ordered, Start = list.Start, Depth = depth });

        string indent = new(' ', depth * 2);
        for (int i = 0; i < list.Items.Count; i++)
        {
            if (i > 0)
            {
                sink.Glue("\n");
            }

            var item = list.Items[i];
            sink.Open(BlockKind.Item, new BlockInfo { Ordered = list.Ordered, Depth = depth, Checked = item.Checked });

            // The box stands in for the bullet: a task list is a list of boxes, not of dots with
            // boxes after them. Numbers keep counting whether or not an item is a task.
            string marker = item.Checked switch
            {
                true => "☑ ",
                false => "☐ ",
                null => list.Ordered ? $"{list.Start + i}. " : "- ",
            };

            sink.Marker(indent + marker);

            // A blank line between an item's own blocks would open the list up; one newline keeps a
            // nested list tight against the line it hangs from.
            Blocks(item.Blocks, sink, depth + 1, "\n");
            sink.Close(BlockKind.Item);
        }

        sink.Close(BlockKind.List);
    }

    private static void Table(TableBlock table, IMarkdownSink sink)
    {
        sink.Open(BlockKind.Table, BlockInfo.None);

        for (int r = 0; r < table.Rows.Count; r++)
        {
            if (r > 0)
            {
                sink.Glue("\n");
            }

            var row = table.Rows[r];
            sink.Open(BlockKind.Row, new BlockInfo { IsHeader = row.IsHeader });

            for (int c = 0; c < row.Cells.Count; c++)
            {
                if (c > 0)
                {
                    sink.Glue("\t");
                }

                var align = c < table.Aligns.Count ? table.Aligns[c] : TableAlign.Left;
                sink.Open(BlockKind.Cell, new BlockInfo { Align = align, IsHeader = row.IsHeader });
                Spans(row.Cells[c], sink, SpanInfo.Plain);
                sink.Close(BlockKind.Cell);
            }

            sink.Close(BlockKind.Row);
        }

        sink.Close(BlockKind.Table);
    }

    private static void Spans(IReadOnlyList<MarkdownSpan> spans, IMarkdownSink sink, SpanInfo style)
    {
        foreach (var span in spans)
        {
            switch (span)
            {
                case TextSpan text:
                    sink.Text(text.Text, style);
                    break;

                case CodeSpan code:
                    sink.Text(code.Text, style with { Style = style.Style | SpanStyle.Code });
                    break;

                case LineBreakSpan:
                    sink.Text("\n", style);
                    break;

                case StrongSpan strong:
                    Spans(strong.Children, sink, style with { Style = style.Style | SpanStyle.Strong });
                    break;

                case EmphasisSpan emphasis:
                    Spans(emphasis.Children, sink, style with { Style = style.Style | SpanStyle.Emphasis });
                    break;

                case StrikeSpan strike:
                    Spans(strike.Children, sink, style with { Style = style.Style | SpanStyle.Strike });
                    break;

                case LinkSpan link:
                    Spans(link.Children, sink, new SpanInfo(style.Style | SpanStyle.Link, link.Url, link.Title));
                    break;
            }
        }
    }
}

/// <summary>
/// An answer as the plain text it reads as: what copying it gives and what searching it looks
/// through. It is <see cref="MarkdownWalk"/> into a string, so its offsets are the renderer's.
/// </summary>
public static class FlatText
{
    public static string Of(IReadOnlyList<MarkdownBlock> blocks)
    {
        var sink = new Sink();
        MarkdownWalk.Walk(blocks, sink);
        return sink.Builder.ToString();
    }

    /// <summary>The plain text of a Markdown string; the two steps in one for callers that have only the text.</summary>
    public static string Of(string? markdown) => Of(Markdown.Parse(markdown));

    private sealed class Sink : IMarkdownSink
    {
        public StringBuilder Builder { get; } = new();

        public void Open(BlockKind kind, BlockInfo info)
        {
        }

        public void Close(BlockKind kind)
        {
        }

        public void Text(string text, SpanInfo style) => Builder.Append(text);

        public void Marker(string text) => Builder.Append(text);

        public void Glue(string text) => Builder.Append(text);
    }
}
