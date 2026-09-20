using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

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
/// Turning flat-text offsets into places in a <see cref="TextBlock"/> and back. A block is built so
/// its plain text equals its flat-text slice exactly — every character a <see cref="Run"/>, every
/// newline a <see cref="LineBreak"/> — so an offset counts the same on both sides.
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

    /// <summary>The pointer at a character offset into the block, counting a newline as one character.</summary>
    public static TextPointer PointerForChar(TextBlock block, int target)
    {
        int seen = 0;
        var at = block.ContentStart;
        while (at is not null)
        {
            switch (at.GetPointerContext(LogicalDirection.Forward))
            {
                case TextPointerContext.Text:
                    string run = at.GetTextInRun(LogicalDirection.Forward);
                    if (target <= seen + run.Length)
                    {
                        return at.GetPositionAtOffset(target - seen, LogicalDirection.Forward) ?? at;
                    }

                    seen += run.Length;
                    at = at.GetPositionAtOffset(run.Length, LogicalDirection.Forward);
                    continue;

                case TextPointerContext.ElementStart when at.GetAdjacentElement(LogicalDirection.Forward) is LineBreak:
                    if (target <= seen)
                    {
                        return at;
                    }

                    seen += 1;
                    break;
            }

            at = at.GetNextContextPosition(LogicalDirection.Forward);
        }

        return block.ContentEnd;
    }

    /// <summary>The character offset of a pointer into the block — the inverse of <see cref="PointerForChar"/>.</summary>
    public static int CharForPointer(TextBlock block, TextPointer pointer)
    {
        int seen = 0;
        var at = block.ContentStart;
        while (at is not null && at.CompareTo(pointer) < 0)
        {
            switch (at.GetPointerContext(LogicalDirection.Forward))
            {
                case TextPointerContext.Text:
                    string run = at.GetTextInRun(LogicalDirection.Forward);
                    var end = at.GetPositionAtOffset(run.Length, LogicalDirection.Forward);
                    if (end is not null && end.CompareTo(pointer) <= 0)
                    {
                        seen += run.Length;
                        at = end;
                        continue;
                    }

                    // The pointer is inside this run.
                    return seen + at.GetOffsetToPosition(pointer);

                case TextPointerContext.ElementStart when at.GetAdjacentElement(LogicalDirection.Forward) is LineBreak:
                    seen += 1;
                    break;
            }

            at = at.GetNextContextPosition(LogicalDirection.Forward);
        }

        return seen;
    }

    /// <summary>
    /// The rectangles a run of characters covers, one per visual line, in the block's own
    /// coordinates. The caller offsets them by where the block sits.
    /// </summary>
    public static IEnumerable<Rect> LineRects(TextBlock block, int from, int to)
    {
        if (to <= from)
        {
            yield break;
        }

        var end = PointerForChar(block, to);

        var cursor = PointerForChar(block, from);
        while (cursor is not null && cursor.CompareTo(end) < 0)
        {
            var next = cursor.GetLineStartPosition(1);

            // The current visual line ends where the next one starts, unless the range ends first.
            var lineEnd = next is not null && next.CompareTo(end) < 0 ? next : end;

            var a = cursor.GetCharacterRect(LogicalDirection.Forward);
            var b = lineEnd.GetCharacterRect(LogicalDirection.Backward);
            double left = Math.Min(a.Left, b.Left);
            double right = Math.Max(a.Right, b.Right);
            double top = Math.Min(a.Top, b.Top);
            double bottom = Math.Max(a.Bottom, b.Bottom);
            yield return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

            if (next is null || next.CompareTo(end) >= 0)
            {
                yield break;
            }

            cursor = next;
        }
    }
}
