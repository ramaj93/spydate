using System.Collections;
using System.Collections.Concurrent;
using Spydate.Core.PE;
using Spydate.Core.Pdb;
using Spydate.Core.Strings;
using Spydate.Core.Symbols;

namespace Spydate.Disassembly;

/// <summary>Progress report emitted during whole-image function discovery.</summary>
public readonly record struct AnalysisProgress(int FunctionsFound, int Pending, string Message);

/// <summary>
/// Analysis session for one native <see cref="PeImage"/>: owns the disassembler, the symbol table
/// and the cache of discovered functions. Safe for concurrent readers.
/// </summary>
public sealed class BinaryAnalysis
{
    /// <summary>How many times the gap sweep re-runs; each round can expose calls into new gaps.</summary>
    private const int MaxSweepRounds = 3;

    /// <summary>
    /// Instruction budget for a swept candidate. Decoding data produces plausible-looking x86 that
    /// branches all over the section, so an unbounded probe spends most of its time proving that
    /// bytes are not code. A candidate that needs more than this is rejected rather than truncated.
    /// </summary>
    private const int CandidateInstructionBudget = 2000;

    private readonly ConcurrentDictionary<ulong, Function> _functions = new();
    private readonly FunctionDiscovery _discovery;
    /// <summary>Discovery for swept candidates, with a tight budget: see TryDiscoverCandidate.</summary>
    private readonly FunctionDiscovery _candidateDiscovery;
    /// <summary>Addresses already proven not to be functions, so later sweep rounds skip them.</summary>
    private readonly HashSet<ulong> _rejectedCandidates = new();
    private readonly XrefExtractor _xrefExtractor;
    private readonly Lazy<StringIndex> _strings;
    private readonly DiscoveryOptions _options;

    /// <summary>
    /// Functions the CRT and the Win32 API never return from. A call to one of these ends a code
    /// path: the bytes after it are padding, data, or the next function - decoding them produces
    /// garbage instructions and bogus cross-references.
    /// </summary>
    private static readonly HashSet<string> NoReturnNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ExitProcess", "ExitThread", "TerminateProcess", "TerminateThread",
        "RtlExitUserProcess", "RtlExitUserThread", "NtTerminateProcess", "ZwTerminateProcess",
        "RtlRaiseStatus", "RaiseFailFastException", "RtlFailFast", "__fastfail",
        "FatalExit", "FatalAppExitA", "FatalAppExitW", "CorExitProcess",
        "abort", "exit", "_exit", "_Exit", "quick_exit", "_invoke_watson",
        "_invalid_parameter_noinfo_noreturn", "_CxxThrowException", "longjmp", "_longjmp",
    };

    /// <summary>Addresses (IAT slots and function entries) known never to return.</summary>
    private readonly HashSet<ulong> _noReturn = new();
    private readonly Lock _noReturnGate = new();

    /// <summary>Function extents declared by the x64 unwind table, keyed by start VA.</summary>
    private readonly Dictionary<ulong, ulong> _bounds = new();

    public BinaryAnalysis(PeImage image, AsmSyntax syntax = AsmSyntax.Intel, DiscoveryOptions? options = null)
    {
        Image = image;
        Symbols = SymbolTable.FromImage(image);
        Source = new PeCodeSource(image);
        Disassembler = new X86Disassembler(image.Bitness, Symbols, syntax);
        options ??= DiscoveryOptions.Default;
        _options = options;
        var discoveryOptions = options.IsNoReturn is null ? options with { IsNoReturn = IsNoReturn } : options;
        _discovery = new FunctionDiscovery(Source, Disassembler, Symbols, discoveryOptions);
        _candidateDiscovery = new FunctionDiscovery(Source, Disassembler, Symbols, discoveryOptions with { MaxInstructionsPerFunction = CandidateInstructionBudget });
        _xrefExtractor = new XrefExtractor(Source);
        // Scanning touches every byte of the file, so it waits until something asks for a string.
        _strings = new Lazy<StringIndex>(
            () => StringIndex.Build(StringScanner.Scan(image)),
            LazyThreadSafetyMode.ExecutionAndPublication);

        foreach (var symbol in Symbols.All)
        {
            // Import thunks are named "kernel32!ExitProcess"; exports are bare.
            string bare = symbol.Name.Contains('!') ? symbol.Name[(symbol.Name.LastIndexOf('!') + 1)..] : symbol.Name;
            if (NoReturnNames.Contains(bare))
            {
                _noReturn.Add(symbol.Va);
            }
        }

        CrtHelpers.ApplyLoadConfigSymbols(image, Symbols);

        foreach (var rf in image.ExceptionTable)
        {
            if (!rf.IsChained && rf.BeginRva != 0 && rf.EndRva > rf.BeginRva)
            {
                _bounds[image.RvaToVa(rf.BeginRva)] = image.RvaToVa(rf.EndRva);
            }
        }

    }

    public PeImage Image { get; }

    public SymbolTable Symbols { get; }

    public ICodeSource Source { get; }

    public X86Disassembler Disassembler { get; }

    /// <summary>Result of looking for the image's PDB, once <see cref="LoadPdbSymbols"/> has run.</summary>
    public PdbLoadResult? Pdb { get; private set; }

    /// <summary>
    /// Finds and applies the matching PDB. Not done in the constructor: it touches the file system
    /// and the caller decides whether that is wanted (and on which thread).
    /// </summary>
    public PdbLoadResult LoadPdbSymbols()
    {
        var result = PdbSymbols.TryLoadFor(Image, Symbols);
        Pdb = result;
        return result;
    }

    /// <summary>Cross-references collected from every function discovered so far.</summary>
    public XrefTable Xrefs { get; } = new();

    /// <summary>
    /// Strings found in the image, indexed by address. Built on first use — call it off the UI
    /// thread, since the scan reads the whole file.
    /// </summary>
    public StringIndex Strings => _strings.Value;

    /// <summary>The string literal covering <paramref name="va"/>, or null.</summary>
    public FoundString? StringAt(ulong va) => Strings.Find(va);

    /// <summary>Whether the image's machine type is supported by the x86 disassembler.</summary>
    public bool CanDisassemble => Image.IsX86Family;

    /// <summary>Functions discovered so far, sorted by entry VA.</summary>
    public IReadOnlyList<Function> Functions => _functions.Values.OrderBy(f => f.EntryVa).ToList();

    public int FunctionCount => _functions.Count;

    public bool TryGetFunction(ulong va, out Function function) => _functions.TryGetValue(va, out function!);

    /// <summary>Returns the cached function at <paramref name="entryVa"/> or discovers it now.</summary>
    public Function GetOrDiscoverFunction(ulong entryVa, string? name = null)
    {
        return _functions.GetOrAdd(entryVa, va =>
        {
            var f = NameHelpers(_discovery.Discover(va, name, BoundsFor(va)), name);
            Symbols.Add(new Symbol(va, f.Name, SymbolKind.Function, f.CodeSize));
            RecordIfNoReturnThunk(f);
            // The extractor is not thread-safe, but GetOrAdd's factory may run concurrently.
            lock (_xrefExtractor)
            {
                _xrefExtractor.Extract(f, Xrefs);
            }

            return f;
        });
    }

    /// <summary>
    /// Seed VAs for whole-image discovery, in descending order of confidence: entry point, TLS
    /// callbacks (they run before it), executable exports, non-chained RUNTIME_FUNCTION starts from
    /// the x64 exception directory, and the Control Flow Guard / SafeSEH tables — every address the
    /// image itself declares as a legal indirect-call target.
    /// </summary>
    public IReadOnlyList<(ulong Va, string? Name)> GetSeeds()
    {
        var seeds = new List<(ulong, string?)>();
        var seen = new HashSet<ulong>();

        void Add(ulong va, string? name)
        {
            if (va != 0 && seen.Add(va) && Source.IsExecutable(va))
            {
                seeds.Add((va, name));
            }
        }

        if (Image.EntryPointRva != 0)
        {
            Add(Image.EntryPointVa, Image.IsDll ? "DllEntryPoint" : "EntryPoint");
        }

        if (Image.Tls is { } tls)
        {
            for (int i = 0; i < tls.CallbackVas.Count; i++)
            {
                Add(tls.CallbackVas[i], $"TlsCallback{i}");
            }
        }

        if (Image.Exports is { } exports)
        {
            foreach (var e in exports.Entries)
            {
                if (!e.IsForwarder && e.Rva != 0)
                {
                    Add(Image.RvaToVa(e.Rva), e.Name ?? $"Ordinal{e.Ordinal}");
                }
            }
        }

        foreach (var rf in Image.ExceptionTable)
        {
            if (!rf.IsChained && rf.BeginRva != 0)
            {
                Add(Image.RvaToVa(rf.BeginRva), null);
            }
        }

        // Symbols that name a function - from a PDB, when one was loaded - are exact starts.
        foreach (var symbol in Symbols.All.Where(s => s.Kind == SymbolKind.Function).ToList())
        {
            Add(symbol.Va, symbol.Name);
        }

        if (Image.LoadConfig is { } config)
        {
            foreach (uint rva in config.GuardCfFunctionRvas)
            {
                Add(Image.RvaToVa(rva), null);
            }

            for (int i = 0; i < config.SeHandlerRvas.Count; i++)
            {
                Add(Image.RvaToVa(config.SeHandlerRvas[i]), $"SehHandler{i}");
            }
        }

        return seeds;
    }

    /// <summary>
    /// Discovers functions from the seeds and transitively through direct call targets.
    /// Bounded by <paramref name="maxFunctions"/>. Reports progress periodically.
    /// </summary>
    public IReadOnlyList<Function> DiscoverAll(int maxFunctions = 20_000, IProgress<AnalysisProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var queue = new Queue<(ulong Va, string? Name)>();
        var queued = new HashSet<ulong>();
        foreach (var (va, name) in GetSeeds())
        {
            if (queued.Add(va))
            {
                queue.Enqueue((va, name));
            }
        }

        progress?.Report(new AnalysisProgress(0, queue.Count, $"{queue.Count} seeds"));
        Drain();

        // Everything reachable from a seed is now known. What is left in executable memory is
        // either data or a function nothing points at directly: leaf functions on x64, and most of
        // an x86 image, which has no unwind table to seed from.
        if (_options.SweepGapsForFunctions)
        {
            for (int round = 0; round < MaxSweepRounds && _functions.Count < maxFunctions; round++)
            {
                int found = SweepGaps(maxFunctions, queue, queued, progress, cancellationToken);
                if (found == 0)
                {
                    break;
                }

                progress?.Report(new AnalysisProgress(_functions.Count, queue.Count, $"gap sweep found {found}"));
                Drain();
            }
        }

        progress?.Report(new AnalysisProgress(_functions.Count, queue.Count, "Discovery complete"));
        return Functions;

        void Drain()
        {
            int processed = 0;
            while (queue.Count > 0 && _functions.Count < maxFunctions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (va, name) = queue.Dequeue();
                var f = GetOrDiscoverFunction(va, name);
                Enqueue(f);

                if (++processed % 64 == 0)
                {
                    progress?.Report(new AnalysisProgress(_functions.Count, queue.Count, $"Analyzing {f.Name}"));
                }
            }
        }

        void Enqueue(Function f)
        {
            foreach (ulong target in f.CallTargets)
            {
                if (queued.Add(target) && Source.IsExecutable(target))
                {
                    queue.Enqueue((target, null));
                }
            }
        }
    }

    /// <summary>
    /// Scans the uncovered bytes of every executable section for function prologues. Padding is
    /// skipped and candidates that do not decode into a plausible function are discarded rather
    /// than cached, because a false positive attributes real code to the wrong function.
    /// </summary>
    private int SweepGaps(int maxFunctions, Queue<(ulong Va, string? Name)> queue, HashSet<ulong> queued, IProgress<AnalysisProgress>? progress, CancellationToken cancellationToken)
    {
        int found = 0;
        int probed = 0;

        foreach (var section in Image.Sections)
        {
            if (!section.IsExecutable || section.SizeOfRawData == 0)
            {
                continue;
            }

            uint size = section.VirtualSize == 0 ? section.SizeOfRawData : Math.Min(section.VirtualSize, section.SizeOfRawData);
            ulong sectionStart = Image.RvaToVa(section.VirtualAddress);
            var covered = BuildCoverage(sectionStart, size);

            // Read the section once: probing through the code source per byte would repeat a
            // section lookup for every candidate offset and dominate the runtime.
            var body = Image.ReadAtRva(section.VirtualAddress, (int)size);
            if (body.IsEmpty)
            {
                continue;
            }

            var span = body.Span;
            bool atLimit = false;
            for (int offset = 0; offset < span.Length && !atLimit; offset++)
            {
                // ConcurrentDictionary.Count takes every lock, so it is checked periodically rather
                // than per byte - the loop runs once for each byte of every executable section.
                if ((offset & 0xFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    atLimit = _functions.Count >= maxFunctions;
                }

                if (covered[offset] || FunctionPrologues.IsPadding(span[offset]))
                {
                    continue;
                }

                int window = Math.Min(16, span.Length - offset);
                if (!FunctionPrologues.LooksLikeFunctionStart(span.Slice(offset, window), Image.Bitness))
                {
                    continue;
                }

                ulong va = sectionStart + (ulong)offset;
                if (_rejectedCandidates.Contains(va))
                {
                    continue;
                }

                probed++;
                if (TryDiscoverCandidate(va) is not { } function)
                {
                    continue;
                }

                found++;
                Mark(covered, sectionStart, size, function);
                foreach (ulong target in function.CallTargets)
                {
                    if (queued.Add(target) && Source.IsExecutable(target))
                    {
                        queue.Enqueue((target, null));
                    }
                }

                offset = (int)Math.Max((long)offset, (long)(function.EndVa - sectionStart) - 1);
            }
        }

        progress?.Report(new AnalysisProgress(
            _functions.Count,
            queue.Count,
            $"gap sweep: {probed} candidates, {found} accepted"));
        return found;
    }

    /// <summary>Bitmap of the section's bytes that already belong to a discovered function.</summary>
    private BitArray BuildCoverage(ulong sectionStart, uint size)
    {
        var covered = new BitArray((int)size);
        foreach (var f in _functions.Values)
        {
            Mark(covered, sectionStart, size, f);
        }

        return covered;
    }

    private static void Mark(BitArray covered, ulong sectionStart, uint size, Function function)
    {
        foreach (var block in function.Blocks)
        {
            if (block.EndVa <= sectionStart || block.StartVa >= sectionStart + size)
            {
                continue;
            }

            int from = (int)Math.Max(0, (long)(block.StartVa - sectionStart));
            int to = (int)Math.Min(size, block.EndVa - sectionStart);
            for (int i = from; i < to; i++)
            {
                covered[i] = true;
            }
        }
    }

    /// <summary>Discovers a swept candidate, caching it only if it decodes into something believable.</summary>
    private Function? TryDiscoverCandidate(ulong va)
    {
        if (_functions.TryGetValue(va, out var existing))
        {
            return existing;
        }

        var candidate = _candidateDiscovery.Discover(va, null, BoundsFor(va));
        if (candidate.InstructionCount >= CandidateInstructionBudget || !IsPlausibleFunction(candidate))
        {
            _rejectedCandidates.Add(va);
            return null;
        }

        candidate = NameHelpers(candidate, null);

        if (_functions.TryAdd(va, candidate))
        {
            Symbols.Add(new Symbol(va, candidate.Name, SymbolKind.Function, candidate.CodeSize));
            RecordIfNoReturnThunk(candidate);
        }

        return _functions[va];
    }

    /// <summary>
    /// Replaces an auto-generated name with the CRT helper the body identifies, when it does. A
    /// caller-supplied name (an export, a TLS callback) always wins: it came from the image itself.
    /// </summary>
    private Function NameHelpers(Function function, string? requestedName)
    {
        if (requestedName is not null || Symbols.TryGet(function.EntryVa, out var known) && known.Kind != SymbolKind.Section)
        {
            return function;
        }

        return CrtHelpers.Identify(function, Image) is { } helper ? function.WithName(helper) : function;
    }

    /// <summary>
    /// A swept candidate has to look like real code: no invalid instructions, and either a proper
    /// terminator or enough instructions that a chance byte sequence is unlikely. A lone jump is
    /// accepted because import thunks are exactly that.
    /// </summary>
    private static bool IsPlausibleFunction(Function candidate)
    {
        if (candidate.InstructionCount == 0)
        {
            return false;
        }

        if (candidate.Instructions.Any(i => i.Flow == InstructionFlow.Invalid))
        {
            return false;
        }

        if (candidate.InstructionCount == 1)
        {
            return candidate.Blocks[0].Last.Flow is InstructionFlow.UnconditionalBranch or InstructionFlow.IndirectBranch;
        }

        return candidate.Blocks.Any(b => b.Last.Flow is InstructionFlow.Return or InstructionFlow.IndirectBranch or InstructionFlow.UnconditionalBranch)
               || candidate.InstructionCount >= 4;
    }

    /// <summary>Linear disassembly of up to <paramref name="byteCount"/> bytes starting at <paramref name="va"/>.</summary>
    public IReadOnlyList<DecodedInstruction> DisassembleRange(ulong va, int byteCount, int maxInstructions = int.MaxValue)
    {
        var bytes = Source.Read(va, byteCount);
        return Disassembler.Decode(bytes, va, Image.ImageBase, maxInstructions);
    }

/// <summary>End address declared by the unwind table for the function starting at <paramref name="va"/>.</summary>
    public ulong? BoundsFor(ulong va)
    {
        lock (_noReturnGate)
        {
            return _bounds.TryGetValue(va, out ulong end) ? end : null;
        }
    }

    /// <summary>True when a call to <paramref name="va"/> never comes back.</summary>
    public bool IsNoReturn(ulong va)
    {
        lock (_noReturnGate)
        {
            return _noReturn.Contains(va);
        }
    }

    /// <summary>
    /// A one-block function whose only exit is a jump to something that never returns is itself a
    /// no-return thunk. Recording it lets later callers stop at the right instruction.
    /// </summary>
    private void RecordIfNoReturnThunk(Function function)
    {
        if (function.Blocks.Count != 1)
        {
            return;
        }

        var last = function.Blocks[0].Last;
        ulong? target = last.Flow switch
        {
            InstructionFlow.UnconditionalBranch => last.BranchTargetVa,
            InstructionFlow.IndirectBranch => last.IndirectSlotVa,
            _ => null,
        };

        if (target is { } t && IsNoReturn(t))
        {
            lock (_noReturnGate)
            {
                _noReturn.Add(function.EntryVa);
            }
        }
    }

    /// <summary>Functions that reference <paramref name="va"/>, nearest enclosing function per reference.</summary>
    public IReadOnlyList<(Xref Xref, Function? From)> XrefsTo(ulong va)
        => Xrefs.To(va).Select(x => (x, FunctionContaining(x.FromVa))).ToList();

    /// <summary>The discovered function whose blocks cover <paramref name="va"/>, if any.</summary>
    public Function? FunctionContaining(ulong va)
    {
        if (_functions.TryGetValue(va, out var exact))
        {
            return exact;
        }

        // Functions are sparse and can have gaps, so match on block ranges rather than entry..end.
        foreach (var f in _functions.Values)
        {
            if (va >= f.EntryVa && va < f.EndVa && f.Blocks.Any(b => va >= b.StartVa && va < b.EndVa))
            {
                return f;
            }
        }

        return null;
    }

    /// <summary>Best-effort name for a VA: symbol, function, or <c>loc_XXXX</c>.</summary>
    public string NameFor(ulong va) => Symbols.NameOrDefault(va);
}
