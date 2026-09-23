using System.Buffers.Binary;

namespace Spydate.Core.Jvm;

/// <summary>What follows an opcode, which decides how it is decoded and how its operands read.</summary>
public enum OperandKind : byte
{
    None,

    /// <summary><c>bipush</c>: a signed byte.</summary>
    SignedByte,

    /// <summary><c>sipush</c>: a signed short.</summary>
    SignedShort,

    /// <summary>A local variable slot, one byte (two after <c>wide</c>).</summary>
    Local,

    /// <summary><c>ldc</c>: a one-byte constant pool index.</summary>
    PoolByte,

    /// <summary>A two-byte constant pool index: a field, method, class or constant.</summary>
    Pool,

    /// <summary><c>iinc</c>: a slot and a signed increment.</summary>
    Increment,

    /// <summary>A two-byte signed branch offset.</summary>
    Branch,

    /// <summary><c>goto_w</c>, <c>jsr_w</c>: a four-byte signed branch offset.</summary>
    WideBranch,

    /// <summary><c>newarray</c>: a primitive element type code.</summary>
    ArrayType,

    /// <summary><c>invokeinterface</c>: a pool index, an argument count and a zero.</summary>
    Interface,

    /// <summary><c>invokedynamic</c>: a pool index and two zeros.</summary>
    Dynamic,

    /// <summary><c>multianewarray</c>: a pool index and a dimension count.</summary>
    MultiArray,

    TableSwitch,
    LookupSwitch,

    /// <summary><c>wide</c>: widens the instruction after it, and is decoded as part of it.</summary>
    Wide,

    /// <summary>Not a JVM opcode — a reserved one, or a byte no specification assigns.</summary>
    Invalid,
}

/// <summary>
/// One decoded instruction. <see cref="Operand"/> is the slot, pool index, immediate or absolute branch target;
/// <see cref="Operand2"/> the increment of <c>iinc</c>, the count of <c>invokeinterface</c> or the dimensions of
/// <c>multianewarray</c>. A switch's absolute targets are in <see cref="Cases"/> and <see cref="Default"/>.
/// </summary>
public sealed record JvmInstruction(int Offset, int Length, byte Opcode, bool IsWide, int Operand, int Operand2)
{
    public string Mnemonic => Bytecode.Mnemonic(Opcode);

    public OperandKind Kind => Bytecode.Operands(Opcode);

    public IReadOnlyList<(int Match, int Target)>? Cases { get; init; }

    public int Default { get; init; }

    /// <summary>Set when the bytes could not be decoded: an unassigned opcode, or code that ends mid-instruction.</summary>
    public string? Problem { get; init; }
}

/// <summary>The JVM instruction set: names, operand shapes, and a decoder that reports bad code rather than throwing on it.</summary>
public static class Bytecode
{
    private static readonly string[] Names =
    [
        "nop", "aconst_null", "iconst_m1", "iconst_0", "iconst_1", "iconst_2", "iconst_3", "iconst_4", "iconst_5",
        "lconst_0", "lconst_1", "fconst_0", "fconst_1", "fconst_2", "dconst_0", "dconst_1", "bipush", "sipush",
        "ldc", "ldc_w", "ldc2_w", "iload", "lload", "fload", "dload", "aload", "iload_0", "iload_1", "iload_2",
        "iload_3", "lload_0", "lload_1", "lload_2", "lload_3", "fload_0", "fload_1", "fload_2", "fload_3",
        "dload_0", "dload_1", "dload_2", "dload_3", "aload_0", "aload_1", "aload_2", "aload_3", "iaload", "laload",
        "faload", "daload", "aaload", "baload", "caload", "saload", "istore", "lstore", "fstore", "dstore", "astore",
        "istore_0", "istore_1", "istore_2", "istore_3", "lstore_0", "lstore_1", "lstore_2", "lstore_3", "fstore_0",
        "fstore_1", "fstore_2", "fstore_3", "dstore_0", "dstore_1", "dstore_2", "dstore_3", "astore_0", "astore_1",
        "astore_2", "astore_3", "iastore", "lastore", "fastore", "dastore", "aastore", "bastore", "castore",
        "sastore", "pop", "pop2", "dup", "dup_x1", "dup_x2", "dup2", "dup2_x1", "dup2_x2", "swap", "iadd", "ladd",
        "fadd", "dadd", "isub", "lsub", "fsub", "dsub", "imul", "lmul", "fmul", "dmul", "idiv", "ldiv", "fdiv",
        "ddiv", "irem", "lrem", "frem", "drem", "ineg", "lneg", "fneg", "dneg", "ishl", "lshl", "ishr", "lshr",
        "iushr", "lushr", "iand", "land", "ior", "lor", "ixor", "lxor", "iinc", "i2l", "i2f", "i2d", "l2i", "l2f",
        "l2d", "f2i", "f2l", "f2d", "d2i", "d2l", "d2f", "i2b", "i2c", "i2s", "lcmp", "fcmpl", "fcmpg", "dcmpl",
        "dcmpg", "ifeq", "ifne", "iflt", "ifge", "ifgt", "ifle", "if_icmpeq", "if_icmpne", "if_icmplt", "if_icmpge",
        "if_icmpgt", "if_icmple", "if_acmpeq", "if_acmpne", "goto", "jsr", "ret", "tableswitch", "lookupswitch",
        "ireturn", "lreturn", "freturn", "dreturn", "areturn", "return", "getstatic", "putstatic", "getfield",
        "putfield", "invokevirtual", "invokespecial", "invokestatic", "invokeinterface", "invokedynamic", "new",
        "newarray", "anewarray", "arraylength", "athrow", "checkcast", "instanceof", "monitorenter", "monitorexit",
        "wide", "multianewarray", "ifnull", "ifnonnull", "goto_w", "jsr_w",
    ];

    public static string Mnemonic(byte opcode) => opcode < Names.Length ? Names[opcode] : $"op_{opcode:x2}";

    public static OperandKind Operands(byte opcode) => opcode switch
    {
        0x10 => OperandKind.SignedByte,
        0x11 => OperandKind.SignedShort,
        0x12 => OperandKind.PoolByte,
        0x13 or 0x14 => OperandKind.Pool,
        >= 0x15 and <= 0x19 => OperandKind.Local,
        >= 0x36 and <= 0x3A => OperandKind.Local,
        0x84 => OperandKind.Increment,
        >= 0x99 and <= 0xA8 => OperandKind.Branch,
        0xA9 => OperandKind.Local,
        0xAA => OperandKind.TableSwitch,
        0xAB => OperandKind.LookupSwitch,
        >= 0xB2 and <= 0xB8 => OperandKind.Pool,
        0xB9 => OperandKind.Interface,
        0xBA => OperandKind.Dynamic,
        0xBB => OperandKind.Pool,
        0xBC => OperandKind.ArrayType,
        0xBD => OperandKind.Pool,
        0xC0 or 0xC1 => OperandKind.Pool,
        0xC4 => OperandKind.Wide,
        0xC5 => OperandKind.MultiArray,
        0xC6 or 0xC7 => OperandKind.Branch,
        0xC8 or 0xC9 => OperandKind.WideBranch,
        < 0xCA => OperandKind.None,
        _ => OperandKind.Invalid,
    };

    /// <summary>The element type <c>newarray</c> names by code.</summary>
    public static string ArrayTypeName(int code) => code switch
    {
        4 => "boolean",
        5 => "char",
        6 => "float",
        7 => "double",
        8 => "byte",
        9 => "short",
        10 => "int",
        11 => "long",
        _ => $"type {code}",
    };

    /// <summary>
    /// Decodes a method's code from start to end. Bad code does not throw: an unassigned opcode or an instruction
    /// cut short by the end of the code becomes one last instruction with <see cref="JvmInstruction.Problem"/>
    /// set, and decoding stops there — nothing after it can be trusted to start on an instruction boundary.
    /// </summary>
    public static IReadOnlyList<JvmInstruction> Decode(ReadOnlySpan<byte> code)
    {
        var list = new List<JvmInstruction>(code.Length / 2);
        int at = 0;
        while (at < code.Length)
        {
            var instruction = DecodeOne(code, at);
            list.Add(instruction);
            if (instruction.Problem is not null)
            {
                break;
            }

            at += instruction.Length;
        }

        return list;
    }

    private static JvmInstruction DecodeOne(ReadOnlySpan<byte> code, int at)
    {
        byte opcode = code[at];
        int left = code.Length - at - 1;
        int rest = code.Length - at;
        JvmInstruction Short(string problem) => new(at, rest, opcode, false, 0, 0) { Problem = problem };

        switch (Operands(opcode))
        {
            case OperandKind.None:
                return new JvmInstruction(at, 1, opcode, false, 0, 0);
            case OperandKind.SignedByte:
                return left < 1 ? Short("cut short") : new JvmInstruction(at, 2, opcode, false, (sbyte)code[at + 1], 0);
            case OperandKind.SignedShort:
                return left < 2 ? Short("cut short") : new JvmInstruction(at, 3, opcode, false, S2(code, at + 1), 0);
            case OperandKind.Local:
            case OperandKind.PoolByte:
            case OperandKind.ArrayType:
                return left < 1 ? Short("cut short") : new JvmInstruction(at, 2, opcode, false, code[at + 1], 0);
            case OperandKind.Pool:
                return left < 2 ? Short("cut short") : new JvmInstruction(at, 3, opcode, false, U2(code, at + 1), 0);
            case OperandKind.Increment:
                return left < 2 ? Short("cut short") : new JvmInstruction(at, 3, opcode, false, code[at + 1], (sbyte)code[at + 2]);
            case OperandKind.Branch:
                return left < 2 ? Short("cut short") : new JvmInstruction(at, 3, opcode, false, at + S2(code, at + 1), 0);
            case OperandKind.WideBranch:
                return left < 4 ? Short("cut short") : new JvmInstruction(at, 5, opcode, false, at + S4(code, at + 1), 0);
            case OperandKind.Interface:
                return left < 4 ? Short("cut short") : new JvmInstruction(at, 5, opcode, false, U2(code, at + 1), code[at + 3]);
            case OperandKind.Dynamic:
                return left < 4 ? Short("cut short") : new JvmInstruction(at, 5, opcode, false, U2(code, at + 1), 0);
            case OperandKind.MultiArray:
                return left < 3 ? Short("cut short") : new JvmInstruction(at, 4, opcode, false, U2(code, at + 1), code[at + 3]);
            case OperandKind.Wide:
                if (left < 1)
                {
                    return Short("cut short");
                }

                byte widened = code[at + 1];
                if (widened == 0x84)
                {
                    return left < 5 ? Short("cut short") : new JvmInstruction(at, 6, widened, true, U2(code, at + 2), S2(code, at + 4));
                }

                return Operands(widened) == OperandKind.Local
                    ? left < 3 ? Short("cut short") : new JvmInstruction(at, 4, widened, true, U2(code, at + 2), 0)
                    : Short($"wide cannot widen {Mnemonic(widened)}");
            case OperandKind.TableSwitch:
                return Switch(code, at, table: true);
            case OperandKind.LookupSwitch:
                return Switch(code, at, table: false);
            default:
                return Short("not an opcode");
        }
    }

    /// <summary>
    /// A switch: padding to a four-byte boundary (from the start of the code), then the default and the cases. The
    /// case count is checked against the bytes left before anything is allocated, so a forged range cannot.
    /// </summary>
    private static JvmInstruction Switch(ReadOnlySpan<byte> code, int at, bool table)
    {
        byte opcode = code[at];
        int p = (at + 4) & ~3;
        int rest = code.Length - at;
        JvmInstruction Short() => new(at, rest, opcode, false, 0, 0) { Problem = "cut short" };

        if (p + (table ? 12 : 8) > code.Length)
        {
            return Short();
        }

        int fallback = at + S4(code, p);
        var cases = new List<(int, int)>();
        int end;
        if (table)
        {
            int low = S4(code, p + 4);
            int high = S4(code, p + 8);
            long count = (long)high - low + 1;
            if (count < 0 || p + 12 + count * 4 > code.Length)
            {
                return Short();
            }

            for (int i = 0; i < count; i++)
            {
                cases.Add((low + i, at + S4(code, p + 12 + (i * 4))));
            }

            end = p + 12 + (int)(count * 4);
        }
        else
        {
            int pairs = S4(code, p + 4);
            if (pairs < 0 || p + 8 + ((long)pairs * 8) > code.Length)
            {
                return Short();
            }

            for (int i = 0; i < pairs; i++)
            {
                cases.Add((S4(code, p + 8 + (i * 8)), at + S4(code, p + 12 + (i * 8))));
            }

            end = p + 8 + (pairs * 8);
        }

        return new JvmInstruction(at, end - at, opcode, false, 0, 0) { Cases = cases, Default = fallback };
    }

    private static int U2(ReadOnlySpan<byte> code, int at) => BinaryPrimitives.ReadUInt16BigEndian(code[at..]);

    private static int S2(ReadOnlySpan<byte> code, int at) => BinaryPrimitives.ReadInt16BigEndian(code[at..]);

    private static int S4(ReadOnlySpan<byte> code, int at) => BinaryPrimitives.ReadInt32BigEndian(code[at..]);
}
