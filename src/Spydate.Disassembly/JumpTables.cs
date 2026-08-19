using Iced.Intel;

namespace Spydate.Disassembly;

/// <summary>How a table's entries name their targets.</summary>
public enum JumpTableKind
{
    /// <summary>Each entry is a code address (32-bit images).</summary>
    Absolute,
    /// <summary>
    /// Each entry is a signed 32-bit delta from a base the dispatch loaded - the image base on x64,
    /// which is why the entries look like RVAs.
    /// </summary>
    RelativeToBase,
}

/// <summary>A recovered switch dispatch: where the table is, and where its entries lead.</summary>
public sealed record JumpTable
{
    public required ulong JumpVa { get; init; }
    public required ulong TableVa { get; init; }
    public required JumpTableKind Kind { get; init; }
    /// <summary>Value each entry is added to; zero for <see cref="JumpTableKind.Absolute"/>.</summary>
    public ulong BaseVa { get; init; }
    /// <summary>Register holding the index, as the lifter names it, when it could be identified.</summary>
    public string? IndexRegister { get; init; }
    /// <summary>Width of <see cref="IndexRegister"/> in bits.</summary>
    public int IndexBits { get; init; }
    /// <summary>Targets in entry order; an index maps to the target at the same position.</summary>
    public required IReadOnlyList<ulong> Targets { get; init; }
    /// <summary>True when the entry count came from a bounds check rather than from validating entries.</summary>
    public required bool CountFromBoundsCheck { get; init; }

    public override string ToString() => $"table at 0x{TableVa:X} for jump at 0x{JumpVa:X} ({Targets.Count} entries)";
}

/// <summary>
/// Recovers the target list behind an indirect jump written by MSVC for a <c>switch</c>.
///
/// Two forms are recognised, and nothing else:
///
/// <code>
///   jmp dword ptr [idx*4 + table]              ; 32-bit: the entry is the address
///
///   lea  base, [rip+X]                         ; 64-bit: X is the image base
///   mov  t32, [base + idx*4 + tableRva]        ;         the entry is an RVA
///   add  t, base
///   jmp  t
/// </code>
///
/// Both are read only as far as the preceding bounds check allows, and every entry must land on
/// executable bytes: a table that cannot be bounded and validated is left alone, because inventing code
/// paths is worse than missing one.
/// </summary>
public static class JumpTables
{
    /// <summary>Entries to read when no bounds check is found and validation is the only limit.</summary>
    private const int UnboundedLimit = 512;

    /// <summary>Hard cap on any table, bounds check or not.</summary>
    private const int MaxEntries = 4096;

    /// <summary>
    /// Analyses the instructions leading up to an indirect jump. <paramref name="trailing"/> is in address
    /// order and ends with the jump itself.
    /// </summary>
    /// <param name="accept">
    /// Extra check a target must pass when the table has no bounds check - normally "inside the function
    /// being discovered", which stops an unbounded read from running into the next switch's table.
    /// </param>
    public static JumpTable? TryRecover(IReadOnlyList<DecodedInstruction> trailing, ICodeSource source, Func<ulong, bool>? accept = null)
    {
        ArgumentNullException.ThrowIfNull(trailing);
        ArgumentNullException.ThrowIfNull(source);

        if (trailing.Count == 0)
        {
            return null;
        }

        var jump = trailing[^1];
        if (jump.Flow != InstructionFlow.IndirectBranch)
        {
            return null;
        }

        return TryAbsoluteTable(trailing, jump, source, accept) ?? TryRelativeTable(trailing, jump, source, accept);
    }

    /// <summary><c>jmp dword ptr [reg*4 + table]</c>: the entry is the address.</summary>
    private static JumpTable? TryAbsoluteTable(IReadOnlyList<DecodedInstruction> trailing, DecodedInstruction jump, ICodeSource source, Func<ulong, bool>? accept)
    {
        var instr = jump.Native;
        if (instr.Op0Kind != OpKind.Memory
            || instr.MemoryBase != Register.None
            || instr.MemoryIndex == Register.None
            || instr.MemoryIndexScale != 4
            || instr.MemorySize.GetSize() != 4)
        {
            return null;
        }

        ulong tableVa = instr.MemoryDisplacement64;
        if (!source.IsMapped(tableVa))
        {
            return null;
        }

        int? bound = BoundsFrom(trailing, instr.MemoryIndex);
        var targets = ReadEntries(tableVa, 0, bound, source, JumpTableKind.Absolute, accept);
        return targets.Count == 0
            ? null
            : new JumpTable
            {
                JumpVa = jump.Va,
                TableVa = tableVa,
                Kind = JumpTableKind.Absolute,
                IndexRegister = RegisterName(instr.MemoryIndex),
                IndexBits = instr.MemoryIndex.GetSize() * 8,
                Targets = targets,
                CountFromBoundsCheck = bound is not null && targets.Count == bound,
            };
    }

    /// <summary><c>jmp reg</c> after <c>add reg, base</c>: the entry is a delta from the loaded base.</summary>
    private static JumpTable? TryRelativeTable(IReadOnlyList<DecodedInstruction> trailing, DecodedInstruction jump, ICodeSource source, Func<ulong, bool>? accept)
    {
        var instr = jump.Native;
        if (instr.Op0Kind != OpKind.Register)
        {
            return null;
        }

        var target = Canonical(instr.Op0Register);

        // add <target>, <base> - the base is added to the entry that was just loaded.
        var baseRegister = Register.None;
        int addAt = -1;
        for (int i = trailing.Count - 2; i >= 0 && addAt < 0; i--)
        {
            var candidate = trailing[i].Native;
            if (candidate.Mnemonic == Mnemonic.Add
                && candidate.Op0Kind == OpKind.Register && Canonical(candidate.Op0Register) == target
                && candidate.Op1Kind == OpKind.Register && Canonical(candidate.Op1Register) != target)
            {
                baseRegister = Canonical(candidate.Op1Register);
                addAt = i;
            }
            else if (Writes(candidate, target))
            {
                break; // the register was produced some other way
            }
        }

        if (addAt < 0)
        {
            return null;
        }

        // mov <target>d, [<base> + <index>*4 + tableRva] - the entry load names the table.
        ulong tableOffset = 0;
        var indexRegister = Register.None;
        int loadAt = -1;
        for (int i = addAt - 1; i >= 0 && loadAt < 0; i--)
        {
            var candidate = trailing[i].Native;
            if (candidate.Mnemonic is Mnemonic.Mov or Mnemonic.Movsxd
                && candidate.Op0Kind == OpKind.Register && Canonical(candidate.Op0Register) == target
                && candidate.Op1Kind == OpKind.Memory
                && candidate.MemoryIndexScale == 4
                && candidate.MemoryIndex != Register.None
                && Canonical(candidate.MemoryBase) == baseRegister)
            {
                tableOffset = candidate.MemoryDisplacement64;
                indexRegister = candidate.MemoryIndex;
                loadAt = i;
            }
        }

        if (loadAt < 0)
        {
            return null;
        }

        // lea <base>, [rip+X] - the value the entries are relative to.
        ulong baseVa = 0;
        for (int i = loadAt - 1; i >= 0; i--)
        {
            var candidate = trailing[i].Native;
            if (candidate.Mnemonic == Mnemonic.Lea
                && candidate.Op0Kind == OpKind.Register && Canonical(candidate.Op0Register) == baseRegister
                && candidate.IsIPRelativeMemoryOperand)
            {
                baseVa = candidate.IPRelativeMemoryAddress;
                break;
            }
        }

        ulong tableVa = baseVa + tableOffset;
        if (baseVa == 0 || !source.IsMapped(tableVa))
        {
            return null;
        }

        int? bound = BoundsFrom(trailing, indexRegister);
        var targets = ReadEntries(tableVa, baseVa, bound, source, JumpTableKind.RelativeToBase, accept);
        return targets.Count == 0
            ? null
            : new JumpTable
            {
                JumpVa = jump.Va,
                TableVa = tableVa,
                BaseVa = baseVa,
                Kind = JumpTableKind.RelativeToBase,
                IndexRegister = RegisterName(indexRegister),
                IndexBits = indexRegister.GetSize() * 8,
                Targets = targets,
                CountFromBoundsCheck = bound is not null && targets.Count == bound,
            };
    }

    /// <summary>
    /// The entry count a preceding <c>cmp idx, n</c> / <c>ja</c> pair allows. <c>ja</c> sends anything
    /// above <c>n</c> to the default label, so the table has <c>n + 1</c> entries; <c>jae</c> leaves
    /// <c>n</c>.
    /// </summary>
    private static int? BoundsFrom(IReadOnlyList<DecodedInstruction> trailing, Register index)
    {
        var wanted = Canonical(index);
        for (int i = trailing.Count - 2; i >= 0; i--)
        {
            var instr = trailing[i].Native;
            if (instr.Mnemonic != Mnemonic.Cmp || instr.Op0Kind != OpKind.Register || Canonical(instr.Op0Register) != wanted)
            {
                continue;
            }

            long limit = instr.Op1Kind switch
            {
                OpKind.Immediate8 or OpKind.Immediate8to16 or OpKind.Immediate8to32 or OpKind.Immediate8to64
                    or OpKind.Immediate16 or OpKind.Immediate32 or OpKind.Immediate32to64 or OpKind.Immediate64
                    => unchecked((long)instr.GetImmediate(1)),
                _ => -1,
            };

            if (limit < 0 || limit > MaxEntries || i + 1 >= trailing.Count)
            {
                return null;
            }

            // Only the branch immediately after the comparison is a bound: anything in between could
            // have set the flags itself.
            return trailing[i + 1].Native.ConditionCode switch
            {
                ConditionCode.a => (int)limit + 1,
                ConditionCode.ae => (int)limit,
                _ => null,
            };
        }

        return null;
    }

    private static List<ulong> ReadEntries(ulong tableVa, ulong baseVa, int? bound, ICodeSource source, JumpTableKind kind, Func<ulong, bool>? accept)
    {
        int limit = Math.Min(bound ?? UnboundedLimit, MaxEntries);
        var targets = new List<ulong>(Math.Min(limit, 64));

        for (int i = 0; i < limit; i++)
        {
            var bytes = source.Read(tableVa + (ulong)(i * 4), 4);
            if (bytes.Length < 4)
            {
                break;
            }

            uint raw = BitConverter.ToUInt32(bytes.Span);
            ulong target = kind == JumpTableKind.Absolute ? raw : baseVa + (ulong)(int)raw;
            if (!source.IsExecutable(target))
            {
                break; // the table has ended, or this was never a table
            }

            // With no range check to trust, an entry that leaves the function means the read has run
            // past the end of the table and into whatever follows it.
            if (bound is null && accept is not null && !accept(target))
            {
                break;
            }

            targets.Add(target);
        }

        return targets;
    }

    /// <summary>True when the instruction produces a new value for the register (comparisons do not).</summary>
    private static bool Writes(in Instruction instr, Register register)
        => instr.OpCount > 0
           && instr.Op0Kind == OpKind.Register
           && Canonical(instr.Op0Register) == register
           && instr.Mnemonic is not (Mnemonic.Cmp or Mnemonic.Test or Mnemonic.Push);

    /// <summary>Lower-case register name, matching how the lifter names registers.</summary>
    private static string? RegisterName(Register register)
        => register == Register.None ? null : register.ToString().ToLowerInvariant();

    /// <summary>Widest register in the family, so <c>eax</c> and <c>rax</c> compare equal.</summary>
    private static Register Canonical(Register register)
        => register == Register.None ? Register.None : register.GetFullRegister();
}
