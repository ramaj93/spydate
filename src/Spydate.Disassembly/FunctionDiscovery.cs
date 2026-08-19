using Spydate.Core.Symbols;

namespace Spydate.Disassembly;

/// <summary>Tunables for <see cref="FunctionDiscovery"/>.</summary>
public sealed record DiscoveryOptions
{
    /// <summary>Upper bound on instructions decoded for one function (guards against runaway sweeps of data).</summary>
    public int MaxInstructionsPerFunction { get; init; } = 50_000;

    /// <summary>Bytes fetched per read from the code source.</summary>
    public int ChunkSize { get; init; } = 4096;

    /// <summary>Whether to follow direct branches into non-executable sections (usually a sign of bad decoding).</summary>
    public bool FollowIntoNonExecutable { get; init; }

    public static DiscoveryOptions Default { get; } = new();
}

/// <summary>
/// Recursive-descent discovery of a single function's basic blocks starting at an entry VA.
/// Direct branch targets are followed; calls record targets but are not followed;
/// indirect jumps, returns and invalid bytes terminate a path.
/// </summary>
public sealed class FunctionDiscovery
{
    private const int MaxInstructionLength = 15;

    private readonly ICodeSource _source;
    private readonly X86Disassembler _disassembler;
    private readonly SymbolTable _symbols;
    private readonly DiscoveryOptions _options;

    public FunctionDiscovery(ICodeSource source, X86Disassembler disassembler, SymbolTable symbols, DiscoveryOptions? options = null)
    {
        _source = source;
        _disassembler = disassembler;
        _symbols = symbols;
        _options = options ?? DiscoveryOptions.Default;
    }

    /// <summary>Discovers the function at <paramref name="entryVa"/>. Never throws for bad code; returns notes instead.</summary>
    public Function Discover(ulong entryVa, string? name = null)
    {
        name ??= _symbols.TryGet(entryVa, out var sym) && sym.Kind != SymbolKind.Section ? sym.Name : $"sub_{entryVa:X}";

        var instructions = new SortedDictionary<ulong, DecodedInstruction>();
        var leaders = new HashSet<ulong> { entryVa };
        var callTargets = new List<ulong>();
        var indirectSlots = new List<ulong>();
        var notes = new List<string>();
        var work = new Stack<ulong>();
        work.Push(entryVa);
        int budget = _options.MaxInstructionsPerFunction;

        while (work.Count > 0)
        {
            ulong va = work.Pop();
            if (instructions.ContainsKey(va))
            {
                continue; // already decoded from here
            }

            if (!_source.IsMapped(va))
            {
                notes.Add($"Target 0x{va:X} is outside the image.");
                continue;
            }

            if (!_options.FollowIntoNonExecutable && !_source.IsExecutable(va))
            {
                notes.Add($"Target 0x{va:X} is not in an executable section.");
                continue;
            }

            foreach (var ins in DecodeLinear(va))
            {
                if (instructions.ContainsKey(ins.Va))
                {
                    break; // merged into previously decoded code
                }

                if (--budget < 0)
                {
                    notes.Add($"Instruction limit ({_options.MaxInstructionsPerFunction}) reached; function may be truncated.");
                    work.Clear();
                    break;
                }

                instructions[ins.Va] = ins;

                if (ins.Flow == InstructionFlow.Invalid)
                {
                    notes.Add($"Invalid instruction at 0x{ins.Va:X}; path abandoned.");
                    break;
                }

                switch (ins.Flow)
                {
                    case InstructionFlow.UnconditionalBranch:
                        if (ins.BranchTargetVa is { } t)
                        {
                            leaders.Add(t);
                            work.Push(t);
                        }

                        goto EndPath;

                    case InstructionFlow.ConditionalBranch:
                        if (ins.BranchTargetVa is { } ct)
                        {
                            leaders.Add(ct);
                            work.Push(ct);
                        }

                        leaders.Add(ins.NextVa);
                        work.Push(ins.NextVa);
                        goto EndPath;

                    case InstructionFlow.IndirectBranch:
                        if (ins.IndirectSlotVa is { } slot)
                        {
                            indirectSlots.Add(slot);
                            // jmp [iat] is a tail-call thunk – path ends here.
                        }
                        else
                        {
                            notes.Add($"Indirect jump at 0x{ins.Va:X}; possible switch table not followed.");
                        }

                        goto EndPath;

                    case InstructionFlow.Return:
                    case InstructionFlow.Interrupt when IsTerminatingInterrupt(ins):
                        goto EndPath;

                    case InstructionFlow.Interrupt:
                        // syscall / int n: execution continues afterwards, but the block ends here.
                        leaders.Add(ins.NextVa);
                        work.Push(ins.NextVa);
                        goto EndPath;

                    case InstructionFlow.Call:
                        if (ins.BranchTargetVa is { } callTarget && !callTargets.Contains(callTarget))
                        {
                            callTargets.Add(callTarget);
                        }

                        break;

                    case InstructionFlow.IndirectCall:
                        if (ins.IndirectSlotVa is { } cs && !indirectSlots.Contains(cs))
                        {
                            indirectSlots.Add(cs);
                        }

                        break;
                }

                // Fallthrough into an existing leader ends the linear run (block boundary), but the
                // next instruction is already or will be decoded from the worklist.
                if (leaders.Contains(ins.NextVa) && instructions.ContainsKey(ins.NextVa))
                {
                    break;
                }
            }

        EndPath:;
        }

        var blocks = BuildBlocks(instructions, leaders);
        return new Function(entryVa, name, blocks, callTargets, indirectSlots, notes);
    }

    private static bool IsTerminatingInterrupt(DecodedInstruction ins)
        => ins.Mnemonic is "int3" or "ud2" or "hlt";

    /// <summary>Decodes linearly from <paramref name="va"/>, refetching chunks so instructions never straddle a chunk end.</summary>
    private IEnumerable<DecodedInstruction> DecodeLinear(ulong va)
    {
        ulong current = va;
        while (true)
        {
            var chunk = _source.Read(current, _options.ChunkSize);
            if (chunk.IsEmpty)
            {
                yield break;
            }

            bool fullChunk = chunk.Length == _options.ChunkSize;
            bool progressed = false;
            foreach (var ins in _disassembler.DecodeLazy(chunk, current, _source.ImageBase))
            {
                // Near the end of a full chunk the decoder may see a truncated instruction; refetch instead.
                if (fullChunk && ins.Va + MaxInstructionLength > current + (ulong)chunk.Length && ins.Flow == InstructionFlow.Invalid)
                {
                    break;
                }

                progressed = true;
                yield return ins;
                if (ins.EndsBlock || ins.Flow == InstructionFlow.Invalid)
                {
                    yield break;
                }

                if (fullChunk && ins.NextVa + MaxInstructionLength > current + (ulong)chunk.Length)
                {
                    current = ins.NextVa;
                    goto Refetch;
                }
            }

            if (!progressed || !fullChunk)
            {
                yield break;
            }

        Refetch:;
        }
    }

    private static List<BasicBlock> BuildBlocks(SortedDictionary<ulong, DecodedInstruction> instructions, HashSet<ulong> leaders)
    {
        var blocks = new List<BasicBlock>();
        var current = new List<DecodedInstruction>();
        ulong currentStart = 0;
        DecodedInstruction? previous = null;

        foreach (var (va, ins) in instructions)
        {
            bool startsBlock = current.Count == 0 || leaders.Contains(va) || (previous is not null && previous.NextVa != va);
            if (startsBlock && current.Count > 0)
            {
                blocks.Add(new BasicBlock(currentStart, current.ToArray()));
                current.Clear();
            }

            if (current.Count == 0)
            {
                currentStart = va;
            }

            current.Add(ins);
            previous = ins;
            if (ins.EndsBlock)
            {
                blocks.Add(new BasicBlock(currentStart, current.ToArray()));
                current.Clear();
                previous = null;
            }
        }

        if (current.Count > 0)
        {
            blocks.Add(new BasicBlock(currentStart, current.ToArray()));
        }

        // Wire successors / predecessors.
        var byVa = blocks.ToDictionary(b => b.StartVa);
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            var last = b.Last;
            switch (last.Flow)
            {
                case InstructionFlow.Next:
                case InstructionFlow.Call:
                case InstructionFlow.IndirectCall:
                    if (byVa.ContainsKey(last.NextVa))
                    {
                        b.AddSuccessor(last.NextVa);
                    }

                    break;
                case InstructionFlow.ConditionalBranch:
                    if (byVa.ContainsKey(last.NextVa))
                    {
                        b.AddSuccessor(last.NextVa);
                    }

                    if (last.BranchTargetVa is { } t && byVa.ContainsKey(t))
                    {
                        b.AddSuccessor(t);
                    }

                    break;
                case InstructionFlow.UnconditionalBranch:
                    if (last.BranchTargetVa is { } ut && byVa.ContainsKey(ut))
                    {
                        b.AddSuccessor(ut);
                    }

                    break;
                case InstructionFlow.Interrupt when !IsTerminatingInterrupt(last):
                    if (byVa.ContainsKey(last.NextVa))
                    {
                        b.AddSuccessor(last.NextVa);
                    }

                    break;
            }
        }

        foreach (var b in blocks)
        {
            foreach (var s in b.Successors)
            {
                byVa[s].AddPredecessor(b.StartVa);
            }
        }

        return blocks;
    }
}
