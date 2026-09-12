using System.Collections.Immutable;
using System.Reflection.Emit;

namespace Spydate.Decompiler.Managed;

/// <summary>One decoded IL instruction: where it starts, how long it is, and what it is.</summary>
public readonly record struct IlInstruction(int Offset, int Length, OpCode Op)
{
    /// <summary>Where the operand starts, which is the offset past the opcode itself.</summary>
    public int OperandAt => Offset + Op.Size;

    public int OperandLength => Length - Op.Size;

    public override string ToString() => $"IL_{Offset:X4}: {Op.Name}";
}

/// <summary>
/// Walking a method body's instruction stream.
///
/// Shared rather than written twice: the reference scan and the patcher must agree exactly on where
/// one instruction ends and the next begins, and two walkers that disagree by a byte would produce a
/// patch placed by one and read back by the other.
///
/// Everything here is over untrusted bytes. Reads are bounds-checked, and an opcode this does not
/// know ends the walk rather than the process — a body whose tail is not decodable still yields
/// everything before it, which is more useful than nothing and is never silently more than that.
/// </summary>
public static class Il
{
    /// <summary>The one-byte NOP, which is what a shortened replacement is padded with.</summary>
    public const byte Nop = 0x00;

    private static readonly OpCode?[] Short = new OpCode?[0x100];
    private static readonly OpCode?[] Long = new OpCode?[0x100];

    static Il()
    {
        // Taken from the runtime rather than transcribed. Every opcode's size and operand shape is
        // already stated by OpCodes, and a hand-copied table of two hundred entries is a thing that
        // is wrong in one place and mis-decodes one instruction — which, in a stream whose
        // boundaries are found by walking, throws off everything after it.
        foreach (var field in typeof(OpCodes).GetFields())
        {
            if (field.GetValue(null) is OpCode op)
            {
                (op.Size == 1 ? Short : Long)[op.Value & 0xFF] = op;
            }
        }
    }

    /// <summary>Every instruction from the start, stopping at the first byte that is not one.</summary>
    public static IEnumerable<IlInstruction> Walk(ImmutableArray<byte> il)
    {
        int pos = 0;
        while (TryDecode(il, pos, out var instruction))
        {
            yield return instruction;
            pos += instruction.Length;
        }
    }

    /// <summary>The instruction beginning exactly at <paramref name="at"/>, if one does.</summary>
    public static bool TryDecode(ImmutableArray<byte> il, int at, out IlInstruction instruction)
    {
        instruction = default;
        if (at < 0 || at >= il.Length)
        {
            return false;
        }

        int pos = at;
        byte first = il[pos++];
        OpCode? op;
        if (first == 0xFE)
        {
            op = pos < il.Length ? Long[il[pos++]] : null;
        }
        else
        {
            op = Short[first];
        }

        if (op is not { } code)
        {
            return false;
        }

        int operand = OperandSize(code.OperandType, il, pos);
        if (operand < 0 || pos + operand > il.Length)
        {
            return false;
        }

        instruction = new IlInstruction(at, pos + operand - at, code);
        return true;
    }

    /// <summary>
    /// The instruction covering an offset, which is not always one that starts there.
    ///
    /// An offset read off a listing is an instruction's own; an offset arrived at some other way can
    /// land inside one, and a patch placed there would overwrite the tail of an instruction and
    /// leave its head to be decoded against whatever follows.
    /// </summary>
    public static bool TryCovering(ImmutableArray<byte> il, int offset, out IlInstruction instruction)
    {
        foreach (var candidate in Walk(il))
        {
            if (offset >= candidate.Offset && offset < candidate.Offset + candidate.Length)
            {
                instruction = candidate;
                return true;
            }

            if (candidate.Offset > offset)
            {
                break;
            }
        }

        instruction = default;
        return false;
    }

    /// <summary>The opcode named that way, or null. Case-insensitive, as people type them.</summary>
    public static OpCode? Named(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var table in new[] { Short, Long })
        {
            foreach (var op in table)
            {
                if (op is { } code && string.Equals(code.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return code;
                }
            }
        }

        return null;
    }

    /// <summary>Writes an opcode's own bytes, which is one for most and two for the 0xFE family.</summary>
    public static void Write(OpCode op, List<byte> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (op.Size == 2)
        {
            into.Add(0xFE);
        }

        into.Add((byte)(op.Value & 0xFF));
    }

    /// <summary>How many bytes of operand follow, or -1 when that cannot be worked out.</summary>
    public static int OperandSize(OperandType type, ImmutableArray<byte> il, int at) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
            or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
            or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,

        // A jump table: four bytes of count, then that many targets. The count comes out of the file
        // and is checked against what is left, because a body claiming a billion arms would
        // otherwise be an overflow rather than an unreadable method.
        OperandType.InlineSwitch => at + 4 > il.Length
            ? -1
            : Arms(il, at) is { } arms && arms <= (il.Length - at - 4) / 4
                ? 4 + (arms * 4)
                : -1,
        _ => -1,
    };

    private static int? Arms(ImmutableArray<byte> il, int at)
    {
        long count = (uint)(il[at] | (il[at + 1] << 8) | (il[at + 2] << 16) | (il[at + 3] << 24));
        return count > int.MaxValue / 4 ? null : (int)count;
    }
}
