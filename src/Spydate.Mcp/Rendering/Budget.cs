using System.Globalization;
using System.Text;

namespace Spydate.Mcp.Rendering;

/// <summary>
/// Keeps a tool's answer inside what an agent can afford to read.
///
/// Context is the scarcest thing in this system — scarcer than CPU, and the only one that cannot be
/// bought back. But the rule that matters more than any number here is that **a cut always says so**.
/// An agent that believes it read a whole function will draw confident conclusions from a third of
/// one, and nothing downstream can tell that it did.
/// </summary>
public static class Budget
{
    /// <summary>
    /// The backstop, not the normal path: every tool's own limit is set well inside it. Roughly
    /// three thousand tokens, which is a large answer but not a ruinous one.
    /// </summary>
    public const int MaxChars = 12_000;

    /// <summary>
    /// Trims to a whole number of lines and appends what was dropped. Cutting mid-line would leave
    /// half an instruction looking like a whole one.
    /// </summary>
    public static string Clip(string text, int maxChars = MaxChars)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxChars <= 0 || text.Length <= maxChars)
        {
            return text;
        }

        var lines = text.Split('\n');
        var kept = new StringBuilder();
        int used = 0;
        int count = 0;

        foreach (string line in lines)
        {
            int cost = line.Length + 1;
            if (used + cost > maxChars && count > 0)
            {
                break;
            }

            kept.Append(line).Append('\n');
            used += cost;
            count++;
        }

        int dropped = lines.Length - count;
        if (dropped <= 0)
        {
            return text;
        }

        kept.Append(CultureInfo.InvariantCulture, $"-- {dropped} more lines were cut to stay inside the response limit --");
        return kept.ToString();
    }

    /// <summary>
    /// A window of lines, with the elision naming the call that continues it. Used wherever a body
    /// of text is longer than one answer should be, so continuing is a fact rather than a guess.
    /// </summary>
    public static string Window(string text, int offset, int maxLines, string continuation)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(continuation);

        var lines = text.Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0)
        {
            lines = lines[..^1];   // a trailing newline is not a line
        }

        offset = Math.Max(0, offset);
        if (offset >= lines.Length)
        {
            return $"-- offset {offset} is past the end; there are {lines.Length} lines --";
        }

        int take = Math.Min(maxLines, lines.Length - offset);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"--- lines {offset + 1}-{offset + take} of {lines.Length} ---").Append('\n');
        for (int i = offset; i < offset + take; i++)
        {
            sb.Append(lines[i]).Append('\n');
        }

        int remaining = lines.Length - offset - take;
        if (remaining > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"--- {remaining} more lines. {continuation} ---");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Shortens a value that would wreck a column, keeping both ends. Mangled C++ names run to
    /// hundreds of characters and the informative parts are the front and the back.
    /// </summary>
    public static string Elide(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
        {
            return text ?? string.Empty;
        }

        if (max <= 1)
        {
            return "…";
        }

        int head = (max - 1) / 2;
        int tail = max - 1 - head;
        return string.Concat(text.AsSpan(0, head), "…", text.AsSpan(text.Length - tail));
    }
}
