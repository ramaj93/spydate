using System.Collections.Concurrent;
using Spydate.Core.PE;

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

    public bool TryGet(ulong va, out Symbol symbol) => _byVa.TryGetValue(va, out symbol!);

    public Symbol? Get(ulong va) => _byVa.TryGetValue(va, out var s) ? s : null;

    public Symbol? GetByName(string name) => _byName.TryGetValue(name, out var s) ? s : null;

    /// <summary>Returns the symbol name at <paramref name="va"/>, or a generated <c>sub_XXXX</c>/<c>loc_XXXX</c> name.</summary>
    public string NameOrDefault(ulong va, string prefix = "loc")
        => _byVa.TryGetValue(va, out var s) ? s.Name : $"{prefix}_{va:X}";

    /// <summary>Builds the initial symbol table from a PE image: entry point, exports, IAT slots.</summary>
    public static SymbolTable FromImage(PeImage pe)
    {
        var table = new SymbolTable();

        if (pe.EntryPointRva != 0)
        {
            table.Add(new Symbol(pe.EntryPointVa, pe.IsDll ? "DllEntryPoint" : "EntryPoint", SymbolKind.EntryPoint));
        }

        if (pe.Exports is { } exports)
        {
            foreach (var e in exports.Entries)
            {
                if (e.IsForwarder || e.Rva == 0)
                {
                    continue;
                }

                table.Add(new Symbol(pe.RvaToVa(e.Rva), e.Name ?? $"Ordinal{e.Ordinal}", SymbolKind.Export));
            }
        }

        foreach (var module in pe.Imports.Concat(pe.DelayImports))
        {
            string moduleName = StripExtension(module.Name);
            foreach (var f in module.Functions)
            {
                table.Add(new Symbol(pe.RvaToVa(f.IatRva), $"{moduleName}!{f.DisplayName}", SymbolKind.Import, (uint)(pe.Is64Bit ? 8 : 4)));
            }
        }

        foreach (var s in pe.Sections)
        {
            // Sections are useful as low-priority names for the start of data regions.
            table.Add(new Symbol(pe.RvaToVa(s.VirtualAddress), s.Name.Length == 0 ? $"section_{s.Index}" : s.Name, SymbolKind.Section, s.VirtualExtent));
        }

        return table;
    }

    private static string StripExtension(string moduleName)
    {
        int dot = moduleName.LastIndexOf('.');
        return dot > 0 ? moduleName[..dot] : moduleName;
    }
}
