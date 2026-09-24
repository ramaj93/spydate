namespace Spydate.Core.Dex;

/// <summary>How an instruction's operands are laid out in its code units, by the names the Dalvik spec uses.</summary>
public enum DalvikFormat : byte
{
    F10x, F12x, F11n, F11x, F10t, F20t, F22x, F21t, F21s, F21h, F21c, F23x, F22b, F22t, F22s, F22c,
    F30t, F32x, F31i, F31t, F31c, F35c, F3rc, F45cc, F4rcc, F51l, Unused,
}

/// <summary>What an instruction's index operand names.</summary>
public enum DalvikIndex : byte
{
    None, String, Type, Field, Method, CallSite, MethodHandle, Proto,
}

/// <summary>
/// One decoded Dalvik instruction. <see cref="A"/>, <see cref="B"/>, <see cref="C"/> are the format's register or
/// small-literal fields; <see cref="Literal"/> a constant; <see cref="Index"/> a string/type/field/method/call-site
/// index; <see cref="Target"/> a branch's absolute address in code units; <see cref="Registers"/> an invoke's
/// argument registers. A switch or fill-array-data carries its payload, read from where the instruction points.
/// </summary>
public sealed record DalvikInstruction(int Address, int Units, byte Opcode, int A, int B, int C, long Literal, int Index, int Target)
{
    public string Mnemonic => Dalvik.Mnemonic(Opcode);

    public DalvikFormat Format => Dalvik.FormatOf(Opcode);

    public DalvikIndex IndexKind => Dalvik.IndexOf(Opcode);

    public IReadOnlyList<int> Registers { get; init; } = [];

    /// <summary>For <c>invoke-polymorphic</c>, the prototype index.</summary>
    public int Proto { get; init; }

    /// <summary>A packed or sparse switch's cases, as (key, absolute target).</summary>
    public IReadOnlyList<(int Key, int Target)>? Cases { get; init; }

    /// <summary>A fill-array-data's element width and elements.</summary>
    public (int Width, IReadOnlyList<long> Values)? ArrayData { get; init; }

    /// <summary>Why the instruction could not be read as it says, when it could not.</summary>
    public string? Problem { get; init; }
}

/// <summary>The Dalvik instruction set: names, formats, and a decoder over a method's 16-bit code units.</summary>
public static class Dalvik
{
    private static readonly string[] Names = BuildNames();
    private static readonly DalvikFormat[] Formats = BuildFormats();

    public static string Mnemonic(byte opcode) => Names[opcode];

    public static DalvikFormat FormatOf(byte opcode) => Formats[opcode];

    public static DalvikIndex IndexOf(byte opcode) => opcode switch
    {
        0x1A or 0x1B => DalvikIndex.String,
        0x1C or 0x1F or 0x20 or 0x22 or 0x23 or 0x24 or 0x25 => DalvikIndex.Type,
        >= 0x52 and <= 0x6D => DalvikIndex.Field,
        >= 0x6E and <= 0x72 or >= 0x74 and <= 0x78 or 0xFA or 0xFB => DalvikIndex.Method,
        0xFC or 0xFD => DalvikIndex.CallSite,
        0xFE => DalvikIndex.MethodHandle,
        0xFF => DalvikIndex.Proto,
        _ => DalvikIndex.None,
    };

    /// <summary>How many code units an instruction of a format takes.</summary>
    public static int Width(DalvikFormat format) => format switch
    {
        DalvikFormat.F10x or DalvikFormat.F12x or DalvikFormat.F11n or DalvikFormat.F11x or DalvikFormat.F10t or DalvikFormat.Unused => 1,
        DalvikFormat.F20t or DalvikFormat.F22x or DalvikFormat.F21t or DalvikFormat.F21s or DalvikFormat.F21h or DalvikFormat.F21c
            or DalvikFormat.F23x or DalvikFormat.F22b or DalvikFormat.F22t or DalvikFormat.F22s or DalvikFormat.F22c => 2,
        DalvikFormat.F30t or DalvikFormat.F32x or DalvikFormat.F31i or DalvikFormat.F31t or DalvikFormat.F31c or DalvikFormat.F35c or DalvikFormat.F3rc => 3,
        DalvikFormat.F45cc or DalvikFormat.F4rcc => 4,
        DalvikFormat.F51l => 5,
        _ => 1,
    };

    /// <summary>
    /// Decodes every instruction of a method, skipping the switch and array payloads in the stream and reading them
    /// for the instructions that point at them. Truncated or impossible instructions end the listing with a
    /// problem noted, never an exception.
    /// </summary>
    public static IReadOnlyList<DalvikInstruction> Decode(ReadOnlySpan<ushort> units)
    {
        ushort[] code = units.ToArray();
        var instructions = new List<DalvikInstruction>();
        int at = 0;
        while (at < code.Length)
        {
            ushort unit = code[at];
            if (unit is 0x0100 or 0x0200 or 0x0300)
            {
                int payload = PayloadUnits(code, at);
                if (payload <= 0 || at + payload > code.Length)
                {
                    instructions.Add(new DalvikInstruction(at, code.Length - at, 0, 0, 0, 0, 0, 0, 0) { Problem = "a truncated payload" });
                    break;
                }

                at += payload;
                continue;
            }

            byte op = (byte)unit;
            var format = Formats[op];
            int width = Width(format);
            if (at + width > code.Length)
            {
                instructions.Add(new DalvikInstruction(at, code.Length - at, op, 0, 0, 0, 0, 0, 0) { Problem = "the instruction runs past the end of the code" });
                break;
            }

            var instruction = Read(code, at, op, format);
            if (instruction.Cases is null && op is 0x2B or 0x2C or 0x26)
            {
                instruction = WithPayload(code, instruction);
            }

            instructions.Add(instruction);
            at += width;
        }

        return instructions;
    }

    private static DalvikInstruction Read(ushort[] c, int at, byte op, DalvikFormat format)
    {
        int hi = c[at] >> 8;
        int nibbleA = hi & 0xF;
        int nibbleB = hi >> 4;
        int U(int i) => c[at + i];
        int S16(int i) => (short)c[at + i];
        int I32(int i) => c[at + i] | (c[at + i + 1] << 16);
        DalvikInstruction I(int a = 0, int b = 0, int cc = 0, long literal = 0, int index = 0, int target = 0)
            => new(at, Width(format), op, a, b, cc, literal, index, target);

        switch (format)
        {
            case DalvikFormat.F12x: return I(nibbleA, nibbleB);
            case DalvikFormat.F11n: return I(nibbleA, literal: (sbyte)(nibbleB << 4) >> 4);
            case DalvikFormat.F11x: return I(hi);
            case DalvikFormat.F10t: return I(target: at + (sbyte)hi);
            case DalvikFormat.F20t: return I(target: at + S16(1));
            case DalvikFormat.F22x: return I(hi, U(1));
            case DalvikFormat.F21t: return I(hi, target: at + S16(1));
            case DalvikFormat.F21s: return I(hi, literal: S16(1));
            case DalvikFormat.F21h: return I(hi, literal: op == 0x19 ? (long)S16(1) << 48 : S16(1) << 16);
            case DalvikFormat.F21c: return I(hi, index: U(1));
            case DalvikFormat.F23x: return I(hi, U(1) & 0xFF, U(1) >> 8);
            case DalvikFormat.F22b: return I(hi, U(1) & 0xFF, literal: (sbyte)(U(1) >> 8));
            case DalvikFormat.F22t: return I(nibbleA, nibbleB, target: at + S16(1));
            case DalvikFormat.F22s: return I(nibbleA, nibbleB, literal: S16(1));
            case DalvikFormat.F22c: return I(nibbleA, nibbleB, index: U(1));
            case DalvikFormat.F30t: return I(target: at + I32(1));
            case DalvikFormat.F32x: return I(U(1), U(2));
            case DalvikFormat.F31i: return I(hi, literal: op == 0x17 ? (long)I32(1) : I32(1));
            case DalvikFormat.F31t: return I(hi, target: at + I32(1));
            case DalvikFormat.F31c: return I(hi, index: I32(1));
            case DalvikFormat.F35c:
            case DalvikFormat.F45cc:
            {
                int count = nibbleB;
                int g = nibbleA;
                int regs = U(2);
                var list = new List<int>(5);
                int[] all = [regs & 0xF, (regs >> 4) & 0xF, (regs >> 8) & 0xF, regs >> 12, g];
                for (int i = 0; i < Math.Min(count, 5); i++)
                {
                    list.Add(all[i]);
                }

                var decoded = I(count, index: U(1)) with { Registers = list, Proto = format == DalvikFormat.F45cc ? U(3) : 0 };
                return count > 5 ? decoded with { Problem = $"an argument count of {count}, past five" } : decoded;
            }

            case DalvikFormat.F3rc:
            case DalvikFormat.F4rcc:
            {
                int first = U(2);
                return I(hi, index: U(1)) with
                {
                    Registers = Enumerable.Range(first, hi).ToList(),
                    Proto = format == DalvikFormat.F4rcc ? U(3) : 0,
                };
            }

            case DalvikFormat.F51l:
                return I(hi, literal: (long)((ulong)c[at + 1] | ((ulong)c[at + 2] << 16) | ((ulong)c[at + 3] << 32) | ((ulong)c[at + 4] << 48)));
            case DalvikFormat.Unused:
                return I() with { Problem = $"opcode 0x{op:X2} is not one Android runs" };
            default:
                return I();
        }
    }

    /// <summary>A payload's length in code units, or 0 when its header is cut off.</summary>
    private static int PayloadUnits(ReadOnlySpan<ushort> c, int at)
    {
        if (at + 2 > c.Length)
        {
            return 0;
        }

        switch (c[at])
        {
            case 0x0100:
                return (c[at + 1] * 2) + 4;
            case 0x0200:
                return (c[at + 1] * 4) + 2;
            default:
                if (at + 4 > c.Length)
                {
                    return 0;
                }

                long bytes = (long)c[at + 1] * (c[at + 2] | ((long)c[at + 3] << 16));
                return bytes > int.MaxValue ? 0 : (int)((bytes + 1) / 2) + 4;
        }
    }

    private static DalvikInstruction WithPayload(ushort[] c, DalvikInstruction instruction)
    {
        int at = instruction.Target;
        int units = at >= 0 && at < c.Length ? PayloadUnits(c, at) : 0;
        if (units <= 0 || at + units > c.Length)
        {
            return instruction with { Problem = "its payload is missing or cut off" };
        }

        int I32(int i) => c[i] | (c[i + 1] << 16);
        int size = c[at + 1];
        switch (instruction.Opcode)
        {
            case 0x2B when c[at] == 0x0100:
            {
                int first = I32(at + 2);
                var cases = new List<(int, int)>(size);
                for (int i = 0; i < size; i++)
                {
                    cases.Add((first + i, instruction.Address + I32(at + 4 + (i * 2))));
                }

                return instruction with { Cases = cases };
            }

            case 0x2C when c[at] == 0x0200:
            {
                var cases = new List<(int, int)>(size);
                for (int i = 0; i < size; i++)
                {
                    cases.Add((I32(at + 2 + (i * 2)), instruction.Address + I32(at + 2 + (size * 2) + (i * 2))));
                }

                return instruction with { Cases = cases };
            }

            case 0x26 when c[at] == 0x0300:
            {
                int width = c[at + 1];
                int count = c[at + 2] | (c[at + 3] << 16);
                if (width is not (1 or 2 or 4 or 8))
                {
                    return instruction with { Problem = $"an element width of {width}" };
                }

                var values = new List<long>(count);
                for (int i = 0; i < count; i++)
                {
                    long value = 0;
                    for (int b = 0; b < width; b++)
                    {
                        int offset = (i * width) + b;
                        int unit = c[at + 4 + (offset / 2)];
                        value |= (long)((offset % 2 == 0 ? unit : unit >> 8) & 0xFF) << (b * 8);
                    }

                    int shift = 64 - (width * 8);
                    values.Add(shift == 0 ? value : (value << shift) >> shift);
                }

                return instruction with { ArrayData = (width, values) };
            }

            default:
                return instruction with { Problem = "it points at a payload of the wrong kind" };
        }
    }

    private static string[] BuildNames()
    {
        var names = new string[256];
        for (int i = 0; i < 256; i++)
        {
            names[i] = $"unused-{i:x2}";
        }

        void Set(int start, params string[] list)
        {
            for (int i = 0; i < list.Length; i++)
            {
                names[start + i] = list[i];
            }
        }

        Set(0x00, "nop", "move", "move/from16", "move/16", "move-wide", "move-wide/from16", "move-wide/16", "move-object", "move-object/from16",
            "move-object/16", "move-result", "move-result-wide", "move-result-object", "move-exception", "return-void", "return", "return-wide",
            "return-object", "const/4", "const/16", "const", "const/high16", "const-wide/16", "const-wide/32", "const-wide", "const-wide/high16",
            "const-string", "const-string/jumbo", "const-class", "monitor-enter", "monitor-exit", "check-cast", "instance-of", "array-length",
            "new-instance", "new-array", "filled-new-array", "filled-new-array/range", "fill-array-data", "throw", "goto", "goto/16", "goto/32",
            "packed-switch", "sparse-switch", "cmpl-float", "cmpg-float", "cmpl-double", "cmpg-double", "cmp-long", "if-eq", "if-ne", "if-lt",
            "if-ge", "if-gt", "if-le", "if-eqz", "if-nez", "if-ltz", "if-gez", "if-gtz", "if-lez");
        string[] kinds = ["", "-wide", "-object", "-boolean", "-byte", "-char", "-short"];
        Set(0x44, [.. kinds.Select(k => "aget" + k), .. kinds.Select(k => "aput" + k)]);
        Set(0x52, [.. kinds.Select(k => "iget" + k), .. kinds.Select(k => "iput" + k)]);
        Set(0x60, [.. kinds.Select(k => "sget" + k), .. kinds.Select(k => "sput" + k)]);
        Set(0x6E, "invoke-virtual", "invoke-super", "invoke-direct", "invoke-static", "invoke-interface");
        Set(0x74, "invoke-virtual/range", "invoke-super/range", "invoke-direct/range", "invoke-static/range", "invoke-interface/range");
        Set(0x7B, "neg-int", "not-int", "neg-long", "not-long", "neg-float", "neg-double", "int-to-long", "int-to-float", "int-to-double",
            "long-to-int", "long-to-float", "long-to-double", "float-to-int", "float-to-long", "float-to-double", "double-to-int", "double-to-long",
            "double-to-float", "int-to-byte", "int-to-char", "int-to-short");
        string[] binops = ["add-int", "sub-int", "mul-int", "div-int", "rem-int", "and-int", "or-int", "xor-int", "shl-int", "shr-int", "ushr-int",
            "add-long", "sub-long", "mul-long", "div-long", "rem-long", "and-long", "or-long", "xor-long", "shl-long", "shr-long", "ushr-long",
            "add-float", "sub-float", "mul-float", "div-float", "rem-float", "add-double", "sub-double", "mul-double", "div-double", "rem-double"];
        Set(0x90, binops);
        Set(0xB0, [.. binops.Select(b => b + "/2addr")]);
        Set(0xD0, "add-int/lit16", "rsub-int", "mul-int/lit16", "div-int/lit16", "rem-int/lit16", "and-int/lit16", "or-int/lit16", "xor-int/lit16");
        Set(0xD8, "add-int/lit8", "rsub-int/lit8", "mul-int/lit8", "div-int/lit8", "rem-int/lit8", "and-int/lit8", "or-int/lit8", "xor-int/lit8",
            "shl-int/lit8", "shr-int/lit8", "ushr-int/lit8");
        Set(0xFA, "invoke-polymorphic", "invoke-polymorphic/range", "invoke-custom", "invoke-custom/range", "const-method-handle", "const-method-type");
        return names;
    }

    private static DalvikFormat[] BuildFormats()
    {
        var formats = new DalvikFormat[256];
        Array.Fill(formats, DalvikFormat.Unused);
        void Set(int start, int end, DalvikFormat format)
        {
            for (int i = start; i <= end; i++)
            {
                formats[i] = format;
            }
        }

        formats[0x00] = DalvikFormat.F10x;
        formats[0x01] = DalvikFormat.F12x;
        formats[0x02] = DalvikFormat.F22x;
        formats[0x03] = DalvikFormat.F32x;
        formats[0x04] = DalvikFormat.F12x;
        formats[0x05] = DalvikFormat.F22x;
        formats[0x06] = DalvikFormat.F32x;
        formats[0x07] = DalvikFormat.F12x;
        formats[0x08] = DalvikFormat.F22x;
        formats[0x09] = DalvikFormat.F32x;
        Set(0x0A, 0x0D, DalvikFormat.F11x);
        formats[0x0E] = DalvikFormat.F10x;
        Set(0x0F, 0x11, DalvikFormat.F11x);
        formats[0x12] = DalvikFormat.F11n;
        formats[0x13] = DalvikFormat.F21s;
        formats[0x14] = DalvikFormat.F31i;
        formats[0x15] = DalvikFormat.F21h;
        formats[0x16] = DalvikFormat.F21s;
        formats[0x17] = DalvikFormat.F31i;
        formats[0x18] = DalvikFormat.F51l;
        formats[0x19] = DalvikFormat.F21h;
        formats[0x1A] = DalvikFormat.F21c;
        formats[0x1B] = DalvikFormat.F31c;
        formats[0x1C] = DalvikFormat.F21c;
        Set(0x1D, 0x1E, DalvikFormat.F11x);
        formats[0x1F] = DalvikFormat.F21c;
        formats[0x20] = DalvikFormat.F22c;
        formats[0x21] = DalvikFormat.F12x;
        formats[0x22] = DalvikFormat.F21c;
        formats[0x23] = DalvikFormat.F22c;
        formats[0x24] = DalvikFormat.F35c;
        formats[0x25] = DalvikFormat.F3rc;
        formats[0x26] = DalvikFormat.F31t;
        formats[0x27] = DalvikFormat.F11x;
        formats[0x28] = DalvikFormat.F10t;
        formats[0x29] = DalvikFormat.F20t;
        formats[0x2A] = DalvikFormat.F30t;
        Set(0x2B, 0x2C, DalvikFormat.F31t);
        Set(0x2D, 0x31, DalvikFormat.F23x);
        Set(0x32, 0x37, DalvikFormat.F22t);
        Set(0x38, 0x3D, DalvikFormat.F21t);
        Set(0x44, 0x51, DalvikFormat.F23x);
        Set(0x52, 0x5F, DalvikFormat.F22c);
        Set(0x60, 0x6D, DalvikFormat.F21c);
        Set(0x6E, 0x72, DalvikFormat.F35c);
        Set(0x74, 0x78, DalvikFormat.F3rc);
        Set(0x7B, 0x8F, DalvikFormat.F12x);
        Set(0x90, 0xAF, DalvikFormat.F23x);
        Set(0xB0, 0xCF, DalvikFormat.F12x);
        Set(0xD0, 0xD7, DalvikFormat.F22s);
        Set(0xD8, 0xE2, DalvikFormat.F22b);
        formats[0xFA] = DalvikFormat.F45cc;
        formats[0xFB] = DalvikFormat.F4rcc;
        formats[0xFC] = DalvikFormat.F35c;
        formats[0xFD] = DalvikFormat.F3rc;
        Set(0xFE, 0xFF, DalvikFormat.F21c);
        return formats;
    }
}
