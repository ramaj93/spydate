using System.Buffers.Binary;
using System.Globalization;
using Spydate.Core.Binary;
using Spydate.Core.Symbols;

namespace Spydate.Disassembly.Arm64;

/// <summary>
/// The ARM64 decoder behind <see cref="IInstructionDecoder"/>: <see cref="Arm64Decoder"/> for each word, and what
/// analysis needs from ARM64 besides — where functions start, what pads them, and how a switch reads its table.
///
/// ARM64 builds an address in two instructions, <c>adrp</c> for the 4 KB page and an <c>add</c> or a load for the
/// rest, so no single instruction says what it refers to. Decoding runs a small register tracker along each
/// straight run of code and puts the address it arrives at on the instruction that uses it
/// (<see cref="DecodedInstruction.DataVa"/>), and the slot a register was loaded from on the <c>blr</c> that calls
/// through it (<see cref="DecodedInstruction.IndirectSlotVa"/>) — which is how a call to an import is named.
/// Thread-safe: each decode has its own tracker.
/// </summary>
public sealed class Arm64Disassembler : IInstructionDecoder
{
    private const uint Nop = 0xD503201F;

    private readonly SymbolTable? _symbols;

    public Arm64Disassembler(SymbolTable? symbols = null, AsmSyntax syntax = AsmSyntax.Intel)
    {
        _symbols = symbols;
        Syntax = syntax;
    }

    public Architecture Architecture => Architecture.Arm64;

    /// <summary>ARM64 has one assembly syntax; the setting is kept for the listing's sake and does not change the text.</summary>
    public AsmSyntax Syntax { get; }

    public int MaxInstructionLength => 4;

    public int InstructionAlignment => 4;

    public IReadOnlyList<DecodedInstruction> Decode(ReadOnlyMemory<byte> code, ulong va, ulong imageBase, int maxInstructions = int.MaxValue)
    {
        var result = new List<DecodedInstruction>();
        foreach (var ins in DecodeLazy(code, va, imageBase))
        {
            result.Add(ins);
            if (result.Count >= maxInstructions)
            {
                break;
            }
        }

        return result;
    }

    public IEnumerable<DecodedInstruction> DecodeLazy(ReadOnlyMemory<byte> code, ulong va, ulong imageBase)
    {
        var tracker = new Arm64Tracker();
        for (int offset = 0; offset + 4 <= code.Length; offset += 4)
        {
            ulong pc = va + (ulong)offset;
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(code.Span.Slice(offset, 4));
            var bytes = code.Slice(offset, 4);
            var decoded = Arm64Decoder.Decode(word, pc);
            if (decoded is null)
            {
                tracker.Reset();
                yield return new DecodedInstruction
                {
                    Va = pc,
                    Rva = (uint)(pc - imageBase),
                    Length = 4,
                    Bytes = bytes,
                    Mnemonic = ".inst",
                    Operands = "0x" + word.ToString("x8", CultureInfo.InvariantCulture),
                    Flow = InstructionFlow.Invalid,
                };
                continue;
            }

            var (data, kind, slot) = tracker.Step(decoded);
            var instruction = new DecodedInstruction
            {
                Va = pc,
                Rva = (uint)(pc - imageBase),
                Length = 4,
                Bytes = bytes,
                Mnemonic = decoded.Mnemonic,
                Operands = decoded.FormatOperands(Name),
                Flow = decoded.Flow,
                BranchTargetVa = decoded.Target,
                IndirectSlotVa = slot,
                DataVa = data,
                DataKind = kind,
                Arm64 = decoded,
            };

            // What a register holds is known along one path; after a jump, the next instruction starts another.
            if (instruction.EndsBlock)
            {
                tracker.Reset();
            }

            yield return instruction;
        }
    }

    public string FormatOperands(DecodedInstruction instruction) => instruction.Arm64?.FormatOperands(Name) ?? instruction.Operands;

    private string? Name(ulong address) => _symbols is not null && _symbols.TryGet(address, out var symbol) ? symbol.Name : null;

    /// <summary>Linkers pad ARM64 code with <c>nop</c>, and between functions with zeros too (<c>udf #0</c>).</summary>
    public bool IsPadding(ReadOnlySpan<byte> code, bool betweenFunctions = false)
    {
        if (code.Length < 4)
        {
            return false;
        }

        uint word = BinaryPrimitives.ReadUInt32LittleEndian(code);
        return word == Nop || word == 0;
    }

    /// <summary>
    /// The instructions compilers open a function with: a branch target landing pad (<c>bti c</c>), signing the
    /// return address (<c>paciasp</c>, <c>pacibsp</c>), or making the frame — <c>stp x29, x30, [sp, #-n]!</c>,
    /// another pre-indexed store to the stack, or <c>sub sp, sp, #n</c>.
    /// </summary>
    public bool LooksLikeFunctionStart(ReadOnlySpan<byte> code)
    {
        if (code.Length < 4)
        {
            return false;
        }

        uint word = BinaryPrimitives.ReadUInt32LittleEndian(code);
        return word is 0xD503245F or 0xD503233F or 0xD503237F
            || (word & 0xFFC003E0) == 0xA98003E0     // stp xN, xM, [sp, #-n]!
            || (word & 0xFFC003E0) == 0x6D8003E0     // stp dN, dM, [sp, #-n]!
            || (word & 0xFFE00FE0) == 0xF81F0FE0     // str xN, [sp, #-n]!  (negative offsets)
            || (word & 0xFF8003FF) == 0xD10003FF;    // sub sp, sp, #n
    }

    public bool NeverContinues(DecodedInstruction instruction)
        => instruction.Flow == InstructionFlow.Interrupt && instruction.Mnemonic is "brk" or "udf" or "hlt";

    public JumpTable? RecoverJumpTable(IReadOnlyList<DecodedInstruction> trailing, ICodeSource source, Func<ulong, bool>? accept)
        => Arm64JumpTables.TryRecover(trailing, source, accept);
}

/// <summary>
/// What the general registers hold along a straight run of ARM64 code, as far as it is a constant: a page from
/// <c>adrp</c>, an address from <c>adr</c> or from an <c>add</c> to one, or a value loaded from a known slot.
/// Anything else a register is given makes it unknown.
/// </summary>
internal sealed class Arm64Tracker
{
    private readonly ulong?[] _values = new ulong?[31];
    private readonly ulong?[] _slots = new ulong?[31];

    public void Reset()
    {
        Array.Clear(_values);
        Array.Clear(_slots);
    }

    /// <summary>The constant in a register before the next instruction, or null.</summary>
    public ulong? Value(Arm64Register register)
        => register.IsGeneral && register.Number < 31 ? _values[register.Number] : register.IsZero ? 0 : null;

    /// <summary>
    /// Takes one instruction: returns the address it reads, writes or takes when that is now known, and for an
    /// indirect branch or call, the slot its target was loaded from.
    /// </summary>
    public (ulong? Data, XrefKind Kind, ulong? Slot) Step(Arm64Instruction ins)
    {
        var ops = ins.Operands;
        string m = ins.Mnemonic;

        // Calls leave the argument and temporary registers with whatever the callee left in them.
        if (ins.Flow is InstructionFlow.Call or InstructionFlow.IndirectCall)
        {
            ulong? slot = ins.Flow == InstructionFlow.IndirectCall && ops is [Arm64RegisterOperand { Register: var target }, ..] ? SlotOf(target) : null;
            for (int r = 0; r <= 18; r++)
            {
                _values[r] = null;
                _slots[r] = null;
            }

            _values[30] = null;
            _slots[30] = null;
            return (null, XrefKind.Offset, slot);
        }

        if (ins.Flow == InstructionFlow.IndirectBranch && ops is [Arm64RegisterOperand { Register: var branchTarget }, ..])
        {
            return (null, XrefKind.Offset, SlotOf(branchTarget));
        }

        switch (m, ops)
        {
            case ("adrp", [Arm64RegisterOperand { Register: var d }, Arm64Address { Address: var page }]):
                Set(d, page);
                return (null, XrefKind.Offset, null);

            case ("adr", [Arm64RegisterOperand { Register: var d }, Arm64Address { Address: var address }]):
                Set(d, address);
                return (address, XrefKind.Offset, null);

            case ("add", [Arm64RegisterOperand { Register: var d }, Arm64RegisterOperand { Register: var n }, var amount])
                when Immediate(amount) is { } imm:
            {
                ulong? result = Value(n) is { } baseValue ? baseValue + imm : null;
                Set(d, result);
                return (result, XrefKind.Offset, null);
            }

            case ("mov", [Arm64RegisterOperand { Register: var d }, Arm64RegisterOperand { Register: var n }]) when n.IsGeneral:
                Set(d, Value(n), SlotOf(n));
                return (null, XrefKind.Offset, null);

            case ("mov", [Arm64RegisterOperand { Register: var d }, Arm64Immediate { Value: var value }]):
                Set(d, d.Is64Bit ? (ulong)value : (ulong)value & 0xFFFF_FFFF);
                return (null, XrefKind.Offset, null);
        }

        // A literal load names its address in the instruction.
        if (m is "ldr" or "ldrsw" or "prfm" && ops is [var destination, Arm64Address { Address: var literal }])
        {
            if (destination is Arm64RegisterOperand { Register: var loaded })
            {
                Set(loaded, null, loaded.Kind == Arm64RegisterKind.X ? literal : null);
            }

            return (literal, XrefKind.Read, null);
        }

        // A load or store through a base register holding an address, at an immediate offset.
        if (ops.Count > 0 && ops[^1] is Arm64Memory { Index: null, PostRegister: null } memory && m.Length > 2)
        {
            bool load = m.StartsWith("ld", StringComparison.Ordinal) || m.StartsWith("prf", StringComparison.Ordinal);
            bool store = m.StartsWith("st", StringComparison.Ordinal);
            ulong? address = memory.Indexing == Arm64Indexing.PostIndex
                ? Value(memory.Base)
                : Value(memory.Base) is { } b ? b + (ulong)memory.Offset : null;

            if (memory.Indexing != Arm64Indexing.Offset)
            {
                Set(memory.Base, null);
            }

            if (load)
            {
                // The registers a load fills: one, or two for a pair. A single 64-bit load remembers where it came from.
                bool pair = m.StartsWith("ldp", StringComparison.Ordinal) || m.StartsWith("ldxp", StringComparison.Ordinal) || m.StartsWith("ldaxp", StringComparison.Ordinal);
                if (ops[0] is Arm64RegisterOperand { Register: var first })
                {
                    Set(first, null, !pair && m == "ldr" && first.Kind == Arm64RegisterKind.X ? address : null);
                }

                if (pair && ops.Count > 2 && ops[1] is Arm64RegisterOperand { Register: var second })
                {
                    Set(second, null);
                }
            }
            else if (store && m.Contains('x', StringComparison.Ordinal) && ops[0] is Arm64RegisterOperand { Register: var status })
            {
                // stxr / stlxr write their status register.
                Set(status, null);
            }

            return address is { } a && (load || store) ? (a, load ? XrefKind.Read : XrefKind.Write, null) : (null, XrefKind.Offset, null);
        }

        // Anything else that writes its first operand makes that register unknown.
        if (ops.Count > 0 && ops[0] is Arm64RegisterOperand { Register: { IsGeneral: true } written } && WritesFirstOperand(m))
        {
            Set(written, null);
        }

        return (null, XrefKind.Offset, null);
    }

    private ulong? SlotOf(Arm64Register register) => register.IsGeneral && register.Number < 31 ? _slots[register.Number] : null;

    private void Set(Arm64Register register, ulong? value, ulong? slot = null)
    {
        if (!register.IsGeneral || register.Number >= 31)
        {
            return;
        }

        _values[register.Number] = value is { } v && !register.Is64Bit ? v & 0xFFFF_FFFF : value;
        _slots[register.Number] = slot;
    }

    private static ulong? Immediate(Arm64Operand operand) => operand switch
    {
        Arm64Immediate { Decimal: false, Value: var v } => (ulong)v,
        Arm64ShiftedImmediate { Kind: Arm64Shift.Lsl, Value: var v, Shift: var s } => (ulong)v << s,
        _ => null,
    };

    private static bool WritesFirstOperand(string mnemonic)
        => mnemonic is not ("cmp" or "cmn" or "tst" or "ccmp" or "ccmn" or "cbz" or "cbnz" or "tbz" or "tbnz" or "msr" or "sys")
           && !mnemonic.StartsWith("b", StringComparison.Ordinal);
}

/// <summary>
/// Recovers the targets behind an ARM64 <c>br</c> that reads a switch table. Compilers emit a table of offsets —
/// bytes, halfwords or words, scaled and added to a base (clang and gcc: <c>adr</c> of the first case, and a
/// shift of 2; or the table's own address) — or, rarely, of whole addresses:
/// <code>
///   cmp   w8, #n ; b.hi default
///   adrp  x9, table ; add x9, x9, :lo12:table
///   ldrb  w10, [x9, x8]              ; or ldrh, ldrsw [x9, x8, lsl #2]
///   adr   x11, base
///   add   x11, x11, x10, lsl #2      ; or sxtb #2, sxth #2 ...
///   br    x11
/// </code>
/// As on x86, a table that cannot be bounded and validated is left alone.
/// </summary>
internal static class Arm64JumpTables
{
    private const int UnboundedLimit = 512;
    private const int MaxEntries = 4096;

    public static JumpTable? TryRecover(IReadOnlyList<DecodedInstruction> trailing, ICodeSource source, Func<ulong, bool>? accept)
    {
        if (trailing.Count < 2 || trailing[^1] is not { Arm64: { Mnemonic: "br", Operands: [Arm64RegisterOperand { Register: var target }] } } jump)
        {
            return null;
        }

        int end = trailing.Count - 1;
        if (Definition(trailing, end, target) is not var (defIndex, def))
        {
            return null;
        }

        // Absolute: br of a 64-bit address read from the table.
        if (def is { Mnemonic: "ldr", Operands: [_, Arm64Memory { Index: { } absoluteIndex, Amount: 3 } absoluteMemory] })
        {
            if (ConstantBefore(trailing, defIndex, absoluteMemory.Base) is not { } absoluteTable)
            {
                return null;
            }

            return Build(trailing, jump, source, accept, absoluteTable, 0, JumpTableKind.Absolute, absoluteIndex, i =>
                source.Read(absoluteTable + ((ulong)i * 8), 8) is { Length: 8 } entry ? BinaryPrimitives.ReadUInt64LittleEndian(entry.Span) : null);
        }

        // Relative: base + entry, the entry extended and shifted.
        if (def is not { Mnemonic: "add", Operands: [_, Arm64RegisterOperand { Register: var baseRegister }, Arm64ExtendedRegister or Arm64ShiftedRegister] } add)
        {
            return null;
        }

        var (entryRegister, extend, shift) = add.Operands[2] switch
        {
            Arm64ExtendedRegister e => (e.Register, e.Extend, e.Amount),
            Arm64ShiftedRegister s when s.Shift == Arm64Shift.Lsl => (s.Register, Arm64Extend.Lsl, s.Amount),
            _ => (default(Arm64Register), Arm64Extend.Lsl, -1),
        };
        if (shift < 0 || ConstantBefore(trailing, defIndex, baseRegister) is not { } baseVa)
        {
            return null;
        }

        if (Definition(trailing, defIndex, entryRegister) is not var (loadIndex, load)
            || load.Operands is not [_, Arm64Memory { Index: { } index } memory]
            || ConstantBefore(trailing, loadIndex, memory.Base) is not { } table)
        {
            return null;
        }

        (int Size, bool Signed)? entryShape = load.Mnemonic switch
        {
            "ldrb" => (1, false),
            "ldrsb" => (1, true),
            "ldrh" => (2, false),
            "ldrsh" => (2, true),
            "ldr" when load.Operands[0] is Arm64RegisterOperand { Register.Kind: Arm64RegisterKind.W } => (4, false),
            "ldrsw" => (4, true),
            _ => null,
        };
        if (entryShape is not var (size, signed))
        {
            return null;
        }

        // The add's own extension decides the sign when the load did not: sxtb #2 on a byte.
        signed |= extend is Arm64Extend.Sxtb or Arm64Extend.Sxth or Arm64Extend.Sxtw;
        return Build(trailing, jump, source, accept, table, baseVa, JumpTableKind.RelativeToBase, index, i =>
        {
            var bytes = source.Read(table + ((ulong)i * (ulong)size), size);
            if (bytes.Length != size)
            {
                return null;
            }

            long value = size switch
            {
                1 => signed ? (sbyte)bytes.Span[0] : bytes.Span[0],
                2 => signed ? BinaryPrimitives.ReadInt16LittleEndian(bytes.Span) : BinaryPrimitives.ReadUInt16LittleEndian(bytes.Span),
                _ => signed ? BinaryPrimitives.ReadInt32LittleEndian(bytes.Span) : BinaryPrimitives.ReadUInt32LittleEndian(bytes.Span),
            };
            return (ulong)((long)baseVa + (value << shift));
        });
    }

    private static JumpTable? Build(
        IReadOnlyList<DecodedInstruction> trailing,
        DecodedInstruction jump,
        ICodeSource source,
        Func<ulong, bool>? accept,
        ulong table,
        ulong baseVa,
        JumpTableKind kind,
        Arm64Register index,
        Func<int, ulong?> entry)
    {
        int? bound = Bound(trailing);
        int limit = Math.Min(bound ?? UnboundedLimit, MaxEntries);
        var targets = new List<ulong>();
        for (int i = 0; i < limit; i++)
        {
            if (entry(i) is not { } target || !source.IsExecutable(target) || (target & 3) != 0
                || (bound is null && accept is not null && !accept(target)))
            {
                // A bounded table whose entries do not all land in code is not what it seemed.
                if (bound is not null)
                {
                    return null;
                }

                break;
            }

            targets.Add(target);
        }

        return targets.Count == 0 ? null : new JumpTable
        {
            JumpVa = jump.Va,
            TableVa = table,
            Kind = kind,
            BaseVa = baseVa,
            IndexRegister = "x" + index.Number.ToString(CultureInfo.InvariantCulture),
            IndexBits = 64,
            Targets = targets,
            CountFromBoundsCheck = bound is not null,
        };
    }

    /// <summary>
    /// The number of entries a range check allows: <c>cmp idx, #n</c> then <c>b.hi</c> (n + 1 entries) or
    /// <c>b.cs</c> (n), the last check before the jump.
    /// </summary>
    private static int? Bound(IReadOnlyList<DecodedInstruction> trailing)
    {
        for (int i = trailing.Count - 2; i >= 1; i--)
        {
            if (trailing[i].Arm64 is not { Mnemonic: "b.hi" or "b.cs" } branch)
            {
                continue;
            }

            // cmp idx, #n — or subs, which sets the same flags. Unoptimised code spills the index and reloads it
            // between the check and the load, so the register may differ; the entries are validated either way.
            var compare = trailing[i - 1].Arm64;
            long? limit = compare switch
            {
                { Mnemonic: "cmp", Operands: [Arm64RegisterOperand, Arm64Immediate { Value: var n }] } => n,
                { Mnemonic: "subs", Operands: [_, Arm64RegisterOperand, Arm64Immediate { Value: var n }] } => n,
                _ => null,
            };

            return limit is >= 0 and < MaxEntries ? (branch.Mnemonic == "b.hi" ? (int)limit + 1 : (int)limit) : null;
        }

        return null;
    }

    /// <summary>The last instruction before <paramref name="before"/> that writes <paramref name="register"/>.</summary>
    private static (int Index, Arm64Instruction Instruction)? Definition(IReadOnlyList<DecodedInstruction> trailing, int before, Arm64Register register)
    {
        for (int i = before - 1; i >= 0; i--)
        {
            if (trailing[i].Arm64 is { Operands: [Arm64RegisterOperand { Register: var written }, ..] } ins
                && written.IsGeneral && written.Number == register.Number && !ins.Mnemonic.StartsWith("st", StringComparison.Ordinal)
                && ins.Mnemonic is not ("cmp" or "cmn" or "tst" or "cbz" or "cbnz" or "tbz" or "tbnz"))
            {
                return (i, ins);
            }
        }

        return null;
    }

    /// <summary>What a register holds just before instruction <paramref name="before"/>, run from the start of the window.</summary>
    private static ulong? ConstantBefore(IReadOnlyList<DecodedInstruction> trailing, int before, Arm64Register register)
    {
        var tracker = new Arm64Tracker();
        for (int i = 0; i < before; i++)
        {
            if (trailing[i].Arm64 is { } ins)
            {
                tracker.Step(ins);
            }
        }

        return tracker.Value(register);
    }
}
