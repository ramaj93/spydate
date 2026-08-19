using System.Collections.Concurrent;
using Spydate.Core.PE;
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

    public BinaryAnalysis(PeImage image, AsmSyntax syntax = AsmSyntax.Intel, DiscoveryOptions? options = null)
    {
        Image = image;
        Symbols = SymbolTable.FromImage(image);
        Source = new PeCodeSource(image);
        Disassembler = new X86Disassembler(image.Bitness, Symbols, syntax);
        _discovery = new FunctionDiscovery(Source, Disassembler, Symbols, options);
    }

    public PeImage Image { get; }

    public SymbolTable Symbols { get; }

    public ICodeSource Source { get; }

    public X86Disassembler Disassembler { get; }

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
            var f = _discovery.Discover(va, name);
            Symbols.Add(new Symbol(va, f.Name, SymbolKind.Function, f.CodeSize));
            return f;
        });
    }

    /// <summary>
    /// Seed VAs for whole-image discovery: entry point, executable exports, and (x64) every non-chained
    /// RUNTIME_FUNCTION start from the exception directory.
    /// </summary>
    public IReadOnlyList<(ulong Va, string? Name)> GetSeeds()
    {
        var seeds = new List<(ulong, string?)>();
        if (Image.EntryPointRva != 0 && Source.IsExecutable(Image.EntryPointVa))
        {
            seeds.Add((Image.EntryPointVa, Image.IsDll ? "DllEntryPoint" : "EntryPoint"));
        }

        if (Image.Exports is { } exports)
        {
            foreach (var e in exports.Entries)
            {
                if (e.IsForwarder || e.Rva == 0)
                {
                    continue;
                }

                ulong va = Image.RvaToVa(e.Rva);
                if (Source.IsExecutable(va))
                {
                    seeds.Add((va, e.Name ?? $"Ordinal{e.Ordinal}"));
                }
            }
        }

        var seen = new HashSet<ulong>(seeds.Select(s => s.Item1));
        foreach (var rf in Image.ExceptionTable)
        {
            if (rf.IsChained || rf.BeginRva == 0)
            {
                continue;
            }

            ulong va = Image.RvaToVa(rf.BeginRva);
            if (seen.Add(va) && Source.IsExecutable(va))
            {
                seeds.Add((va, null));
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

    /// <summary>Best-effort name for a VA: symbol, function, or <c>loc_XXXX</c>.</summary>
    public string NameFor(ulong va) => Symbols.NameOrDefault(va);
}
