using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Spydate.Core.PE;
using Spydate.Disassembly;
using Wpf.Ui.Controls;

namespace Spydate.App.ViewModels.Documents;

// ---------------------------------------------------------------------------
// Sections
// ---------------------------------------------------------------------------

public sealed record SectionRow(
    int Index,
    string Name,
    string VirtualAddress,
    string VirtualSize,
    string RawPointer,
    string RawSize,
    string Permissions,
    string Characteristics,
    string Entropy,
    SectionHeader Header);

public sealed partial class SectionsDocumentViewModel : DocumentViewModel
{
    private readonly Action<SectionHeader> _openHex;

    public SectionsDocumentViewModel(PeImage pe, Action<SectionHeader> openHex) : base("sections", "Sections", SymbolRegular.Layer24)
    {
        _openHex = openHex;
        Rows = pe.Sections.Select(s => new SectionRow(
            s.Index,
            s.Name,
            $"0x{s.VirtualAddress:X8}",
            $"0x{s.VirtualSize:X8}",
            $"0x{s.PointerToRawData:X8}",
            $"0x{s.SizeOfRawData:X8}",
            s.Permissions,
            DescribeCharacteristics(s.Characteristics),
            Entropy(pe.ReadAtRva(s.VirtualAddress, (int)Math.Min(s.SizeOfRawData, 4 * 1024 * 1024)).Span).ToString("0.00", CultureInfo.InvariantCulture),
            s)).ToList();
    }

    public List<SectionRow> Rows { get; }

    [RelayCommand]
    private void OpenInHex(SectionRow? row)
    {
        if (row is not null)
        {
            _openHex(row.Header);
        }
    }

    private static string DescribeCharacteristics(SectionCharacteristics c)
    {
        var parts = new List<string>();
        if (c.HasFlag(SectionCharacteristics.ContainsCode)) parts.Add("CODE");
        if (c.HasFlag(SectionCharacteristics.ContainsInitializedData)) parts.Add("IDATA");
        if (c.HasFlag(SectionCharacteristics.ContainsUninitializedData)) parts.Add("UDATA");
        if (c.HasFlag(SectionCharacteristics.MemDiscardable)) parts.Add("DISCARDABLE");
        if (c.HasFlag(SectionCharacteristics.MemNotPaged)) parts.Add("NOT_PAGED");
        if (c.HasFlag(SectionCharacteristics.MemShared)) parts.Add("SHARED");
        if (c.HasFlag(SectionCharacteristics.MemNotCached)) parts.Add("NOT_CACHED");
        parts.Add($"0x{(uint)c:X8}");
        return string.Join(" ", parts);
    }

    /// <summary>Shannon entropy in bits per byte (0–8); ≥ 7.2 usually means packed/compressed/encrypted.</summary>
    public static double Entropy(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return 0;
        }

        Span<int> counts = stackalloc int[256];
        foreach (byte b in data)
        {
            counts[b]++;
        }

        double entropy = 0;
        double len = data.Length;
        foreach (int c in counts)
        {
            if (c == 0)
            {
                continue;
            }

            double p = c / len;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }
}

// ---------------------------------------------------------------------------
// Resources
// ---------------------------------------------------------------------------

public sealed record ResourceRow(string Type, string Name, string Language, string Rva, string Offset, string Size, string CodePage, long FileOffset);

public sealed partial class ResourcesDocumentViewModel : DocumentViewModel
{
    private readonly Action<long> _openHex;

    public ResourcesDocumentViewModel(PeImage pe, Action<long> openHex) : base("resources", "Resources", SymbolRegular.Image24)
    {
        _openHex = openHex;
        Rows = Flatten(pe).ToList();
    }

    public List<ResourceRow> Rows { get; }

    public string Summary => $"{Rows.Count} entries, {Rows.Select(r => r.Type).Distinct().Count()} types";

    /// <summary>Walks type -> name -> language and yields one row per data entry.</summary>
    private static IEnumerable<ResourceRow> Flatten(PeImage pe)
    {
        if (pe.Resources is not { Children: { } types })
        {
            yield break;
        }

        foreach (var type in types)
        {
            foreach (var name in type.Children ?? (IReadOnlyList<ResourceNode>)Array.Empty<ResourceNode>())
            {
                var languages = name.IsDirectory ? name.Children! : new[] { name };
                foreach (var leaf in languages)
                {
                    if (leaf.IsDirectory)
                    {
                        continue;
                    }

                    long offset = pe.RvaToOffset(leaf.DataRva) is { } o ? o : -1;
                    yield return new ResourceRow(
                        type.DisplayName,
                        name.DisplayName,
                        leaf.Name ?? LanguageName(leaf.Id),
                        $"0x{leaf.DataRva:X8}",
                        offset >= 0 ? $"0x{offset:X8}" : "(unmapped)",
                        $"{leaf.DataSize:N0}",
                        leaf.CodePage == 0 ? "-" : leaf.CodePage.ToString(CultureInfo.InvariantCulture),
                        offset);
                }
            }
        }
    }

    /// <summary>Primary language id -> name for the handful that actually show up in system binaries.</summary>
    private static string LanguageName(uint id) => (id & 0x3FF) switch
    {
        0 => $"neutral (#{id})",
        9 => $"English (#{id})",
        7 => $"German (#{id})",
        10 => $"Spanish (#{id})",
        12 => $"French (#{id})",
        17 => $"Japanese (#{id})",
        4 => $"Chinese (#{id})",
        25 => $"Russian (#{id})",
        _ => $"#{id}",
    };

    [RelayCommand]
    private void OpenHex(ResourceRow? row)
    {
        if (row is { FileOffset: >= 0 })
        {
            _openHex(row.FileOffset);
        }
    }
}

// ---------------------------------------------------------------------------
// Imports
// ---------------------------------------------------------------------------

public sealed record ImportRow(string Module, string Function, string Hint, string IatRva, string IatVa, string Kind);

public sealed class ImportsDocumentViewModel : DocumentViewModel
{
    public ImportsDocumentViewModel(PeImage pe) : base("imports", "Imports", SymbolRegular.ArrowImport24)
    {
        Rows = pe.Imports.Concat(pe.DelayImports)
            .SelectMany(m => m.Functions.Select(f => new ImportRow(
                m.Name,
                f.DisplayName,
                f.IsByOrdinal ? "-" : f.Hint.ToString(CultureInfo.InvariantCulture),
                $"0x{f.IatRva:X8}",
                $"0x{pe.RvaToVa(f.IatRva):X}",
                m.IsDelayLoad ? "delay" : f.IsByOrdinal ? "ordinal" : "name")))
            .ToList();
        ModuleCount = pe.Imports.Count + pe.DelayImports.Count;
    }

    public List<ImportRow> Rows { get; }
    public int ModuleCount { get; }
    public string Summary => $"{ModuleCount} modules, {Rows.Count} functions";
}

// ---------------------------------------------------------------------------
// Exports
// ---------------------------------------------------------------------------

public sealed record ExportRow(uint Ordinal, string Name, string Rva, string Va, string Forwarder, string Section, ExportedFunction Export);

public sealed partial class ExportsDocumentViewModel : DocumentViewModel
{
    private readonly Action<ulong, string>? _openDisassembly;

    public ExportsDocumentViewModel(PeImage pe, Action<ulong, string>? openDisassembly) : base("exports", "Exports", SymbolRegular.ArrowExport24)
    {
        _openDisassembly = openDisassembly;
        var table = pe.Exports;
        ModuleName = table?.Name ?? "(no export table)";
        Rows = table?.Entries.Select(e => new ExportRow(
            e.Ordinal,
            e.DisplayName,
            e.IsForwarder ? "-" : $"0x{e.Rva:X8}",
            e.IsForwarder ? "-" : $"0x{pe.RvaToVa(e.Rva):X}",
            e.ForwarderName ?? string.Empty,
            e.IsForwarder ? string.Empty : pe.SectionFromRva(e.Rva)?.Name ?? "?",
            e)).ToList() ?? new List<ExportRow>();
        ImageBase = pe.ImageBase;
    }

    public string ModuleName { get; }
    public List<ExportRow> Rows { get; }
    public ulong ImageBase { get; }
    public string Summary => $"{ModuleName} — {Rows.Count} exports";

    [RelayCommand]
    private void OpenDisassembly(ExportRow? row)
    {
        if (row is { Export.IsForwarder: false } && _openDisassembly is not null)
        {
            _openDisassembly(ImageBase + row.Export.Rva, row.Name);
        }
    }
}

// ---------------------------------------------------------------------------
// Functions
// ---------------------------------------------------------------------------

public sealed record FunctionRow(string Name, string Va, string Size, int Blocks, int Instructions, int Calls, Function Function);

public sealed partial class FunctionsDocumentViewModel : DocumentViewModel
{
    private readonly BinaryAnalysis _analysis;
    private readonly Action<Function> _openDisassembly;
    private readonly Action<Function> _openPseudoC;

    public FunctionsDocumentViewModel(BinaryAnalysis analysis, Action<Function> openDisassembly, Action<Function> openPseudoC)
        : base("functions", "Functions", SymbolRegular.BranchFork24)
    {
        _analysis = analysis;
        _openDisassembly = openDisassembly;
        _openPseudoC = openPseudoC;
        Refresh();
    }

    public List<FunctionRow> Rows { get; private set; } = new();

    public string Summary => $"{Rows.Count} functions";

    public void Refresh()
    {
        Rows = _analysis.Functions.Select(f => new FunctionRow(
            f.Name,
            $"0x{f.EntryVa:X}",
            $"0x{f.CodeSize:X}",
            f.Blocks.Count,
            f.InstructionCount,
            f.CallTargets.Count + f.IndirectCallSlots.Count,
            f)).ToList();
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private void OpenDisassembly(FunctionRow? row)
    {
        if (row is not null)
        {
            _openDisassembly(row.Function);
        }
    }

    [RelayCommand]
    private void OpenPseudoC(FunctionRow? row)
    {
        if (row is not null)
        {
            _openPseudoC(row.Function);
        }
    }
}
