using System.Globalization;
using Spydate.Core.PE;
using Wpf.Ui.Controls;

namespace Spydate.App.ViewModels.Documents;

/// <summary>Raw header field tables: DOS, COFF file header, optional header, data directories.</summary>
public sealed class HeadersDocumentViewModel : DocumentViewModel
{
    public HeadersDocumentViewModel(PeImage pe) : base("headers", "Headers", SymbolRegular.DocumentHeader24)
    {
        var d = pe.DosHeader;
        Dos = new List<PropertyRow>
        {
            new("e_magic", $"0x{d.Magic:X4} (\"MZ\")"),
            new("e_cblp", Hex(d.BytesOnLastPage)),
            new("e_cp", Hex(d.PagesInFile)),
            new("e_crlc", Hex(d.Relocations)),
            new("e_cparhdr", Hex(d.HeaderParagraphs)),
            new("e_minalloc", Hex(d.MinExtraParagraphs)),
            new("e_maxalloc", Hex(d.MaxExtraParagraphs)),
            new("e_ss:e_sp", $"0x{d.InitialSs:X4}:0x{d.InitialSp:X4}"),
            new("e_csum", Hex(d.Checksum)),
            new("e_cs:e_ip", $"0x{d.InitialCs:X4}:0x{d.InitialIp:X4}"),
            new("e_lfarlc", Hex(d.RelocationTableOffset)),
            new("e_ovno", Hex(d.OverlayNumber)),
            new("e_oemid / e_oeminfo", $"0x{d.OemId:X4} / 0x{d.OemInfo:X4}"),
            new("e_lfanew", $"0x{d.NewHeaderOffset:X8}", "offset of PE signature"),
        };

        var f = pe.FileHeader;
        File = new List<PropertyRow>
        {
            new("Machine", $"0x{(ushort)f.Machine:X4}", f.Machine.ToString()),
            new("NumberOfSections", f.NumberOfSections.ToString(CultureInfo.InvariantCulture)),
            new("TimeDateStamp", $"0x{f.TimeDateStamp:X8}", f.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)),
            new("PointerToSymbolTable", $"0x{f.PointerToSymbolTable:X8}"),
            new("NumberOfSymbols", f.NumberOfSymbols.ToString(CultureInfo.InvariantCulture)),
            new("SizeOfOptionalHeader", $"0x{f.SizeOfOptionalHeader:X4}"),
            new("Characteristics", $"0x{(ushort)f.Characteristics:X4}", f.Characteristics.ToString()),
        };

        var o = pe.OptionalHeader;
        Optional = new List<PropertyRow>
        {
            new("Magic", $"0x{(ushort)o.Magic:X4}", o.Magic.ToString()),
            new("LinkerVersion", $"{o.MajorLinkerVersion}.{o.MinorLinkerVersion}"),
            new("SizeOfCode", $"0x{o.SizeOfCode:X8}"),
            new("SizeOfInitializedData", $"0x{o.SizeOfInitializedData:X8}"),
            new("SizeOfUninitializedData", $"0x{o.SizeOfUninitializedData:X8}"),
            new("AddressOfEntryPoint", $"0x{o.AddressOfEntryPoint:X8}", pe.SectionFromRva(o.AddressOfEntryPoint)?.Name),
            new("BaseOfCode", $"0x{o.BaseOfCode:X8}"),
            new("BaseOfData", o.Is64Bit ? "(n/a)" : $"0x{o.BaseOfData:X8}"),
            new("ImageBase", $"0x{o.ImageBase:X}"),
            new("SectionAlignment", $"0x{o.SectionAlignment:X}"),
            new("FileAlignment", $"0x{o.FileAlignment:X}"),
            new("OperatingSystemVersion", $"{o.MajorOperatingSystemVersion}.{o.MinorOperatingSystemVersion}"),
            new("ImageVersion", $"{o.MajorImageVersion}.{o.MinorImageVersion}"),
            new("SubsystemVersion", $"{o.MajorSubsystemVersion}.{o.MinorSubsystemVersion}"),
            new("Win32VersionValue", $"0x{o.Win32VersionValue:X8}"),
            new("SizeOfImage", $"0x{o.SizeOfImage:X8}"),
            new("SizeOfHeaders", $"0x{o.SizeOfHeaders:X8}"),
            new("CheckSum", $"0x{o.CheckSum:X8}"),
            new("Subsystem", $"0x{(ushort)o.Subsystem:X4}", o.Subsystem.ToString()),
            new("DllCharacteristics", $"0x{(ushort)o.DllCharacteristics:X4}", o.DllCharacteristics.ToString()),
            new("SizeOfStackReserve", $"0x{o.SizeOfStackReserve:X}"),
            new("SizeOfStackCommit", $"0x{o.SizeOfStackCommit:X}"),
            new("SizeOfHeapReserve", $"0x{o.SizeOfHeapReserve:X}"),
            new("SizeOfHeapCommit", $"0x{o.SizeOfHeapCommit:X}"),
            new("LoaderFlags", $"0x{o.LoaderFlags:X8}"),
            new("NumberOfRvaAndSizes", o.NumberOfRvaAndSizes.ToString(CultureInfo.InvariantCulture)),
        };

        Directories = new List<DirectoryRow>();
        for (int i = 0; i < pe.DataDirectories.Count; i++)
        {
            var dir = pe.DataDirectories[i];
            string name = ((DataDirectoryIndex)i).ToString();
            string section = i == (int)DataDirectoryIndex.Security
                ? (dir.IsPresent ? "(file offset)" : string.Empty)
                : pe.SectionFromRva(dir.Rva)?.Name ?? (dir.IsPresent ? "?" : string.Empty);
            Directories.Add(new DirectoryRow(i, name, dir.IsPresent ? $"0x{dir.Rva:X8}" : "-", dir.IsPresent ? $"0x{dir.Size:X8}" : "-", section));
        }
    }

    public List<PropertyRow> Dos { get; }
    public List<PropertyRow> File { get; }
    public List<PropertyRow> Optional { get; }
    public List<DirectoryRow> Directories { get; }

    private static string Hex(ushort v) => $"0x{v:X4}";
}

public sealed record DirectoryRow(int Index, string Name, string Rva, string Size, string Section);
