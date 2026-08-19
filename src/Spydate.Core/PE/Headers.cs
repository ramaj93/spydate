namespace Spydate.Core.PE;

/// <summary>IMAGE_DOS_HEADER (only the fields that matter for PE navigation are kept).</summary>
public readonly record struct DosHeader(
    ushort Magic,
    ushort BytesOnLastPage,
    ushort PagesInFile,
    ushort Relocations,
    ushort HeaderParagraphs,
    ushort MinExtraParagraphs,
    ushort MaxExtraParagraphs,
    ushort InitialSs,
    ushort InitialSp,
    ushort Checksum,
    ushort InitialIp,
    ushort InitialCs,
    ushort RelocationTableOffset,
    ushort OverlayNumber,
    ushort OemId,
    ushort OemInfo,
    uint NewHeaderOffset)
{
    public const ushort ExpectedMagic = 0x5A4D; // 'MZ'
    public const int Size = 64;
}

/// <summary>IMAGE_FILE_HEADER.</summary>
public readonly record struct CoffFileHeader(
    MachineType Machine,
    ushort NumberOfSections,
    uint TimeDateStamp,
    uint PointerToSymbolTable,
    uint NumberOfSymbols,
    ushort SizeOfOptionalHeader,
    ImageCharacteristics Characteristics)
{
    public const int Size = 20;

    public DateTimeOffset? Timestamp =>
        TimeDateStamp is 0 or 0xFFFFFFFF ? null : DateTimeOffset.FromUnixTimeSeconds(TimeDateStamp);

    public bool IsDll => Characteristics.HasFlag(ImageCharacteristics.Dll);
}

/// <summary>Unified IMAGE_OPTIONAL_HEADER32 / IMAGE_OPTIONAL_HEADER64.</summary>
public sealed record OptionalHeader
{
    public required OptionalHeaderMagic Magic { get; init; }
    public bool Is64Bit => Magic == OptionalHeaderMagic.Pe32Plus;
    public required byte MajorLinkerVersion { get; init; }
    public required byte MinorLinkerVersion { get; init; }
    public required uint SizeOfCode { get; init; }
    public required uint SizeOfInitializedData { get; init; }
    public required uint SizeOfUninitializedData { get; init; }
    public required uint AddressOfEntryPoint { get; init; }
    public required uint BaseOfCode { get; init; }
    /// <summary>PE32 only; 0 for PE32+.</summary>
    public required uint BaseOfData { get; init; }
    public required ulong ImageBase { get; init; }
    public required uint SectionAlignment { get; init; }
    public required uint FileAlignment { get; init; }
    public required ushort MajorOperatingSystemVersion { get; init; }
    public required ushort MinorOperatingSystemVersion { get; init; }
    public required ushort MajorImageVersion { get; init; }
    public required ushort MinorImageVersion { get; init; }
    public required ushort MajorSubsystemVersion { get; init; }
    public required ushort MinorSubsystemVersion { get; init; }
    public required uint Win32VersionValue { get; init; }
    public required uint SizeOfImage { get; init; }
    public required uint SizeOfHeaders { get; init; }
    public required uint CheckSum { get; init; }
    public required Subsystem Subsystem { get; init; }
    public required DllCharacteristics DllCharacteristics { get; init; }
    public required ulong SizeOfStackReserve { get; init; }
    public required ulong SizeOfStackCommit { get; init; }
    public required ulong SizeOfHeapReserve { get; init; }
    public required ulong SizeOfHeapCommit { get; init; }
    public required uint LoaderFlags { get; init; }
    public required uint NumberOfRvaAndSizes { get; init; }
}

/// <summary>One IMAGE_DATA_DIRECTORY entry.</summary>
public readonly record struct DataDirectory(uint Rva, uint Size)
{
    public bool IsPresent => Rva != 0 && Size != 0;

    public bool Contains(uint rva) => IsPresent && rva >= Rva && rva < Rva + Size;
}

/// <summary>IMAGE_SECTION_HEADER.</summary>
public sealed record SectionHeader
{
    public const int Size = 40;

    public required int Index { get; init; }
    public required string Name { get; init; }
    public required uint VirtualSize { get; init; }
    public required uint VirtualAddress { get; init; }
    public required uint SizeOfRawData { get; init; }
    public required uint PointerToRawData { get; init; }
    public required uint PointerToRelocations { get; init; }
    public required uint PointerToLinenumbers { get; init; }
    public required ushort NumberOfRelocations { get; init; }
    public required ushort NumberOfLinenumbers { get; init; }
    public required SectionCharacteristics Characteristics { get; init; }

    /// <summary>Extent of the section in the virtual address space (VirtualSize, or raw size if VirtualSize is 0).</summary>
    public uint VirtualExtent => Math.Max(VirtualSize, SizeOfRawData);

    public uint EndRva => VirtualAddress + VirtualExtent;

    public bool IsExecutable => Characteristics.HasFlag(SectionCharacteristics.MemExecute)
                                || Characteristics.HasFlag(SectionCharacteristics.ContainsCode);

    public bool IsReadable => Characteristics.HasFlag(SectionCharacteristics.MemRead);

    public bool IsWritable => Characteristics.HasFlag(SectionCharacteristics.MemWrite);

    public bool ContainsRva(uint rva) => rva >= VirtualAddress && rva < EndRva;

    /// <summary>Compact "RWX"-style permission string, e.g. "R-X".</summary>
    public string Permissions =>
        string.Create(3, this, static (span, s) =>
        {
            span[0] = s.IsReadable ? 'R' : '-';
            span[1] = s.IsWritable ? 'W' : '-';
            span[2] = s.Characteristics.HasFlag(SectionCharacteristics.MemExecute) ? 'X' : '-';
        });

    public override string ToString() => $"{Name} rva=0x{VirtualAddress:X} vsize=0x{VirtualSize:X} raw=0x{PointerToRawData:X}+0x{SizeOfRawData:X} {Permissions}";
}
