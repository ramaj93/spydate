using System.Globalization;
using System.Reflection.Emit;
using Spydate.Disassembly;

namespace Spydate.Decompiler.Managed;

/// <summary>
/// Turns typed IL into bytes, for patching one instruction over another.
///
/// A deliberate subset, on the same terms as <see cref="X86Assembler"/> — but the line is drawn
/// somewhere quite different, and not by effort. Every instruction that names something
/// (<c>call</c>, <c>ldstr</c>, <c>newobj</c>, <c>ldfld</c>, <c>castclass</c>) takes a metadata token,
/// and a token is an index into a table of this assembly's. Writing one that is not already there
/// means adding a row, which means rebuilding the metadata and moving everything after it — which is
/// a different program from a byte patcher, and the one thing this promises never to do is move
/// anything. So those are refused by name: what is assembled here is the instructions that name
/// nothing.
///
/// That still covers what IL patching is actually for. Flipping <c>brtrue</c> to <c>brfalse</c>,
/// pushing a constant the caller will believe, dropping a value, returning early, and padding with
/// <c>nop</c> are all in the set, and between them they are most of "stop this check from failing".
///
/// <c>bytes:</c> takes hex for anything outside it, including a token the caller has worked out for
/// themselves from a listing.
/// </summary>
public static class IlAssembler
{
    /// <summary>
    /// Assembles <paramref name="text"/> as if it sat at <paramref name="offset"/> in a method body
    /// — which matters, because an IL branch is encoded as a distance from the end of itself.
    /// </summary>
    public static AssembleResult Encode(string? text, int offset)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return AssembleResult.Failed("nothing to assemble");
        }

        string trimmed = text.Trim();
        if (trimmed.StartsWith("bytes:", StringComparison.OrdinalIgnoreCase))
        {
            return Hex(trimmed["bytes:".Length..]);
        }

        var bytes = new List<byte>();
        foreach (string line in trimmed.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string statement = Strip(line);
            if (statement.Length == 0)
            {
                continue;
            }

            if (One(statement, offset + bytes.Count, bytes) is { } problem)
            {
                return AssembleResult.Failed(problem);
            }
        }

        return bytes.Count == 0
            ? AssembleResult.Failed("nothing to assemble")
            : new AssembleResult(bytes.ToArray(), null);
    }

    /// <summary>Adds one statement, or returns why it could not be added.</summary>
    private static string? One(string statement, int at, List<byte> bytes)
    {
        string[] parts = statement.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
        string mnemonic = parts[0];
        string? operand = parts.Length > 1 ? parts[1].Trim() : null;

        if (Il.Named(mnemonic) is not { } op)
        {
            return $"'{mnemonic}' is not an IL instruction";
        }

        switch (op.OperandType)
        {
            case OperandType.InlineNone:
                if (operand is { Length: > 0 })
                {
                    return $"{op.Name} takes no operand";
                }

                Il.Write(op, bytes);
                return null;

            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar:
                return Immediate(op, operand, at, bytes, width: 1);

            case OperandType.InlineI:
                return Immediate(op, operand, at, bytes, width: 4);

            case OperandType.InlineVar:
                return Immediate(op, operand, at, bytes, width: 2);

            case OperandType.ShortInlineBrTarget:
                return Branch(op, operand, at, bytes, width: 1);

            case OperandType.InlineBrTarget:
                return Branch(op, operand, at, bytes, width: 4);

            // Everything left names something in the metadata tables, and this cannot add a row to
            // one. Refused by name rather than approximated, because an instruction assembled
            // against a token that means something else is the worst outcome available here.
            default:
                return $"{op.Name} takes a metadata token, which a byte patch cannot create. "
                       + "Use bytes: with a token you have read from a listing, if you know one that fits.";
        }
    }

    private static string? Immediate(OpCode op, string? operand, int at, List<byte> bytes, int width)
    {
        if (operand is not { Length: > 0 })
        {
            return $"{op.Name} needs a number";
        }

        if (Number(operand) is not { } value)
        {
            return $"'{operand}' is not a number";
        }

        long low = width switch { 1 => sbyte.MinValue, 2 => short.MinValue, _ => int.MinValue };
        long high = width switch { 1 => byte.MaxValue, 2 => ushort.MaxValue, _ => uint.MaxValue };
        if (value < low || value > high)
        {
            return $"{value} does not fit in the {width} byte(s) {op.Name} has for it";
        }

        _ = at;
        Il.Write(op, bytes);
        Emit(bytes, value, width);
        return null;
    }

    /// <summary>
    /// A branch, whose operand is the distance from the end of the instruction to the target.
    ///
    /// The target is written as a listing would print it — <c>IL_0012</c> — or as a bare offset,
    /// because those are the two things in front of whoever is typing it. Never as a distance: a
    /// caller computing the relative value by hand has to know the instruction's own length first,
    /// and getting that wrong produces a branch into the middle of another instruction.
    /// </summary>
    private static string? Branch(OpCode op, string? operand, int at, List<byte> bytes, int width)
    {
        if (operand is not { Length: > 0 })
        {
            return $"{op.Name} needs a target, as IL_0012 or an offset";
        }

        string text = operand.Trim();
        if (text.StartsWith("IL_", StringComparison.OrdinalIgnoreCase))
        {
            text = text[3..];
            if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int label))
            {
                return $"'{operand}' is not a label; they read as IL_0012";
            }

            return Relative(op, label, at, bytes, width);
        }

        return Number(text) is { } target
            ? Relative(op, (int)target, at, bytes, width)
            : $"'{operand}' is not a label or an offset";
    }

    private static string? Relative(OpCode op, int target, int at, List<byte> bytes, int width)
    {
        long distance = target - (at + op.Size + width);
        if (width == 1 && distance is < sbyte.MinValue or > sbyte.MaxValue)
        {
            string name = op.Name ?? "this branch";
            return $"IL_{target:X4} is {distance} bytes away, which {name} cannot reach. "
                   + $"Use {name.Replace(".s", string.Empty, StringComparison.Ordinal)}, which has four bytes for it.";
        }

        Il.Write(op, bytes);
        Emit(bytes, distance, width);
        return null;
    }

    private static void Emit(List<byte> bytes, long value, int width)
    {
        for (int i = 0; i < width; i++)
        {
            bytes.Add((byte)((value >> (i * 8)) & 0xFF));
        }
    }

    private static long? Number(string text)
    {
        string trimmed = text.Trim();
        bool negative = trimmed.StartsWith('-');
        if (negative)
        {
            trimmed = trimmed[1..].TrimStart();
        }

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex)
                ? negative ? -hex : hex
                : null;
        }

        return long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? negative ? -value : value
            : null;
    }

    private static AssembleResult Hex(string text)
    {
        string cleaned = new(text.Where(c => !char.IsWhiteSpace(c) && c != ',').ToArray());
        if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[2..];
        }

        if (cleaned.Length == 0 || cleaned.Length % 2 != 0)
        {
            return AssembleResult.Failed("give whole bytes, two hex digits each");
        }

        try
        {
            return new AssembleResult(Convert.FromHexString(cleaned), null);
        }
        catch (FormatException)
        {
            return AssembleResult.Failed("that is not hex");
        }
    }

    /// <summary>Drops a trailing comment, so a line pasted out of a listing still assembles.</summary>
    private static string Strip(string line)
    {
        int slashes = line.IndexOf("//", StringComparison.Ordinal);
        return (slashes >= 0 ? line[..slashes] : line).Trim();
    }
}
