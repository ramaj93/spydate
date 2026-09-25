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

    /// <summary>
    /// Returns true for call targets that never come back (<c>ExitProcess</c>, <c>__fastfail</c>,
    /// <c>abort</c>). Bytes after such a call are usually data or another function, not code.
    /// </summary>
    public Func<ulong, bool>? IsNoReturn { get; init; }

    /// <summary>
    /// When the function's extent is known (from the x64 unwind table), sweep the bytes the
    /// recursive descent never reached. That is where jump-table targets hide.
    /// </summary>
    public bool SweepUnreachedBytes { get; init; } = true;

    /// <summary>
    /// After following every seed and call, scan the leftover bytes of executable sections for
    /// function prologues. Finds leaf functions on x64 and most of an x86 image, which has no
    /// unwind table to seed from.
    /// </summary>
    public bool SweepGapsForFunctions { get; init; } = true;

    /// <summary>
    /// Recover the targets behind an indirect jump that reads a switch table, and follow them. Off,
    /// the path simply ends and the case bodies are left to the gap sweep.
    /// </summary>
    public bool FollowJumpTables { get; init; } = true;

    /// <summary>
    /// True when an address is where some other function begins — on x64, that it has an unwind
    /// record of its own.
    ///
    /// It is asked about the target of a direct <c>jmp</c>, which is the one branch that can leave a
    /// function altogether. A compiler emits one for a tail call, and it is indistinguishable from
    /// an ordinary jump by its bytes: the only thing that separates <c>jmp</c> to a label from
    /// <c>jmp</c> to a whole other function is whether something else already claims the target.
    /// The unwind table answers exactly that, and answers it before any of this runs, so the reply
    /// does not depend on what has been discovered yet.
    /// </summary>
    public Func<ulong, bool>? IsFunctionStart { get; init; }

    public static DiscoveryOptions Default { get; } = new();
}

/// <summary>
/// Recursive-descent discovery of a single function's basic blocks starting at an entry VA.
/// Direct branch targets are followed; calls record targets but are not followed;
/// indirect jumps, returns and invalid bytes terminate a path.
/// </summary>
public sealed class FunctionDiscovery
{
    private readonly ICodeSource _source;
    private readonly IInstructionDecoder _disassembler;
    private readonly SymbolTable _symbols;
    private readonly DiscoveryOptions _options;

    public FunctionDiscovery(ICodeSource source, IInstructionDecoder disassembler, SymbolTable symbols, DiscoveryOptions? options = null)
    {
        _source = source;
        _disassembler = disassembler;
        _symbols = symbols;
        _options = options ?? DiscoveryOptions.Default;
    }

    /// <summary>
    /// Discovers the function at <paramref name="entryVa"/>. Never throws for bad code; returns notes instead.
    /// <paramref name="boundsEnd"/> is the end address from the unwind table when it is known.
    /// </summary>
    public Function Discover(ulong entryVa, string? name = null, ulong? boundsEnd = null)
    {
        name ??= _symbols.TryGet(entryVa, out var sym) && sym.Kind != SymbolKind.Section ? sym.Name : $"sub_{entryVa:X}";

        var instructions = new SortedDictionary<ulong, DecodedInstruction>();
        var leaders = new HashSet<ulong> { entryVa };
        var callTargets = new List<ulong>();
        var indirectSlots = new List<ulong>();
        var jumpTables = new List<JumpTable>();
        // Physical predecessor of each decoded instruction, for reading back over a switch dispatch.
        var previousOf = new Dictionary<ulong, ulong>();
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
                previousOf[ins.NextVa] = ins.Va;

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
                            // A jump to somewhere another function begins is a tail call, and the
                            // function it lands in is not part of this one.
                            //
                            // Followed, it was: the two-instruction thunk that loads a field and
                            // jumps to a method came back as that method, seventy-odd blocks of it,
                            // under the thunk's name and with the thunk's address in the header. The
                            // body then read as one function whose addresses jumped to a region
                            // belonging to another - which is exactly how it was described when
                            // somebody hit it. The callee is discovered separately and says the same
                            // thing once, where it belongs.
                            if (t != entryVa && (_options.IsFunctionStart?.Invoke(t) ?? false))
                            {
                                if (!callTargets.Contains(t))
                                {
                                    callTargets.Add(t);
                                }

                                notes.Add($"Tail call at 0x{ins.Va:X} to 0x{t:X}, which is a function of its own.");
                            }
                            else
                            {
                                leaders.Add(t);
                                work.Push(t);
                            }
                        }

                        goto EndPath;

                    case InstructionFlow.ConditionalBranch:
                        // A conditional tail call — ARM64's cookie check branching to its failure routine — is
                        // the same as an unconditional one: the target is somebody else's function.
                        if (ins.BranchTargetVa is { } ct && ct != entryVa && (_options.IsFunctionStart?.Invoke(ct) ?? false))
                        {
                            if (!callTargets.Contains(ct))
                            {
                                callTargets.Add(ct);
                            }

                            notes.Add($"Conditional tail call at 0x{ins.Va:X} to 0x{ct:X}, which is a function of its own.");
                        }
                        else if (ins.BranchTargetVa is { } ct2)
                        {
                            leaders.Add(ct2);
                            work.Push(ct2);
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
                        else if (RecoverJumpTable(ins, previousOf, instructions, entryVa, boundsEnd) is { } table)
                        {
                            jumpTables.Add(table);
                            foreach (ulong caseTarget in table.Targets)
                            {
                                leaders.Add(caseTarget);
                                work.Push(caseTarget);
                            }

                            notes.Add($"Switch table at 0x{table.TableVa:X}: {table.Targets.Count} target(s) followed"
                                      + (table.CountFromBoundsCheck ? " (bounded by the range check)." : " (entry count inferred from the entries themselves)."));
                        }
                        else
                        {
                            notes.Add($"Indirect jump at 0x{ins.Va:X}; possible switch table not followed.");
                        }

                        goto EndPath;

                    case InstructionFlow.Return:
                    case InstructionFlow.Interrupt when _disassembler.NeverContinues(ins):
                        goto EndPath;

                    case InstructionFlow.Interrupt:
                        // syscall / int n: execution continues afterwards, but the block ends here.
                        leaders.Add(ins.NextVa);
                        work.Push(ins.NextVa);
                        goto EndPath;

                    case InstructionFlow.Call:
                        if (ins.BranchTargetVa is { } callTarget)
                        {
                            if (!callTargets.Contains(callTarget))
                            {
                                callTargets.Add(callTarget);
                            }

                            if (IsNoReturn(callTarget))
                            {
                                notes.Add($"Call at 0x{ins.Va:X} does not return; the bytes after it are not code.");
                                goto EndPath;
                            }
                        }

                        break;

                    case InstructionFlow.IndirectCall:
                        if (ins.IndirectSlotVa is { } cs)
                        {
                            if (!indirectSlots.Contains(cs))
                            {
                                indirectSlots.Add(cs);
                            }

                            if (IsNoReturn(cs))
                            {
                                notes.Add($"Call at 0x{ins.Va:X} does not return; the bytes after it are not code.");
                                goto EndPath;
                            }
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

        if (boundsEnd is { } end && _options.SweepUnreachedBytes)
        {
            SweepGaps(entryVa, end, instructions, leaders, notes);
        }

        var blocks = BuildBlocks(instructions, leaders, jumpTables, _disassembler);
        return new Function(entryVa, name, blocks, callTargets, indirectSlots, notes)
        {
            BoundsEnd = boundsEnd,
            JumpTables = jumpTables,
        };
    }

    /// <summary>
    /// Reads back over the instructions physically preceding an indirect jump and asks whether they are a
    /// switch dispatch. Only the linear run is considered: that is how the compiler emits the range check
    /// and the table load, and following branches backwards would mean guessing which path set the index.
    /// </summary>
    private JumpTable? RecoverJumpTable(DecodedInstruction jump, Dictionary<ulong, ulong> previousOf, SortedDictionary<ulong, DecodedInstruction> instructions, ulong entryVa, ulong? boundsEnd)
    {
        if (!_options.FollowJumpTables)
        {
            return null;
        }

        const int window = 16;
        var trailing = new List<DecodedInstruction>(window) { jump };
        ulong va = jump.Va;
        while (trailing.Count < window && previousOf.TryGetValue(va, out ulong previous) && instructions.TryGetValue(previous, out var earlier))
        {
            trailing.Add(earlier);
            va = previous;
        }

        trailing.Reverse();

        // Without a range check, the function's own extent is the only thing keeping an over-long read
        // from picking up the next switch's targets.
        Func<ulong, bool>? accept = boundsEnd is { } end ? target => target >= entryVa && target < end : null;
        return _disassembler.RecoverJumpTable(trailing, _source, accept);
    }

    private bool IsNoReturn(ulong va) => _options.IsNoReturn?.Invoke(va) ?? false;

    /// <summary>
    /// Decodes the bytes inside a function's known extent that the descent never reached — typically
    /// jump-table targets, which no direct branch points at. Alignment padding is skipped, and a gap
    /// is abandoned as soon as it stops decoding, since it may hold the table itself rather than code.
    /// </summary>
    private void SweepGaps(ulong entryVa, ulong boundsEnd, SortedDictionary<ulong, DecodedInstruction> instructions, HashSet<ulong> leaders, List<string> notes)
    {
        int budget = _options.MaxInstructionsPerFunction - instructions.Count;
        int recovered = 0;
        ulong cursor = entryVa;

        while (cursor < boundsEnd && budget > 0)
        {
            // Skip over what is already decoded.
            if (instructions.TryGetValue(cursor, out var known))
            {
                cursor = known.NextVa;
                continue;
            }

            // Bytes between functions and between blocks are padded; that is not missing code.
            if (IsPadding(cursor))
            {
                cursor += (ulong)_disassembler.InstructionAlignment;
                continue;
            }

            bool sweptAny = false;
            foreach (var ins in DecodeLinear(cursor))
            {
                if (ins.Va >= boundsEnd || instructions.ContainsKey(ins.Va) || ins.Flow == InstructionFlow.Invalid || --budget < 0)
                {
                    break;
                }

                if (!sweptAny)
                {
                    leaders.Add(ins.Va);
                    sweptAny = true;
                }

                instructions[ins.Va] = ins;
                recovered++;
                cursor = ins.NextVa;
            }

            if (!sweptAny)
            {
                // Not code (jump table, embedded data): step past it and keep looking.
                cursor += (ulong)_disassembler.InstructionAlignment;
            }
        }

        if (recovered > 0)
        {
            notes.Add($"Recovered {recovered} instruction(s) the recursive descent did not reach, using the unwind table bounds.");
        }
    }

    /// <summary>Filler the linker inserts between blocks.</summary>
    private bool IsPadding(ulong va) => _disassembler.IsPadding(_source.Read(va, _disassembler.InstructionAlignment).Span);

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
                if (fullChunk && ins.Va + (ulong)_disassembler.MaxInstructionLength > current + (ulong)chunk.Length && ins.Flow == InstructionFlow.Invalid)
                {
                    break;
                }

                progressed = true;
                yield return ins;
                if (ins.EndsBlock || ins.Flow == InstructionFlow.Invalid)
                {
                    yield break;
                }

                if (fullChunk && ins.NextVa + (ulong)_disassembler.MaxInstructionLength > current + (ulong)chunk.Length)
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

    private static List<BasicBlock> BuildBlocks(SortedDictionary<ulong, DecodedInstruction> instructions, HashSet<ulong> leaders, List<JumpTable> jumpTables, IInstructionDecoder decoder)
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
                case InstructionFlow.IndirectBranch:
                    // A recovered switch has as many successors as the table has entries.
                    foreach (var table in jumpTables.Where(t => t.JumpVa == last.Va))
                    {
                        foreach (ulong caseTarget in table.Targets)
                        {
                            if (byVa.ContainsKey(caseTarget))
                            {
                                b.AddSuccessor(caseTarget);
                            }
                        }
                    }

                    break;
                case InstructionFlow.Interrupt when !decoder.NeverContinues(last):
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
