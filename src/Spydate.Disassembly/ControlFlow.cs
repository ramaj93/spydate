namespace Spydate.Disassembly;

/// <summary>A straight-line sequence of instructions with a single entry and a single exit.</summary>
public sealed class BasicBlock
{
    private readonly List<ulong> _successors = new();
    private readonly List<ulong> _predecessors = new();

    public BasicBlock(ulong startVa, IReadOnlyList<DecodedInstruction> instructions)
    {
        StartVa = startVa;
        Instructions = instructions;
    }

    public ulong StartVa { get; }

    public IReadOnlyList<DecodedInstruction> Instructions { get; }

    public DecodedInstruction Last => Instructions[^1];

    /// <summary>VA of the first byte after the block.</summary>
    public ulong EndVa => Instructions.Count == 0 ? StartVa : Last.NextVa;

    public uint Size => (uint)(EndVa - StartVa);

    /// <summary>VAs of blocks control may flow to (fallthrough first, then branch target).</summary>
    public IReadOnlyList<ulong> Successors => _successors;

    public IReadOnlyList<ulong> Predecessors => _predecessors;

    internal void AddSuccessor(ulong va)
    {
        if (!_successors.Contains(va))
        {
            _successors.Add(va);
        }
    }

    internal void AddPredecessor(ulong va)
    {
        if (!_predecessors.Contains(va))
        {
            _predecessors.Add(va);
        }
    }

    public override string ToString() => $"block 0x{StartVa:X}-0x{EndVa:X} ({Instructions.Count} insns)";
}

/// <summary>Why function discovery stopped exploring a path.</summary>
public enum DiscoveryNote
{
    None,
    IndirectJump,
    UnmappedTarget,
    InvalidInstruction,
    InstructionLimit,
    NonExecutableTarget,
}

/// <summary>A discovered native function: an entry point plus its basic blocks in address order.</summary>
public sealed class Function
{
    public Function(ulong entryVa, string name, IReadOnlyList<BasicBlock> blocks, IReadOnlyList<ulong> callTargets, IReadOnlyList<ulong> indirectCallSlots, IReadOnlyList<string> notes)
    {
        EntryVa = entryVa;
        Name = name;
        Blocks = blocks;
        CallTargets = callTargets;
        IndirectCallSlots = indirectCallSlots;
        Notes = notes;
        BlockByVa = blocks.ToDictionary(b => b.StartVa);
    }

    public ulong EntryVa { get; }

    public string Name { get; }

    /// <summary>Blocks sorted by start VA.</summary>
    public IReadOnlyList<BasicBlock> Blocks { get; }

    public IReadOnlyDictionary<ulong, BasicBlock> BlockByVa { get; }

    /// <summary>Direct call targets (VAs) made from this function.</summary>
    public IReadOnlyList<ulong> CallTargets { get; }

    /// <summary>Memory slots (usually IAT entries) used by indirect calls.</summary>
    public IReadOnlyList<ulong> IndirectCallSlots { get; }

    /// <summary>Human-readable analysis notes (paths that could not be followed etc.).</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>
    /// End address from the x64 unwind table when the image declares one. Authoritative, unlike
    /// <see cref="EndVa"/>, which only covers what discovery actually decoded.
    /// </summary>
    public ulong? BoundsEnd { get; init; }

    /// <summary>Declared size from the unwind table, or the discovered span when there is none.</summary>
    public ulong DeclaredSize => BoundsEnd is { } end && end > EntryVa ? end - EntryVa : EndVa - EntryVa;

    /// <summary>True when decoding ran past the end the unwind table declares — a bad sign.</summary>
    public bool ExtendsBeyondBounds => BoundsEnd is { } end && EndVa > end;

    public IEnumerable<DecodedInstruction> Instructions => Blocks.SelectMany(b => b.Instructions);

    public int InstructionCount => Blocks.Sum(b => b.Instructions.Count);

    /// <summary>Highest end address among the blocks (functions may have gaps).</summary>
    public ulong EndVa => Blocks.Count == 0 ? EntryVa : Blocks.Max(b => b.EndVa);

    /// <summary>Total number of code bytes in all blocks.</summary>
    public uint CodeSize => (uint)Blocks.Sum(b => (long)b.Size);

    public override string ToString() => $"{Name} @ 0x{EntryVa:X} ({Blocks.Count} blocks, {InstructionCount} insns)";
}
