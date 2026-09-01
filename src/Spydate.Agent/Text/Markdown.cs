using System.Text;

namespace Spydate.Agent.Text;

/// <summary>A run of text inside a block, with whatever emphasis was asked for.</summary>
public abstract record MarkdownSpan;

public sealed record TextSpan(string Text) : MarkdownSpan;

public sealed record StrongSpan(string Text) : MarkdownSpan;

public sealed record EmphasisSpan(string Text) : MarkdownSpan;

public sealed record CodeSpan(string Text) : MarkdownSpan;

/// <summary>A block-level piece of an answer.</summary>
public abstract record MarkdownBlock;

public sealed record ParagraphBlock(IReadOnlyList<MarkdownSpan> Spans) : MarkdownBlock;

public sealed record HeadingBlock(int Level, IReadOnlyList<MarkdownSpan> Spans) : MarkdownBlock;

public sealed record CodeBlock(string Text, string? Language) : MarkdownBlock;

public sealed record ListItem(IReadOnlyList<MarkdownSpan> Spans);

public sealed record ListBlock(bool Ordered, IReadOnlyList<ListItem> Items) : MarkdownBlock;

/// <summary>
/// Enough Markdown to read an answer by: headings, fenced and inline code, bullet and numbered
/// lists, bold and italic. Deliberately not a full implementation — the input is one assistant's
/// prose, not arbitrary documents, and the failure mode of a clever parser on odd input is worse
/// than the failure mode of a plain one.
///
/// Two rules it does not share with CommonMark, both chosen for what actually gets written here:
/// a single newline inside a paragraph is kept as a line break rather than folded into a space,
/// because addresses and symbol names get listed one per line; and an unmatched delimiter is left
/// as literal text rather than swallowed, because <c>sub_401000 * 2</c> is not an italic.
/// </summary>
public static class Markdown
{
    /// <summary>Never throws. Any input at all is some sequence of blocks, even if it is one paragraph.</summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string? text)
    {
        var blocks = new List<MarkdownBlock>();
        if (string.IsNullOrEmpty(text))
        {
            return blocks;
        }

        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        var paragraph = new List<string>();
        var items = new List<ListItem>();
        bool ordered = false;

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                blocks.Add(new ParagraphBlock(Inline(string.Join("\n", paragraph))));
                paragraph.Clear();
            }
        }

        void FlushList()
        {
            if (items.Count > 0)
            {
                blocks.Add(new ListBlock(ordered, items.ToList()));
                items.Clear();
            }
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();

            // A fence runs to the next fence, or to the end if the answer was cut off mid-block.
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                FlushList();

                string? language = trimmed[3..].Trim() is { Length: > 0 } named ? named : null;
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[i]);
                    i++;
                }

                blocks.Add(new CodeBlock(string.Join("\n", code), language));
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                FlushList();
                continue;
            }

            if (Heading(trimmed) is { } heading)
            {
                FlushParagraph();
                FlushList();
                blocks.Add(heading);
                continue;
            }

            if (Bullet(trimmed) is { } bullet)
            {
                FlushParagraph();
                if (items.Count > 0 && ordered)
                {
                    FlushList();
                }

                ordered = false;
                items.Add(new ListItem(Inline(bullet)));
                continue;
            }

            if (Numbered(trimmed) is { } numbered)
            {
                FlushParagraph();
                if (items.Count > 0 && !ordered)
                {
                    FlushList();
                }

                ordered = true;
                items.Add(new ListItem(Inline(numbered)));
                continue;
            }

            FlushList();
            paragraph.Add(line);
        }

        FlushParagraph();
        FlushList();
        return blocks;
    }

    private static HeadingBlock? Heading(string trimmed)
    {
        int level = 0;
        while (level < trimmed.Length && trimmed[level] == '#')
        {
            level++;
        }

        return level is >= 1 and <= 6 && level < trimmed.Length && trimmed[level] == ' '
            ? new HeadingBlock(level, Inline(trimmed[(level + 1)..].Trim()))
            : null;
    }

    private static string? Bullet(string trimmed)
        => trimmed.Length > 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' '
            ? trimmed[2..].Trim()
            : null;

    private static string? Numbered(string trimmed)
    {
        int digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits]))
        {
            digits++;
        }

        return digits > 0 && digits + 1 < trimmed.Length && trimmed[digits] is '.' or ')' && trimmed[digits + 1] == ' '
            ? trimmed[(digits + 2)..].Trim()
            : null;
    }

    /// <summary>
    /// Code spans first, so a backtick protects whatever is inside it — a name with an underscore
    /// in it is the normal case here, and it must not come out italic.
    /// </summary>
    private static IReadOnlyList<MarkdownSpan> Inline(string text)
    {
        var spans = new List<MarkdownSpan>();
        var literal = new StringBuilder();

        void Flush()
        {
            if (literal.Length > 0)
            {
                spans.Add(new TextSpan(literal.ToString()));
                literal.Clear();
            }
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    Flush();
                    spans.Add(new CodeSpan(text[(i + 1)..end]));
                    i = end;
                    continue;
                }
            }
            else if (c is '*' or '_')
            {
                bool doubled = i + 1 < text.Length && text[i + 1] == c;
                string marker = doubled ? new string(c, 2) : c.ToString();
                int from = i + marker.Length;

                // An underscore touching a word is part of that word. This is CommonMark's rule and
                // it matters more here than anywhere: snake_case is most of what gets written about
                // a binary, and pairing the underscores in find_strings and sub_401000 turns both
                // names into one italic and deletes the underscores from the text entirely.
                bool opens = c != '_' || i == 0 || !char.IsLetterOrDigit(text[i - 1]);
                int end = opens && from < text.Length ? Closing(text, marker, from, c) : -1;

                // Non-empty, and not opening on a space: "a * b" is arithmetic, not emphasis.
                if (end > from && !char.IsWhiteSpace(text[from]))
                {
                    Flush();
                    string inner = text[from..end];
                    spans.Add(doubled ? new StrongSpan(inner) : new EmphasisSpan(inner));
                    i = end + marker.Length - 1;
                    continue;
                }
            }

            literal.Append(c);
        }

        Flush();
        return spans;
    }

    /// <summary>
    /// The next place this emphasis could close. For an underscore that means one not followed by a
    /// word character, so <c>a_b_c</c> never closes anywhere and stays the name it is.
    /// </summary>
    private static int Closing(string text, string marker, int from, char opener)
    {
        int at = from;
        while (at >= 0 && at < text.Length)
        {
            at = text.IndexOf(marker, at, StringComparison.Ordinal);
            if (at < 0)
            {
                return -1;
            }

            int after = at + marker.Length;
            if (opener != '_' || after >= text.Length || !char.IsLetterOrDigit(text[after]))
            {
                return at;
            }

            at = after;
        }

        return -1;
    }
}
