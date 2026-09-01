using System.Collections.Concurrent;
using Spydate.Core.PE;
using Spydate.Core.Symbols;

namespace Spydate.Disassembly;

/// <summary>Where a resolved module was read from, and whether it was usable.</summary>
public sealed record ResolvedModule(string Name, string? Path, int Bitness, int Exports, string? Problem)
{
    public bool IsUsable => Problem is null;

    public override string ToString() => Problem is null ? $"{Name} → {Path} ({Exports} exports)" : $"{Name}: {Problem}";
}

/// <summary>
/// Answers what an imported function takes by opening the DLL that exports it and reading the export's
/// own code. This is the alternative to a hand-typed Win32 signature table recorded in DECISIONS.md: a
/// table cannot be checked against anything on the machine, whereas <c>user32!SendMessageW</c> ending in
/// <c>ret 10h</c> is a fact about a file that is present.
///
/// What it gives, and does not:
///
/// <list type="bullet">
/// <item>On x86 it gives an exact argument count for every <c>__stdcall</c> export, which is nearly all
/// of the Win32 API.</item>
/// <item>On x64 it gives which argument slots the export reads and which of those arrive in an xmm
/// register — the float arguments — as a lower bound.</item>
/// <item>It gives no parameter <em>names</em> and no types beyond integer-versus-float. Those are not in
/// the binary, so they are still not claimed.</item>
/// </list>
///
/// Everything here fails soft. A missing DLL, an unreadable one, or one built for the other architecture
/// yields nothing and analysis continues exactly as it did before; that is the price of not requiring the
/// DLLs to be present, and it is why <see cref="ResolvedModule"/> records what happened for each one.
/// </summary>
public sealed class ImportSignatures
{
    /// <summary>
    /// How many hops of <c>A.dll → B.dll → C.dll</c> are followed. A single export can need several: the
    /// kernel32 entry jumps through its own import table at an api set, which redirects to kernelbase,
    /// where a forwarder may send it on again. Each hop is a dictionary lookup, so the budget is generous.
    /// </summary>
    private const int MaxForwarderDepth = 8;

    /// <summary>
    /// Instruction budget when discovering an export. Generous enough for a real API entry point and
    /// small enough that a bad decode cannot spend the session walking a DLL.
    /// </summary>
    private const int ExportInstructionBudget = 4000;

    /// <summary>Modules opened at most once each, whether or not they turned out to be usable.</summary>
    private readonly ConcurrentDictionary<string, Module> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Module, string Export), CalleeSignature> _cache = new();

    private readonly Lock _schemaGate = new();
    private ApiSetSchema? _schema;
    private readonly int _bitness;
    private readonly string[] _searchPaths;

    /// <param name="bitness">Architecture the analysed image is built for; a DLL that disagrees is rejected.</param>
    /// <param name="searchPaths">Directories to look in, in order. Usually the image's own folder first.</param>
    public ImportSignatures(int bitness, IEnumerable<string> searchPaths)
    {
        ArgumentNullException.ThrowIfNull(searchPaths);
        _bitness = bitness;
        _searchPaths = searchPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// The places a DLL an image imports is looked for: beside the image, then the system directory for
    /// the image's own architecture. A 32-bit binary's <c>kernel32.dll</c> is the one in SysWOW64, and
    /// reading the 64-bit one instead would report every argument count wrong.
    /// </summary>
    public static ImportSignatures For(PeImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var paths = new List<string>();
        if (image.Path is { Length: > 0 } file && Path.GetDirectoryName(file) is { Length: > 0 } beside)
        {
            paths.Add(beside);
        }

        string root = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (root.Length > 0)
        {
            // Sysnative/SysWOW64 are the two real directories; Environment.SpecialFolder.System is
            // whichever one this process happens to be, which is not the question being asked.
            paths.Add(Path.Combine(root, image.Bitness == 32 ? "SysWOW64" : "System32"));
        }

        return new ImportSignatures(image.Bitness, paths);
    }

    /// <summary>
    /// The table behind <c>api-ms-win-*</c> names, read from <c>apisetschema.dll</c> the first time one
    /// is asked for. Without it nearly every import of a binary built this decade resolves to nothing.
    /// </summary>
    public ApiSetSchema Schema
    {
        get
        {
            lock (_schemaGate)
            {
                return _schema ??= LoadSchema();
            }
        }
    }

    private ApiSetSchema LoadSchema()
    {
        // The schema is a property of the installed system, not of an architecture: Windows ships one
        // copy, in System32, and a 32-bit process is redirected by the same table. So System32 is
        // searched even when the image being analysed is 32-bit and its DLLs come from SysWOW64.
        string system32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        foreach (string directory in _searchPaths.Append(system32))
        {
            string path = Path.Combine(directory, "apisetschema.dll");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                // Loaded directly rather than through Open: the schema exports nothing, so the checks
                // that make an ordinary module usable would throw it away.
                return ApiSetSchema.From(PeImage.Load(path));
            }
            catch (Exception ex) when (ex is PeParseException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return ApiSetSchema.Empty;
            }
        }

        return ApiSetSchema.Empty;
    }

    /// <summary>Modules that have been looked for so far, and what came of each.</summary>
    public IReadOnlyList<ResolvedModule> Modules => _modules.Values.Select(m => m.Resolved).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// What <paramref name="export"/> of <paramref name="module"/> takes, or
    /// <see cref="CalleeSignature.Unknown"/> when the module is absent or the export cannot be read.
    /// </summary>
    public CalleeSignature Lookup(string module, string export)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(export);
        return _cache.GetOrAdd((module, export), key => Resolve(key.Module, key.Export, MaxForwarderDepth));
    }

    /// <summary>
    /// Splits the symbol name an import thunk carries — <c>kernel32!GetProcAddress</c> — and looks it up.
    /// Returns unknown for anything that is not in that shape, ordinals included: an ordinal-only import
    /// names no export, and guessing which one it meant is exactly the invention this avoids.
    /// </summary>
    public CalleeSignature LookupSymbol(string symbolName)
    {
        ArgumentNullException.ThrowIfNull(symbolName);
        int bang = symbolName.IndexOf('!', StringComparison.Ordinal);
        if (bang <= 0 || bang == symbolName.Length - 1)
        {
            return CalleeSignature.Unknown;
        }

        string export = symbolName[(bang + 1)..];
        return export.StartsWith('#') ? CalleeSignature.Unknown : Lookup(symbolName[..bang], export);
    }

    /// <summary>The same split, continuing an existing chain rather than starting one.</summary>
    private CalleeSignature ResolveSymbol(string symbolName, int depth)
    {
        int bang = symbolName.IndexOf('!', StringComparison.Ordinal);
        if (bang <= 0 || bang == symbolName.Length - 1)
        {
            return CalleeSignature.Unknown;
        }

        string export = symbolName[(bang + 1)..];
        return export.StartsWith('#') ? CalleeSignature.Unknown : Resolve(symbolName[..bang], export, depth);
    }

    private CalleeSignature Resolve(string module, string export, int depth)
    {
        if (depth <= 0)
        {
            return CalleeSignature.Unknown;
        }

        string normalised = Normalise(module);

        // An api set is a name, not a file. Redirecting before looking on disk keeps the module report a
        // list of real files, and costs a level of the depth budget the same way a forwarder does.
        if (ApiSetSchema.IsApiSetName(normalised) && Schema.Resolve(normalised) is { } host
            && !host.Equals(normalised, StringComparison.OrdinalIgnoreCase))
        {
            return Resolve(host, export, depth - 1);
        }

        var loaded = _modules.GetOrAdd(normalised, Open);
        if (loaded.Image is null || loaded.Exports is null || !loaded.Exports.TryGetValue(export, out var entry))
        {
            return CalleeSignature.Unknown;
        }

        if (entry.ForwarderName is { } forwarder)
        {
            // "NTDLL.RtlAllocateHeap" — the module part carries no extension, and the export part can
            // itself be an ordinal (#123), which resolves to nothing by the rule above.
            int dot = forwarder.LastIndexOf('.');
            return dot <= 0 || dot == forwarder.Length - 1
                ? CalleeSignature.Unknown
                : Resolve(forwarder[..dot] + ".dll", forwarder[(dot + 1)..], depth - 1);
        }

        if (entry.Rva == 0)
        {
            return CalleeSignature.Unknown;
        }

        try
        {
            // Discovery is re-entrant: it keeps its whole state in locals, and the disassembler makes a
            // decoder per call. Two threads resolving different imports of the same DLL need not queue.
            var function = loaded.Discovery!.Discover(loaded.Image.RvaToVa(entry.Rva), export);

            // Most of the Win32 API is exported as a one-instruction jump through the exporting DLL's
            // own import table: kernel32!CloseHandle is `jmp [api-ms-win-core-handle-l1-1-0!CloseHandle]`.
            // The thunk takes exactly what it jumps to, so the answer is one module further on. Only a
            // lone jump qualifies - anything before it, such as kernel32!GetProcAddress reading the
            // return address into r8, is the thunk building a different argument list.
            if (function.InstructionCount == 1
                && function.Blocks[0].Last is { Flow: InstructionFlow.IndirectBranch, IndirectSlotVa: { } slot }
                && loaded.Symbols!.TryGet(slot, out var onward) && onward.Kind == SymbolKind.Import)
            {
                return ResolveSymbol(onward.Name, depth - 1);
            }

            return CalleeSignatures.FromCode(function, loaded.Image.Bitness);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or PeParseException or IndexOutOfRangeException)
        {
            return CalleeSignature.Unknown;
        }
    }

    /// <summary>Import descriptors spell the same DLL as "KERNEL32.dll", "kernel32.DLL" and "kernel32".</summary>
    private static string Normalise(string module)
        => module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? module : module + ".dll";

    private Module Open(string module)
    {
        // A module name from an import descriptor is untrusted data: it can contain a path, a traversal,
        // or characters no file name may have. Only a bare file name is ever opened, and only from the
        // directories this instance was given.
        if (module.Length == 0 || module.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return Module.Failed(module, "not a file name");
        }

        string? problem = null;
        foreach (string directory in _searchPaths)
        {
            string path = Path.Combine(directory, module);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var image = PeImage.Load(path);
                if (image.Bitness != _bitness)
                {
                    // Keep looking: a 32-bit image's kernel32 is in SysWOW64, and System32 holding a
                    // 64-bit one of the same name is not a reason to give up on the name.
                    problem ??= $"{path} is {image.Bitness}-bit, the image is {_bitness}-bit";
                    continue;
                }

                if (image.Exports is null || image.Exports.Entries.Count == 0)
                {
                    return Module.Failed(module, $"{path} exports nothing");
                }

                var symbols = SymbolTable.FromImage(image);
                var discovery = new FunctionDiscovery(
                    new PeCodeSource(image),
                    new X86Disassembler(image.Bitness, symbols),
                    symbols,
                    DiscoveryOptions.Default with { MaxInstructionsPerFunction = ExportInstructionBudget, SweepUnreachedBytes = false });

                var byName = new Dictionary<string, ExportedFunction>(StringComparer.Ordinal);
                foreach (var entry in image.Exports.Entries)
                {
                    if (entry.Name is { Length: > 0 } name)
                    {
                        byName[name] = entry;
                    }
                }

                return new Module(image, symbols, byName, discovery, new ResolvedModule(module, path, image.Bitness, byName.Count, null));
            }
            catch (Exception ex) when (ex is PeParseException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return Module.Failed(module, $"{path}: {ex.Message}");
            }
        }

        return Module.Failed(module, problem ?? (ApiSetSchema.IsApiSetName(module)
            ? "api set the schema does not name a host for"
            : "not found"));
    }

    private sealed record Module(
        PeImage? Image,
        SymbolTable? Symbols,
        Dictionary<string, ExportedFunction>? Exports,
        FunctionDiscovery? Discovery,
        ResolvedModule Resolved)
    {
        public static Module Failed(string name, string problem) => new(null, null, null, null, new ResolvedModule(name, null, 0, 0, problem));
    }
}
