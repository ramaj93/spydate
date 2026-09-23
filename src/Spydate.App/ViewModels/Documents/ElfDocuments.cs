using System.Globalization;
using Spydate.Core.Elf;
using Wpf.Ui.Controls;

namespace Spydate.App.ViewModels.Documents;

public sealed record ElfSegmentRow(int Index, string Type, string Offset, string VirtualAddress, string FileSize, string MemorySize, string Permissions, string Align, string Contains, long FileOffset);

public sealed record ElfSectionRow(int Index, string Name, string Type, string Address, string Offset, string Size, string Flags, string Link, string Info, string Align, string EntrySize, string Entropy, long FileOffset);

public sealed record ElfDynamicRow(string Tag, string Value, string Meaning);

public sealed record ElfSymbolRow(string Table, int Index, string Name, string Value, string Size, string Type, string Binding, string Visibility, string Section, string Version, ulong Va, bool IsCode);

public sealed record ElfImportRow(string Library, string Symbol, string Version, string Slot, string Stub, ulong StubVa);

public sealed record ElfExportRow(string Name, string Address, string Size, string Type, string Binding, string Section, ulong Va, bool IsCode);

/// <summary>
/// The documents an ELF has instead of a PE's: its header, segments, section headers, dynamic section and
/// symbol tables. Each is a <see cref="RecordsDocumentViewModel"/>; a row that has code opens it, one that has
/// bytes opens them in the hex view.
/// </summary>
public static class ElfDocuments
{
    public static RecordsDocumentViewModel Headers(ElfImage elf)
    {
        var h = elf.Header;
        var rows = new List<PropertyRow>
        {
            new("Class", h.ClassName, h.Is64Bit ? "64-bit addresses and offsets" : "32-bit addresses and offsets"),
            new("Data", h.ByteOrder),
            new("OS/ABI", h.OsAbiName, h.AbiVersion == 0 ? null : $"ABI version {h.AbiVersion}"),
            new("Type", $"{(ushort)h.Type}", elf.Kind),
            new("Machine", $"{h.Machine}", h.MachineName),
            new("Version", h.Version.ToString(CultureInfo.InvariantCulture)),
            new("Entry point", $"0x{h.Entry:X}", h.Entry == 0 ? "none" : elf.SectionFromVa(h.Entry)?.Name),
            new("Program headers", $"0x{h.ProgramHeaderOffset:X}", $"{h.ProgramHeaderCount} × {h.ProgramHeaderEntrySize} bytes"),
            new("Section headers", $"0x{h.SectionHeaderOffset:X}", $"{h.SectionHeaderCount} × {h.SectionHeaderEntrySize} bytes"),
            new("Section names", h.SectionNameTableIndex.ToString(CultureInfo.InvariantCulture), elf.SectionHeaders.ElementAtOrDefault(h.SectionNameTableIndex)?.Name),
            new("Flags", $"0x{h.Flags:X8}"),
            new("Header size", h.HeaderSize.ToString(CultureInfo.InvariantCulture)),
            new("Image base", $"0x{elf.ImageBase:X}", "the lowest loaded address; RVAs count from here"),
            new("Interpreter", elf.Interpreter ?? "(none)", elf.Interpreter is null ? "statically linked, or a library" : "the dynamic loader"),
            new("Build ID", elf.BuildId ?? "(none)"),
        };

        return new RecordsDocumentViewModel(
            "headers",
            "Headers",
            SymbolRegular.DocumentHeader24,
            "the ELF header, field by field",
            [new("Field", nameof(PropertyRow.Name), 160), new("Value", nameof(PropertyRow.Value), 220), new("Meaning", nameof(PropertyRow.Note), 0)],
            rows);
    }

    public static RecordsDocumentViewModel Segments(ElfImage elf, Action<long> openHex)
    {
        var rows = elf.Segments.Select(s => new ElfSegmentRow(
            s.Index,
            s.TypeName,
            $"0x{s.Offset:X}",
            $"0x{s.VirtualAddress:X}",
            $"0x{s.FileSize:X}",
            $"0x{s.MemorySize:X}",
            s.Permissions,
            $"0x{s.Align:X}",
            string.Join(" ", elf.SectionHeaders.Where(c => c.IsAllocated && c.Size > 0 && s.IsLoad && c.Address >= s.VirtualAddress && c.Address < s.VirtualAddress + s.MemorySize).Select(c => c.Name)),
            (long)s.Offset)).ToList();

        return new RecordsDocumentViewModel(
            "segments",
            "Segments",
            SymbolRegular.Layer24,
            "what the loader maps; double-click for the bytes",
            [
                new("#", nameof(ElfSegmentRow.Index), 32), new("Type", nameof(ElfSegmentRow.Type), 110), new("Offset", nameof(ElfSegmentRow.Offset), 90),
                new("Virtual address", nameof(ElfSegmentRow.VirtualAddress), 130), new("File size", nameof(ElfSegmentRow.FileSize), 90),
                new("Memory size", nameof(ElfSegmentRow.MemorySize), 95), new("Perm", nameof(ElfSegmentRow.Permissions), 50),
                new("Align", nameof(ElfSegmentRow.Align), 70), new("Sections", nameof(ElfSegmentRow.Contains), 0),
            ],
            rows,
            row => openHex(((ElfSegmentRow)row).FileOffset));
    }

    public static RecordsDocumentViewModel Sections(ElfImage elf, Action<long> openHex)
    {
        var rows = elf.SectionHeaders.Select(s => new ElfSectionRow(
            s.Index,
            s.Name,
            s.TypeName,
            s.IsAllocated ? $"0x{elf.AddressOf(s):X}" : "-",
            $"0x{s.Offset:X}",
            $"0x{s.Size:X}",
            s.FlagText,
            s.Link.ToString(CultureInfo.InvariantCulture),
            s.Info.ToString(CultureInfo.InvariantCulture),
            $"0x{s.AddressAlign:X}",
            $"0x{s.EntrySize:X}",
            s.HasNoBits ? "-" : SectionsDocumentViewModel.Entropy(Slice(elf, s)).ToString("0.00", CultureInfo.InvariantCulture),
            (long)s.Offset)).ToList();

        return new RecordsDocumentViewModel(
            "sections",
            "Sections",
            SymbolRegular.Layer24,
            "every section header, loaded or not; double-click for the bytes.  Entropy ≥ 7.2 suggests packed or encrypted data",
            [
                new("#", nameof(ElfSectionRow.Index), 32), new("Name", nameof(ElfSectionRow.Name), 150), new("Type", nameof(ElfSectionRow.Type), 100),
                new("Address", nameof(ElfSectionRow.Address), 110), new("Offset", nameof(ElfSectionRow.Offset), 90), new("Size", nameof(ElfSectionRow.Size), 90),
                new("Flags", nameof(ElfSectionRow.Flags), 55), new("Link", nameof(ElfSectionRow.Link), 45), new("Info", nameof(ElfSectionRow.Info), 45),
                new("Align", nameof(ElfSectionRow.Align), 60), new("EntSize", nameof(ElfSectionRow.EntrySize), 65), new("Entropy", nameof(ElfSectionRow.Entropy), 0),
            ],
            rows,
            row => openHex(((ElfSectionRow)row).FileOffset));
    }

    public static RecordsDocumentViewModel Dynamic(ElfImage elf)
    {
        var rows = elf.Dynamic.Select(d => new ElfDynamicRow(
            d.TagName,
            d.Text ?? $"0x{d.Value:X}",
            d.Tag switch
            {
                ElfDynamicEntry.Needed => "a library this one loads",
                ElfDynamicEntry.SoName => "the name other binaries link against",
                ElfDynamicEntry.RPath or ElfDynamicEntry.RunPath => "where the loader looks for libraries",
                ElfDynamicEntry.Flags1 => (d.Value & ElfDynamicEntry.Flags1Pie) != 0 ? "position-independent executable" : string.Empty,
                24 => "every symbol is bound at load time (full RELRO with GNU_RELRO)",
                _ => string.Empty,
            })).ToList();

        return new RecordsDocumentViewModel(
            "dynamic",
            "Dynamic",
            SymbolRegular.PlugConnected24,
            "what the dynamic loader is told: libraries, symbol tables, relocations, flags",
            [new("Tag", nameof(ElfDynamicRow.Tag), 140), new("Value", nameof(ElfDynamicRow.Value), 280), new("Meaning", nameof(ElfDynamicRow.Meaning), 0)],
            rows);
    }

    public static RecordsDocumentViewModel Symbols(ElfImage elf, Action<ulong, string> openCode, Action<long> openHex)
    {
        ElfSymbolRow Row(ElfSymbol s, string table) => new(
            table,
            s.Index,
            s.Name,
            $"0x{s.Value:X}",
            s.Size.ToString(CultureInfo.InvariantCulture),
            s.TypeName,
            s.BindingName,
            s.VisibilityName,
            s.SectionIndex switch
            {
                ElfSymbol.Undefined => "UND",
                ElfSymbol.Absolute => "ABS",
                ElfSymbol.CommonSection => "COM",
                var i when i < elf.SectionHeaders.Count => elf.SectionHeaders[i].Name,
                var i => i.ToString(CultureInfo.InvariantCulture),
            },
            s.Version is null ? string.Empty : $"{s.Version} ({s.Library})",
            s.Value,
            s.IsDefined && s.Type is ElfSymbolType.Function or ElfSymbolType.GnuIndirectFunction);

        var rows = elf.DynamicSymbols.Where(s => s.Index > 0).Select(s => Row(s, ".dynsym"))
            .Concat(elf.StaticSymbols.Where(s => s.Index > 0).Select(s => Row(s, ".symtab")))
            .ToList();

        return new RecordsDocumentViewModel(
            "symbols",
            "Symbols",
            SymbolRegular.Tag24,
            elf.StaticSymbols.Count == 0 ? "the dynamic symbols (the file is stripped: no .symtab); double-click to open" : ".dynsym and .symtab; double-click to open",
            [
                new("Table", nameof(ElfSymbolRow.Table), 70), new("#", nameof(ElfSymbolRow.Index), 50), new("Name", nameof(ElfSymbolRow.Name), 0),
                new("Value", nameof(ElfSymbolRow.Value), 110), new("Size", nameof(ElfSymbolRow.Size), 60), new("Type", nameof(ElfSymbolRow.Type), 65),
                new("Bind", nameof(ElfSymbolRow.Binding), 65), new("Visibility", nameof(ElfSymbolRow.Visibility), 75),
                new("Section", nameof(ElfSymbolRow.Section), 90), new("Version", nameof(ElfSymbolRow.Version), 200),
            ],
            rows,
            row =>
            {
                var r = (ElfSymbolRow)row;
                if (r.IsCode)
                {
                    openCode(r.Va, r.Name);
                }
                else if (elf.VaToOffset(r.Va) is { } offset)
                {
                    openHex(offset);
                }
            });
    }

    public static RecordsDocumentViewModel Imports(ElfImage elf, Action<ulong, string> openCode)
    {
        var stubs = elf.PltStubs.ToLookup(p => p.Import.SlotRva, p => p.Rva);
        var versions = elf.DynamicSymbols.Where(s => !s.IsDefined && s.Version is not null).GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First().Version!);
        var rows = elf.Imports.Select(i =>
        {
            uint? stub = stubs[i.SlotRva].Cast<uint?>().FirstOrDefault();
            return new ElfImportRow(
                i.Module.Length == 0 ? "(any)" : i.Module,
                i.DisplayName,
                i.Name is { } n && versions.TryGetValue(n, out var v) ? v : string.Empty,
                $"0x{elf.RvaToVa(i.SlotRva):X}",
                stub is { } s ? $"0x{elf.RvaToVa(s):X}" : "-",
                stub is { } at ? elf.RvaToVa(at) : 0);
        }).ToList();

        return new RecordsDocumentViewModel(
            "imports",
            "Imports",
            SymbolRegular.ArrowImport24,
            "each symbol this binary takes from a library, the GOT slot the loader fills and the PLT stub code calls; double-click opens the stub.  (any) = the file does not record which library",
            [
                new("Library", nameof(ElfImportRow.Library), 180), new("Symbol", nameof(ElfImportRow.Symbol), 0), new("Version", nameof(ElfImportRow.Version), 130),
                new("GOT slot", nameof(ElfImportRow.Slot), 130), new("PLT stub", nameof(ElfImportRow.Stub), 130),
            ],
            rows,
            row =>
            {
                if (row is ElfImportRow { StubVa: not 0 } r)
                {
                    openCode(r.StubVa, r.Symbol);
                }
            });
    }

    public static RecordsDocumentViewModel Exports(ElfImage elf, Action<ulong, string>? openCode)
    {
        var bySymbol = elf.DynamicSymbols.Where(s => s.IsDefined).GroupBy(s => (s.Name, s.Value)).ToDictionary(g => g.Key, g => g.First());
        var rows = elf.Exports.Select(e =>
        {
            ulong va = elf.RvaToVa(e.Rva);
            bySymbol.TryGetValue((e.Name ?? string.Empty, va), out var s);
            return new ElfExportRow(
                e.DisplayName,
                $"0x{va:X}",
                s?.Size.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                s?.TypeName ?? string.Empty,
                s?.BindingName ?? string.Empty,
                elf.SectionFromRva(e.Rva)?.Name ?? "?",
                va,
                elf.SectionFromRva(e.Rva) is { IsExecutable: true });
        }).ToList();

        return new RecordsDocumentViewModel(
            "exports",
            "Exports",
            SymbolRegular.ArrowExport24,
            openCode is null ? "the symbols this binary defines for others" : "the symbols this binary defines for others; double-click a function to disassemble",
            [
                new("Name", nameof(ElfExportRow.Name), 0), new("Address", nameof(ElfExportRow.Address), 130), new("Size", nameof(ElfExportRow.Size), 70),
                new("Type", nameof(ElfExportRow.Type), 70), new("Bind", nameof(ElfExportRow.Binding), 70), new("Section", nameof(ElfExportRow.Section), 90),
            ],
            rows,
            row =>
            {
                if (row is ElfExportRow { IsCode: true } r)
                {
                    openCode?.Invoke(r.Va, r.Name);
                }
            });
    }

    /// <summary>What <c>checksec</c> reports, read from the headers: the hardening a Linux binary was built with.</summary>
    public static IEnumerable<PropertyRow> Hardening(ElfImage elf)
    {
        var stack = elf.Segments.FirstOrDefault(s => s.Type == (uint)SegmentType.GnuStack);
        bool relro = elf.Segments.Any(s => s.Type == (uint)SegmentType.GnuRelro);
        bool bindNow = elf.Dynamic.Any(d => d.Tag == 24 || (d.Tag == 30 && (d.Value & 0x8) != 0) || (d.Tag == ElfDynamicEntry.Flags1 && (d.Value & 0x1) != 0));
        bool canary = elf.Imports.Any(i => i.Name is "__stack_chk_fail" or "__stack_chk_guard")
                      || elf.StaticSymbols.Any(s => s.Name is "__stack_chk_fail" or "__stack_chk_guard");
        int fortified = elf.Imports.Count(i => i.Name is { } n && n.StartsWith("__", StringComparison.Ordinal) && n.EndsWith("_chk", StringComparison.Ordinal));

        yield return new PropertyRow("PIE", elf.Header.Type == ElfType.Shared ? "yes" : "no", elf.Header.Type == ElfType.Shared ? "loads at a random address" : "loads at a fixed address");
        yield return new PropertyRow("NX", stack is null ? "unknown" : (stack.Flags & ElfSegment.FlagExecute) == 0 ? "yes" : "NO", stack is null ? "no GNU_STACK segment" : $"stack is {stack.Permissions}");
        yield return new PropertyRow("RELRO", relro ? bindNow ? "full" : "partial" : "none", relro && !bindNow ? "the GOT stays writable" : null);
        yield return new PropertyRow("Stack canary", canary ? "yes" : "no", canary ? "uses __stack_chk_fail" : null);
        yield return new PropertyRow("FORTIFY_SOURCE", fortified > 0 ? "yes" : "no", fortified > 0 ? $"{fortified} checked function(s)" : null);
        if (elf.Dynamic.FirstOrDefault(d => d.Tag is ElfDynamicEntry.RPath or ElfDynamicEntry.RunPath) is { } path)
        {
            yield return new PropertyRow(path.TagName, path.Text ?? string.Empty, "a library search path baked into the file");
        }
    }

    private static ReadOnlySpan<byte> Slice(ElfImage elf, ElfSectionHeader section)
    {
        var bytes = elf.SectionBytes(section);
        return bytes[..Math.Min(bytes.Length, 4 * 1024 * 1024)];
    }
}
