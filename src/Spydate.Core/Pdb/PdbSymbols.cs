using Spydate.Core.PE;
using Spydate.Core.Symbols;

namespace Spydate.Core.Pdb;

/// <summary>Outcome of looking for the PDB that belongs to an image.</summary>
public sealed record PdbLoadResult
{
    public required bool Loaded { get; init; }
    /// <summary>The file that was used, when one was.</summary>
    public string? Path { get; init; }
    /// <summary>Symbols added to the table (names already present are kept).</summary>
    public int SymbolsAdded { get; init; }
    /// <summary>Why nothing was loaded: missing file, wrong build, unreadable container.</summary>
    public string? Reason { get; init; }

    public override string ToString() => Loaded
        ? $"{SymbolsAdded} symbols from {Path}"
        : Reason ?? "no PDB";
}

/// <summary>Turns a PDB's public symbols into entries in a <see cref="SymbolTable"/>.</summary>
public static class PdbSymbols
{
    /// <summary>
    /// Adds every public symbol whose section-relative address resolves inside the image. Existing
    /// names win: exports carry the undecorated name a reader expects, while a PDB public is the
    /// decorated one.
    /// </summary>
    public static int Apply(PeImage image, PdbFile pdb, SymbolTable symbols)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(pdb);
        ArgumentNullException.ThrowIfNull(symbols);

        int added = 0;
        foreach (var symbol in pdb.PublicSymbols)
        {
            // Segments are 1-based indices into the image's section table.
            if (symbol.Segment == 0 || symbol.Segment > image.Sections.Count)
            {
                continue;
            }

            var section = image.Sections[symbol.Segment - 1];
            uint limit = Math.Max(section.VirtualSize, section.SizeOfRawData);
            if (symbol.Offset >= limit)
            {
                continue; // the record points past the section it names
            }

            uint rva = section.VirtualAddress + symbol.Offset;
            var kind = symbol.IsFunction ? SymbolKind.Function : SymbolKind.Data;
            if (symbols.Add(new Symbol(image.RvaToVa(rva), symbol.Name, kind)))
            {
                added++;
            }
        }

        return added;
    }

    /// <summary>
    /// Finds the PDB built with <paramref name="image"/> and applies it. A PDB whose GUID and age
    /// do not match is rejected: symbols from a different build land at the wrong addresses, which
    /// is worse than having none.
    /// </summary>
    public static PdbLoadResult TryLoadFor(PeImage image, SymbolTable symbols)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(symbols);

        var codeView = image.Debug.Select(d => d.CodeView).FirstOrDefault(cv => cv is not null);
        if (codeView is null)
        {
            return new PdbLoadResult { Loaded = false, Reason = "the image has no CodeView debug record" };
        }

        string? mismatch = null;
        foreach (string candidate in PdbFile.ProbePaths(image).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var pdb = PdbFile.TryLoad(candidate, out string? error);
            if (pdb is null)
            {
                mismatch ??= $"{candidate}: {error}";
                continue;
            }

            if (!pdb.Matches(codeView))
            {
                mismatch ??= $"{candidate} is from a different build (age {pdb.Age}, expected {codeView.Age})";
                continue;
            }

            return new PdbLoadResult
            {
                Loaded = true,
                Path = candidate,
                SymbolsAdded = Apply(image, pdb, symbols),
            };
        }

        return new PdbLoadResult
        {
            Loaded = false,
            Reason = mismatch ?? $"no PDB found (looked for {codeView.PdbPath})",
        };
    }
}
