using System.Collections.Concurrent;
using Spydate.Core.Binary;

namespace Spydate.Core.Symbols;

/// <summary>
/// Thread-safe VA → <see cref="Symbol"/> map. Populated from PE metadata via <see cref="FromImage"/>
/// and later extended by analysis (discovered functions, user renames).
/// </summary>
public sealed class SymbolTable
{
    private readonly ConcurrentDictionary<ulong, Symbol> _byVa = new();
    private readonly ConcurrentDictionary<string, Symbol> _byName = new(StringComparer.Ordinal);

    public int Count => _byVa.Count;

    public IEnumerable<Symbol> All => _byVa.Values;

    /// <summary>Adds a symbol; an existing symbol at the same VA is only replaced when <paramref name="overwrite"/> is true.</summary>
    public bool Add(Symbol symbol, bool overwrite = false)
    {
        bool added;
        if (overwrite)
        {
            _byVa[symbol.Va] = symbol;
            added = true;
        }
        else
        {
            added = _byVa.TryAdd(symbol.Va, symbol);
        }

        if (added)
        {
            _byName[symbol.Name] = symbol;
        }

        return added;
    }

    /// <summary>Removes the symbol at <paramref name="va"/>, if there is one.</summary>
    public bool Remove(ulong va)
    {
        if (!_byVa.TryRemove(va, out var removed))
        {
            return false;
        }

        // The name index may already point at a different symbol with the same name.
        if (_byName.TryGetValue(removed.Name, out var byName) && byName.Va == va)
        {
            _byName.TryRemove(removed.Name, out _);
        }

        return true;
    }

    public bool TryGet(ulong va, out Symbol symbol) => _byVa.TryGetValue(va, out symbol!);

    public Symbol? Get(ulong va) => _byVa.TryGetValue(va, out var s) ? s : null;

    public Symbol? GetByName(string name) => _byName.TryGetValue(name, out var s) ? s : null;

    /// <summary>Returns the symbol name at <paramref name="va"/>, or a generated <c>sub_XXXX</c>/<c>loc_XXXX</c> name.</summary>
    public string NameOrDefault(ulong va, string prefix = "loc")
        => _byVa.TryGetValue(va, out var s) ? s.Name : $"{prefix}_{va:X}";

    /// <summary>
    /// What the entry point is called: the name the image gives it (an ELF's <c>_start</c>) when it has one,
    /// otherwise <c>EntryPoint</c>, or <c>DllEntryPoint</c> for a PE DLL.
    /// </summary>
    public static string EntryPointName(IBinaryImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image is ISymbolSource source && source.Symbols.FirstOrDefault(s => s.Rva == image.EntryPointRva && s.Kind == ImageSymbolKind.Function) is { } named)
        {
            return named.Name;
        }

        return image.IsLibrary && image.Format == BinaryFormat.Pe ? "DllEntryPoint" : "EntryPoint";
    }

    /// <summary>
    /// Builds the initial symbol table from an image: entry point, exports, import slots, the image's own
    /// symbols (an ELF's symbol table and PLT stubs), sections.
    /// </summary>
    public static SymbolTable FromImage(IBinaryImage image)
    {
        var table = new SymbolTable();

        if (image.EntryPointRva != 0)
        {
            table.Add(new Symbol(image.EntryPointVa, EntryPointName(image), SymbolKind.EntryPoint));
        }

        foreach (var e in image.Exports)
        {
            if (e.IsForwarder || e.Rva == 0)
            {
                continue;
            }

            table.Add(new Symbol(image.RvaToVa(e.Rva), e.Name ?? $"Ordinal{e.Ordinal}", SymbolKind.Export));
        }

        foreach (var import in image.Imports)
        {
            // An ELF import whose library the file does not record is still an import; its slot says so by name.
            string name = import.Module.Length == 0 ? $"{import.DisplayName}@got" : $"{StripExtension(import.Module)}!{import.DisplayName}";
            table.Add(new Symbol(image.RvaToVa(import.SlotRva), name, SymbolKind.Import, (uint)(image.Is64Bit ? 8 : 4)));
        }

        if (image is ISymbolSource source)
        {
            foreach (var s in source.Symbols)
            {
                // A PLT stub is code — discovery reads it as a one-jump function — and carries the import's bare name.
                var kind = s.Kind == ImageSymbolKind.Data ? SymbolKind.Data : SymbolKind.Function;
                table.Add(new Symbol(image.RvaToVa(s.Rva), s.Name, kind, s.Size));
            }
        }

        foreach (var s in image.Sections)
        {
            // Sections are useful as low-priority names for the start of data regions.
            table.Add(new Symbol(image.RvaToVa(s.Rva), s.Name.Length == 0 ? $"section_{s.Index}" : s.Name, SymbolKind.Section, s.Extent));
        }

        return table;
    }

    /// <summary><c>kernel32.dll</c> → <c>kernel32</c>; <c>libc.so.6</c> → <c>libc</c>, dropping the version too.</summary>
    private static string StripExtension(string moduleName)
    {
        int so = moduleName.IndexOf(".so", StringComparison.Ordinal);
        if (so > 0 && (so + 3 == moduleName.Length || moduleName[so + 3] == '.'))
        {
            return moduleName[..so];
        }

        int dot = moduleName.LastIndexOf('.');
        return dot > 0 ? moduleName[..dot] : moduleName;
    }
}
