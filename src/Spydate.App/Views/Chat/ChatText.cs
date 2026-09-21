using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Spydate.App.Views.Chat;

/// <summary>A place in the transcript: an offset into one line's flat text. The selection is a pair of these.</summary>
internal readonly record struct TextPlace(int Line, int Offset);

/// <summary>
/// One <see cref="TextBlock"/> and the slice of a line's flat text it draws. Every registered slice
/// is a contiguous, non-overlapping run of the flat text, in order, so a flat-text offset lands in
/// at most one slice — which is what lets the selection live in flat-text offsets and still be
/// drawn, hit and highlighted on whatever happens to be realized.
/// </summary>
internal readonly record struct TextSlice(TextBlock Block, int Base, int Length)
{
    public int End => Base + Length;

    public bool Contains(int offset) => offset >= Base && offset < End;
}

/// <summary>
/// Where every character of a <see cref="TextBlock"/> is drawn: the visual lines it wraps into, and
/// the left edge of each character.
///
/// Built in one pass and kept until the block is redrawn or the panel resized. Highlighting a range
/// is then arithmetic over this — no pointer walking per repaint, which is what makes dragging a
/// selection cheap. It replaced a walk that stepped visual lines with
/// <see cref="TextPointer.GetLineStartPosition"/>: that took pointers which, at a
/// <see cref="LineBreak"/>, are element boundaries rather than insertion positions, and it silently
/// skipped lines in exactly the place a block has many of them — a fenced code listing.
/// </summary>
internal sealed class LineMap
{
    /// <summary>One visual line: the characters on it, and the box it occupies.</summary>
    internal readonly record struct Line(int Start, int End, double Top, double Bottom, double Left, double Right);

    private readonly double[] _edges;

    private LineMap(IReadOnlyList<Line> lines, double[] edges)
    {
        Lines = lines;
        _edges = edges;
    }

    public IReadOnlyList<Line> Lines { get; }

    /// <summary>The left edge of a character, for a range that starts or ends mid-line.</summary>
    public double Edge(int index) => index >= 0 && index < _edges.Length ? _edges[index] : 0;

    public static LineMap Of(TextBlock block)
    {
        var lines = new List<Line>();
        var edges = new List<double>();

        int start = 0;
        double top = 0;
        double bottom = 0;
        double left = 0;
        double right = 0;
        bool open = false;

        foreach (var (index, at) in ChatText.Positions(block))
        {
            var box = at.GetCharacterRect(LogicalDirection.Forward);
            edges.Add(box.Left);

            if (!open)
            {
                open = true;
                (start, top, bottom, left, right) = (index, box.Top, box.Bottom, box.Left, box.Right);
                continue;
            }

            // A character drawn at a different height began a new visual line — a wrap, or the
            // newline before it. The newline itself belongs to the line it ends, which is what makes
            // a selection through it reach the right-hand edge.
            if (Math.Abs(box.Top - top) > 0.5)
            {
                lines.Add(new Line(start, index, top, bottom, left, right));
                (start, top, bottom, left, right) = (index, box.Top, box.Bottom, box.Left, box.Right);
                continue;
            }

            bottom = Math.Max(bottom, box.Bottom);
            left = Math.Min(left, box.Left);
            right = Math.Max(right, box.Right);
        }

        if (open)
        {
            lines.Add(new Line(start, edges.Count, top, bottom, left, right));
        }

        return new LineMap(lines, [.. edges]);
    }

    /// <summary>The boxes a run of characters covers, one per visual line, in the block's coordinates.</summary>
    public IEnumerable<Rect> Rects(int from, int to)
    {
        foreach (var line in Lines)
        {
            int a = Math.Max(from, line.Start);
            int b = Math.Min(to, line.End);
            if (b <= a)
            {
                continue;
            }

            double x1 = a > line.Start ? Edge(a) : line.Left;

            // Up to the start of the character that is not selected, or to the end of the line when
            // the whole of it is — including its newline, so a multi-line selection is one block.
            double x2 = b < line.End ? Edge(b) : line.Right;

            yield return new Rect(x1, line.Top, Math.Max(0, x2 - x1), Math.Max(0, line.Bottom - line.Top));
        }
    }
}

/// <summary>
/// Turning flat-text offsets into places in a <see cref="TextBlock"/> and back. A block is built so
/// its plain text equals its flat-text slice exactly — every character a <see cref="Run"/>, every
/// newline a <see cref="LineBreak"/> — so an offset counts the same on both sides. One walk,
/// <see cref="Positions"/>, defines that counting, and everything here shares it.
/// </summary>
internal static class ChatText
{
    /// <summary>Adds text to a block as runs split on newlines, each newline a real break. Returns the character count.</summary>
    public static int Append(TextBlock block, string text, Action<Run>? style = null)
    {
        string[] parts = text.Split('\n');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                block.Inlines.Add(new LineBreak());
            }

            if (parts[i].Length > 0)
            {
                var run = new Run(parts[i]);
                style?.Invoke(run);
                block.Inlines.Add(run);
            }
        }

        return text.Length;
    }

    /// <summary>
    /// Every character of the block, in order: its offset and a pointer standing before it. This is
    /// the one definition of what an offset counts — run text one per character, a line break one —
    /// so the map, the hit test and the two lookups below can never disagree about it.
    /// </summary>
    internal static IEnumerable<(int Index, TextPointer At)> Positions(TextBlock block)
    {
        int seen = 0;
        var at = block.ContentStart;
        while (at is not null)
        {
            switch (at.GetPointerContext(LogicalDirection.Forward))
            {
                case TextPointerContext.Text:
                    string run = at.GetTextInRun(LogicalDirection.Forward);
                    for (int i = 0; i < run.Length; i++)
                    {
                        if (at.GetPositionAtOffset(i, LogicalDirection.Forward) is { } inside)
                        {
                            yield return (seen + i, inside);
                        }
                    }

                    seen += run.Length;
                    at = at.GetPositionAtOffset(run.Length, LogicalDirection.Forward);
                    continue;

                case TextPointerContext.ElementStart when at.GetAdjacentElement(LogicalDirection.Forward) is LineBreak:
                    yield return (seen, at);
                    seen += 1;
                    break;
            }

            at = at.GetNextContextPosition(LogicalDirection.Forward);
        }
    }

    /// <summary>The pointer at a character offset into the block, counting a newline as one character.</summary>
    public static TextPointer PointerForChar(TextBlock block, int target)
    {
        foreach (var (index, at) in Positions(block))
        {
            if (index >= target)
            {
                return at;
            }
        }

        return block.ContentEnd;
    }

    /// <summary>The character offset of a pointer into the block — the inverse of <see cref="PointerForChar"/>.</summary>
    public static int CharForPointer(TextBlock block, TextPointer pointer)
    {
        int last = 0;
        foreach (var (index, at) in Positions(block))
        {
            int order = at.CompareTo(pointer);
            if (order == 0)
            {
                return index;
            }

            if (order > 0)
            {
                return index;
            }

            // Inside this character's own run: the pointer sits between two positions we yield.
            last = index + 1;
        }

        return last;
    }
}
