using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spydate.Core.PE;
using Spydate.Core.Strings;
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

public sealed record ResourceRow(string Type, string Name, string Language, string Rva, string Offset, string Size, string CodePage, long FileOffset, uint TypeId, uint Id, uint DataRva, uint DataSize);

public sealed partial class ResourcesDocumentViewModel : DocumentViewModel
{
    private readonly Action<ResourceRow> _open;

    public ResourcesDocumentViewModel(PeImage pe, Action<ResourceRow> open) : base("resources", "Resources", SymbolRegular.Image24)
    {
        _open = open;
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
                        offset,
                        type.Id,
                        name.Id,
                        leaf.DataRva,
                        leaf.DataSize);
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
        if (row is not null)
        {
            _open(row);
        }
    }
}

// ---------------------------------------------------------------------------
// Strings
// ---------------------------------------------------------------------------

public sealed record StringRow(string Va, string Rva, string Offset, string Section, string Encoding, int Length, string Text, long FileOffset, bool InCodeSection, ulong? RawVa, int ByteLength, int Refs);

public sealed partial class StringsDocumentViewModel : DocumentViewModel
{
    private readonly PeImage _pe;
    private readonly BinaryAnalysis? _analysis;
    private readonly Action<long> _openHex;
    private IReadOnlyList<FoundString> _found = Array.Empty<FoundString>();

    public StringsDocumentViewModel(PeImage pe, BinaryAnalysis? analysis, Action<long> openHex)
        : base("strings", "Strings", SymbolRegular.TextT24)
    {
        _pe = pe;
        _analysis = analysis;
        _openHex = openHex;
    }

    /// <summary>Selecting a string points the Xrefs panel at the bytes it occupies.</summary>
    [ObservableProperty]
    private StringRow? _selectedRow;

    partial void OnSelectedRowChanged(StringRow? value)
    {
        Address = value?.RawVa;
        AddressLength = value is null ? 1 : Math.Max(1, value.ByteLength);
    }

    public List<StringRow> Rows { get; private set; } = new();

    [ObservableProperty]
    private string _summary = "scanning…";

    /// <summary>Scanning a large image touches every byte, so it happens off the UI thread.</summary>
    public override async Task LoadAsync(CancellationToken cancellationToken)
    {
        _found = await Task.Run(() => StringScanner.Scan(_pe, StringScanOptions.Default, cancellationToken), cancellationToken).ConfigureAwait(true);
        Rebuild();
    }

    /// <summary>Recomputes reference counts; discovery keeps finding code after the first scan.</summary>
    public void RefreshReferences()
    {
        if (_found.Count > 0)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var found = _found;
        var references = _analysis is null
            ? null
            : StringReferences.Resolve(found, _analysis.Xrefs);

        var codeSections = _pe.Sections.Where(s => s.IsExecutable).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        Rows = found.Select((s, i) => new StringRow(
            s.Va is { } va ? $"0x{va:X}" : "-",
            s.Rva is { } rva ? $"0x{rva:X8}" : "-",
            $"0x{s.Offset:X8}",
            s.Section,
            s.Encoding == StringEncodingKind.Utf16 ? "utf-16" : "ascii",
            s.Length,
            s.Text,
            s.Offset,
            codeSections.Contains(s.Section),
            s.Va,
            StringIndex.ByteLength(s),
            references?[i].Count ?? 0)).ToList();

        int wide = found.Count(s => s.Encoding == StringEncodingKind.Utf16);
        int inCode = Rows.Count(r => r.InCodeSection);
        int referenced = Rows.Count(r => r.Refs > 0);
        Summary = $"{Rows.Count:N0} strings ({wide:N0} utf-16, {inCode:N0} in code" +
                  (references is null ? ")" : $", {referenced:N0} referenced)");
        OnPropertyChanged(nameof(Rows));
    }

    [RelayCommand]
    private void OpenHex(StringRow? row)
    {
        if (row is not null)
        {
            _openHex(row.FileOffset);
        }
    }
}

// ---------------------------------------------------------------------------
// Imports
// ---------------------------------------------------------------------------

public sealed record ImportRow(string Module, string Function, string Hint, string IatRva, string IatVa, string Kind)
{
    /// <summary>What the DLL on disk says this import takes; empty when it could not be read.</summary>
    public string Takes { get; init; } = string.Empty;
}

public sealed partial class ImportsDocumentViewModel : DocumentViewModel
{
    private readonly BinaryAnalysis? _analysis;

    public ImportsDocumentViewModel(PeImage pe, BinaryAnalysis? analysis = null) : base("imports", "Imports", SymbolRegular.ArrowImport24)
    {
        _analysis = analysis;
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

    [ObservableProperty]
    private List<ImportRow> _rows;

    public int ModuleCount { get; }

    public string Summary => $"{ModuleCount} modules, {Rows.Count} functions";

    /// <summary>
    /// Opening every DLL an image imports and reading its exports takes about a second, so it happens
    /// when the document is first shown rather than when it is created.
    /// </summary>
    public override async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_analysis?.Signatures is not { } signatures)
        {
            return;
        }

        var rows = Rows;
        var (described, resolved) = await Task.Run(
            () =>
            {
                var built = new List<ImportRow>(rows.Count);
                int found = 0;
                foreach (var row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var signature = row.Kind == "ordinal"
                        ? CalleeSignature.Unknown
                        : signatures.Lookup(row.Module, row.Function);
                    if (signature.Source != SignatureSource.None)
                    {
                        found++;
                    }

                    built.Add(row with { Takes = Describe(signature) });
                }

                return (built, found);
            },
            cancellationToken).ConfigureAwait(true);

        Rows = described;
        StatusMessage = $"{resolved} of {rows.Count} imports read from the DLLs on disk";
    }

    /// <summary>
    /// What was learned, in a column's worth of words. A count and a float slot are different kinds of
    /// fact and both are worth seeing; "caller-cleaned" is what is known about a cdecl import, whose
    /// argument count its own code does not state.
    /// </summary>
    private static string Describe(CalleeSignature signature)
    {
        if (signature.Source == SignatureSource.None)
        {
            return string.Empty;
        }

        if (!signature.HasArgumentCount)
        {
            return signature.StackCleanupBytes == 0 ? "caller-cleaned" : string.Empty;
        }

        string text = signature.ArgumentCount == 1 ? "1 arg" : $"{signature.ArgumentCount} args";
        var floats = Enumerable.Range(0, signature.ArgumentCount).Where(signature.IsFloat).ToList();
        return floats.Count == 0 ? text : $"{text} (float: {string.Join(", ", floats)})";
    }
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

public sealed record FunctionRow(string Name, string Va, string Size, int Blocks, int Instructions, int Calls, int Refs, Function Function);

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
            _analysis.Xrefs.CountTo(f.EntryVa),
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
