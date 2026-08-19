using System.Collections.Concurrent;
using Spydate.Core.PE;
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
    private readonly ConcurrentDictionary<ulong, Function> _functions = new();
    private readonly FunctionDiscovery _discovery;
    private readonly XrefExtractor _xrefExtractor;
    private readonly Lazy<StringIndex> _strings;

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
        _discovery = new FunctionDiscovery(Source, Disassembler, Symbols, options.IsNoReturn is null ? options with { IsNoReturn = IsNoReturn } : options);
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
            var f = _discovery.Discover(va, name, BoundsFor(va));
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

        int processed = 0;
        while (queue.Count > 0 && _functions.Count < maxFunctions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (va, name) = queue.Dequeue();
            var f = GetOrDiscoverFunction(va, name);
            foreach (ulong target in f.CallTargets)
            {
                if (queued.Add(target) && Source.IsExecutable(target))
                {
                    queue.Enqueue((target, null));
                }
            }

            if (++processed % 64 == 0)
            {
                progress?.Report(new AnalysisProgress(_functions.Count, queue.Count, $"Analyzing {f.Name}"));
            }
        }

        progress?.Report(new AnalysisProgress(_functions.Count, queue.Count, "Discovery complete"));
        return Functions;
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
