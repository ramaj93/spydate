using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Spydate.Agent.Text;

/// <summary>A run of text inside a block, with whatever emphasis was asked for.</summary>
public abstract record MarkdownSpan;

public sealed record TextSpan(string Text) : MarkdownSpan;

public sealed record CodeSpan(string Text) : MarkdownSpan;

/// <summary>A line break inside a paragraph. Every newline in an answer is one — see <see cref="Markdown"/>.</summary>
public sealed record LineBreakSpan : MarkdownSpan;

public sealed record StrongSpan(IReadOnlyList<MarkdownSpan> Children) : MarkdownSpan;

public sealed record EmphasisSpan(IReadOnlyList<MarkdownSpan> Children) : MarkdownSpan;

public sealed record StrikeSpan(IReadOnlyList<MarkdownSpan> Children) : MarkdownSpan;

/// <summary>
/// A link. An image is one of these too, with its alt text as the children: nothing here ever
/// fetches a URL an answer contains, and the reader can follow it or not.
/// </summary>
public sealed record LinkSpan(IReadOnlyList<MarkdownSpan> Children, string Url, string? Title) : MarkdownSpan;

/// <summary>A block-level piece of an answer.</summary>
public abstract record MarkdownBlock;

public sealed record ParagraphBlock(IReadOnlyList<MarkdownSpan> Spans) : MarkdownBlock;

public sealed record HeadingBlock(int Level, IReadOnlyList<MarkdownSpan> Spans) : MarkdownBlock;

public sealed record CodeBlock(string Text, string? Language) : MarkdownBlock;

/// <summary>One item of a list: its blocks (a nested list is one of them), and its box if it is a task.</summary>
public sealed record ListItem(IReadOnlyList<MarkdownBlock> Blocks, bool? Checked);

public sealed record ListBlock(bool Ordered, int Start, IReadOnlyList<ListItem> Items) : MarkdownBlock;

public sealed record QuoteBlock(IReadOnlyList<MarkdownBlock> Blocks) : MarkdownBlock;

public enum TableAlign
{
    Left,
    Center,
    Right,
}

public sealed record TableRow(IReadOnlyList<IReadOnlyList<MarkdownSpan>> Cells, bool IsHeader);

public sealed record TableBlock(IReadOnlyList<TableRow> Rows, IReadOnlyList<TableAlign> Aligns) : MarkdownBlock;

public sealed record RuleBlock : MarkdownBlock;

/// <summary>
/// An answer's Markdown as a small, closed tree of records. Markdig does the parsing — CommonMark
/// plus the GitHub pieces an assistant actually writes: pipe tables, task lists, strikethrough and
/// bare URLs — and this turns its tree into these records so that nothing above knows Markdig
/// exists. HTML is off: a model that writes <c>&lt;b&gt;</c>, or leaks a tool-call template in angle
/// brackets, gets it shown as the text it wrote.
///
/// Two rules the pipeline is set to that CommonMark's default is not, both chosen for what gets
/// written here: a single newline inside a paragraph is a line break rather than a space, because
/// addresses and symbol names get listed one per line; and of the extra emphasis markers only
/// <c>~~strike~~</c> is on, because <c>~</c>, <c>^</c> and <c>==</c> are code in this domain, not
/// subscript, superscript and highlighting. The rule that matters most is CommonMark's own: an
/// underscore inside a word is part of the word, so <c>sub_401000</c> and <c>find_strings</c> stay
/// the names they are.
/// </summary>
public static class Markdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseTaskLists()
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak()
        .DisableHtml()
        .Build();

    /// <summary>Never throws. Any input at all is some sequence of blocks, even if it is one paragraph.</summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        try
        {
            return Blocks(Markdig.Markdown.Parse(text, Pipeline));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A parser that throws on a model's prose would take the answer down with it. The text
            // as it came is always readable.
            return [new ParagraphBlock([new TextSpan(text)])];
        }
    }

    private static List<MarkdownBlock> Blocks(ContainerBlock container)
    {
        var blocks = new List<MarkdownBlock>();
        foreach (var block in container)
        {
            Add(blocks, block);
        }

        return blocks;
    }

    private static void Add(List<MarkdownBlock> blocks, Block block)
    {
        switch (block)
        {
            case Markdig.Syntax.HeadingBlock heading:
                blocks.Add(new HeadingBlock(heading.Level, Inlines(heading.Inline)));
                break;

            case Markdig.Syntax.ParagraphBlock paragraph:
                blocks.Add(new ParagraphBlock(Inlines(paragraph.Inline)));
                break;

            case FencedCodeBlock fenced:
                blocks.Add(new CodeBlock(Code(fenced), fenced.Info is { Length: > 0 } info ? info : null));
                break;

            case Markdig.Syntax.CodeBlock indented:
                blocks.Add(new CodeBlock(Code(indented), null));
                break;

            case Markdig.Syntax.ListBlock list:
                blocks.Add(List(list));
                break;

            case Markdig.Syntax.QuoteBlock quote:
                blocks.Add(new QuoteBlock(Blocks(quote)));
                break;

            case ThematicBreakBlock:
                blocks.Add(new RuleBlock());
                break;

            case Table table:
                blocks.Add(Table(table));
                break;

            // The definitions a reference-style link pointed at. Their links already carry the URL.
            case LinkReferenceDefinitionGroup:
            case LinkReferenceDefinition:
                break;

            // Anything else that holds blocks is shown as what it holds; anything else that holds
            // text is shown as that text. Nothing an answer contains is dropped.
            case ContainerBlock container:
                blocks.AddRange(Blocks(container));
                break;

            case LeafBlock leaf:
                blocks.Add(new ParagraphBlock(leaf.Inline is { } inline ? Inlines(inline) : [new TextSpan(Lines(leaf))]));
                break;
        }
    }

    private static string Code(LeafBlock block) => Lines(block);

    private static string Lines(LeafBlock block)
    {
        var lines = block.Lines;
        var parts = new string[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            parts[i] = lines.Lines[i].Slice.ToString();
        }

        return string.Join("\n", parts);
    }

    private static ListBlock List(Markdig.Syntax.ListBlock list)
    {
        var items = new List<ListItem>();
        foreach (var child in list)
        {
            if (child is ListItemBlock item)
            {
                items.Add(new ListItem(Blocks(item), Task(item)));
            }
        }

        int start = list.IsOrdered && int.TryParse(list.OrderedStart, out int number) ? number : 1;
        return new ListBlock(list.IsOrdered, start, items);
    }

    /// <summary>
    /// The box of a task item, which Markdig leaves as the first inline of the item's first paragraph.
    /// <see cref="Inlines"/> skips that inline, so the text does not also say <c>[x]</c>.
    /// </summary>
    private static bool? Task(ListItemBlock item)
    {
        foreach (var block in item)
        {
            if (block is Markdig.Syntax.ParagraphBlock { Inline.FirstChild: TaskList task })
            {
                return task.Checked;
            }

            break;
        }

        return null;
    }

    private static TableBlock Table(Table table)
    {
        var aligns = new List<TableAlign>();
        foreach (var column in table.ColumnDefinitions)
        {
            aligns.Add(column.Alignment switch
            {
                TableColumnAlign.Center => TableAlign.Center,
                TableColumnAlign.Right => TableAlign.Right,
                _ => TableAlign.Left,
            });
        }

        var rows = new List<TableRow>();
        foreach (var child in table)
        {
            if (child is not Markdig.Extensions.Tables.TableRow row)
            {
                continue;
            }

            var cells = new List<IReadOnlyList<MarkdownSpan>>();
            foreach (var member in row)
            {
                if (member is TableCell cell)
                {
                    cells.Add(Cell(cell));
                }
            }

            rows.Add(new TableRow(cells, row.IsHeader));
        }

        return new TableBlock(rows, aligns);
    }

    /// <summary>A cell is a paragraph nearly always; whatever else it holds is joined on line breaks.</summary>
    private static IReadOnlyList<MarkdownSpan> Cell(TableCell cell)
    {
        var spans = new List<MarkdownSpan>();
        foreach (var block in cell)
        {
            if (spans.Count > 0)
            {
                spans.Add(new LineBreakSpan());
            }

            if (block is LeafBlock { Inline: { } inline })
            {
                spans.AddRange(Inlines(inline));
            }
            else if (block is LeafBlock leaf)
            {
                spans.Add(new TextSpan(Lines(leaf)));
            }
        }

        return Merge(spans);
    }

    private static IReadOnlyList<MarkdownSpan> Inlines(ContainerInline? container)
    {
        var spans = new List<MarkdownSpan>();
        if (container is null)
        {
            return spans;
        }

        bool task = false;
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            if (inline is TaskList)
            {
                task = true;
                continue;
            }

            Add(spans, inline);
        }

        // The box was followed by a space that belonged to it.
        if (task && spans.Count > 0 && spans[0] is TextSpan first)
        {
            spans[0] = new TextSpan(first.Text.TrimStart());
        }

        return Merge(spans);
    }

    private static void Add(List<MarkdownSpan> spans, Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                spans.Add(new TextSpan(literal.Content.ToString()));
                break;

            case CodeInline code:
                spans.Add(new CodeSpan(code.Content));
                break;

            case LineBreakInline:
                // Soft or hard, a newline is a newline here; the pipeline says so too.
                spans.Add(new LineBreakSpan());
                break;

            case EmphasisInline emphasis:
                var children = Inlines(emphasis);
                spans.Add(emphasis.DelimiterChar == '~'
                    ? new StrikeSpan(children)
                    : emphasis.DelimiterCount >= 2 ? new StrongSpan(children) : new EmphasisSpan(children));
                break;

            case LinkInline link:
                var text = Inlines(link);
                if (text.Count == 0)
                {
                    text = [new TextSpan(link.Url ?? string.Empty)];
                }

                spans.Add(new LinkSpan(text, link.Url ?? string.Empty, link.Title is { Length: > 0 } title ? title : null));
                break;

            case AutolinkInline auto:
                spans.Add(new LinkSpan([new TextSpan(auto.Url)], auto.IsEmail ? "mailto:" + auto.Url : auto.Url, null));
                break;

            case HtmlEntityInline entity:
                spans.Add(new TextSpan(entity.Transcoded.ToString()));
                break;

            case HtmlInline html:
                spans.Add(new TextSpan(html.Tag));
                break;

            case TaskList:
                break;

            case ContainerInline container:
                spans.AddRange(Inlines(container));
                break;
        }
    }

    /// <summary>
    /// Adjacent text runs become one. Markdig splits a literal wherever it looked for something and
    /// found nothing — around an unpaired <c>*</c>, at an escape — and one span per fragment is
    /// noise for everything downstream.
    /// </summary>
    private static IReadOnlyList<MarkdownSpan> Merge(List<MarkdownSpan> spans)
    {
        var merged = new List<MarkdownSpan>(spans.Count);
        foreach (var span in spans)
        {
            if (span is TextSpan { Text: var text })
            {
                if (text.Length == 0)
                {
                    continue;
                }

                if (merged.Count > 0 && merged[^1] is TextSpan previous)
                {
                    merged[^1] = new TextSpan(previous.Text + text);
                    continue;
                }
            }

            merged.Add(span);
        }

        return merged;
    }
}
