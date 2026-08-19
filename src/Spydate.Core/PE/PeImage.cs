using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using Spydate.Core.Binary;

namespace Spydate.Core.PE;

/// <summary>
/// Immutable, fully parsed view of a PE (Portable Executable) file held in memory.
/// Construction is bounds-checked; fatal structural problems throw <see cref="PeParseException"/>,
/// non-fatal problems (a corrupt import table, for example) are recorded in <see cref="Warnings"/>.
/// </summary>
public sealed class PeImage
{
    private const uint PeSignature = 0x00004550; // "PE\0\0"
    private const int MaxSections = 96;          // loader limit
    private const int MaxImportModules = 4096;
    private const int MaxImportFunctions = 65536;
    private const int MaxExports = 65536;
    private const int MaxDebugEntries = 64;
    private const int MaxRelocationBlocks = 65536;
    private const int MaxRelocationsPerBlock = 4096;
    private const int MaxTlsCallbacks = 1024;
    private const int MaxGuardFunctions = 1_000_000;
    private const int MaxResourceNodes = 65536;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly List<string> _warnings = new();

    private PeImage(ReadOnlyMemory<byte> data, string? path)
    {
        _data = data;
        Path = path;
        FileName = path is null ? "<memory>" : System.IO.Path.GetFileName(path);

        var span = data.Span;
        DosHeader = ParseDosHeader(span);
        int ntOffset = checked((int)DosHeader.NewHeaderOffset);
        if (ntOffset < DosHeader.Size || ntOffset + 4 + CoffFileHeader.Size > span.Length)
        {
            throw new PeParseException($"e_lfanew (0x{ntOffset:X}) points outside the file.");
        }

        var r = new SpanReader(span, ntOffset);
        if (r.ReadU32() != PeSignature)
        {
            throw new PeParseException("Missing PE\\0\\0 signature.");
        }

        FileHeader = ParseFileHeader(ref r);
        int optionalHeaderOffset = r.Position;
        (OptionalHeader, DataDirectories) = ParseOptionalHeader(ref r, FileHeader.SizeOfOptionalHeader);
        int sectionTableOffset = optionalHeaderOffset + FileHeader.SizeOfOptionalHeader;
        Sections = ParseSectionTable(span, sectionTableOffset, FileHeader.NumberOfSections);
        SectionTableOffset = (uint)sectionTableOffset;
        NtHeadersOffset = (uint)ntOffset;

        // Directories - each guarded independently.
        Imports = Guard(ParseImports, "import table", Array.Empty<ImportedModule>());
        DelayImports = Guard(ParseDelayImports, "delay-import table", Array.Empty<ImportedModule>());
        Exports = Guard(ParseExports, "export table", null);
        ClrHeader = Guard(ParseClrHeader, "CLR header", null);
        Debug = Guard(ParseDebug, "debug directory", Array.Empty<DebugEntry>());
        ExceptionTable = Guard(ParseExceptionTable, "exception directory", Array.Empty<RuntimeFunction>());
        Relocations = Guard(ParseRelocations, "base relocation table", Array.Empty<RelocationBlock>());
        Tls = Guard(ParseTls, "TLS directory", null);
        LoadConfig = Guard(ParseLoadConfig, "load config directory", null);
        Resources = Guard(ParseResources, "resource directory", null);
        RichHeader = Guard(ParseRichHeader, "Rich header", null);
        Signature = Guard(ParseSignature, "certificate table", null);

        Overlay = ComputeOverlay();
    }

    // ---------------------------------------------------------------------
    // Construction
    // ---------------------------------------------------------------------

    /// <summary>Loads and parses a PE file from disk.</summary>
    public static PeImage Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PeParseException($"Cannot read '{path}': {ex.Message}", ex);
        }

        return new PeImage(bytes, path);
    }

    /// <summary>Parses a PE image from an in-memory buffer.</summary>
    public static PeImage Parse(ReadOnlyMemory<byte> data, string? path = null) => new(data, path);

    /// <summary>Returns true if the buffer starts with an MZ header and has a PE signature at e_lfanew.</summary>
    public static bool LooksLikePe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x40 || data[0] != (byte)'M' || data[1] != (byte)'Z')
        {
            return false;
        }

        uint lfanew = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x3C, 4));
        return lfanew + 4 <= (uint)data.Length
               && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)lfanew, 4)) == PeSignature;
    }

    // ---------------------------------------------------------------------
    // Public surface
    // ---------------------------------------------------------------------

    public string? Path { get; }
    public string FileName { get; }
    public ReadOnlyMemory<byte> Data => _data;
    public long Length => _data.Length;

    public DosHeader DosHeader { get; }
    public uint NtHeadersOffset { get; }
    public uint SectionTableOffset { get; }
    public CoffFileHeader FileHeader { get; }
    public OptionalHeader OptionalHeader { get; }
    public IReadOnlyList<DataDirectory> DataDirectories { get; }
    public IReadOnlyList<SectionHeader> Sections { get; }
    public IReadOnlyList<ImportedModule> Imports { get; }
    public IReadOnlyList<ImportedModule> DelayImports { get; }
    public ExportTable? Exports { get; }
    public ClrHeader? ClrHeader { get; }
    public IReadOnlyList<DebugEntry> Debug { get; }
    /// <summary>x64 unwind table (RUNTIME_FUNCTION entries); empty for x86 or when absent.</summary>
    public IReadOnlyList<RuntimeFunction> ExceptionTable { get; }
    /// <summary>Base relocation blocks; empty when the image is not relocatable.</summary>
    public IReadOnlyList<RelocationBlock> Relocations { get; }
    /// <summary>TLS directory, including callbacks that run before the entry point.</summary>
    public TlsDirectory? Tls { get; }
    /// <summary>Load config: security cookie, SafeSEH and Control Flow Guard tables.</summary>
    public LoadConfig? LoadConfig { get; }
    /// <summary>Root of the resource tree (type -> name -> language), or null when absent.</summary>
    public ResourceNode? Resources { get; }
    /// <summary>Microsoft linker build stamp hidden in the DOS stub, or null.</summary>
    public RichHeader? RichHeader { get; }
    /// <summary>Embedded code signature, described but not verified. Null when unsigned.</summary>
    public AuthenticodeSignature? Signature { get; }
    public IReadOnlyList<string> Warnings => new ReadOnlyCollection<string>(_warnings);

    /// <summary>File offset and length of any data past the last section (0,0 if none).</summary>
    public (uint Offset, uint Length) Overlay { get; }

    public bool Is64Bit => OptionalHeader.Is64Bit;
    public MachineType Machine => FileHeader.Machine;
    public ulong ImageBase => OptionalHeader.ImageBase;
    public uint EntryPointRva => OptionalHeader.AddressOfEntryPoint;
    public ulong EntryPointVa => ImageBase + EntryPointRva;
    public Subsystem Subsystem => OptionalHeader.Subsystem;
    public bool IsDll => FileHeader.IsDll;
    public bool IsManaged => ClrHeader is not null;
    /// <summary>Total number of base relocations across all blocks.</summary>
    public int RelocationCount => Relocations.Sum(b => b.Entries.Count);
    /// <summary>Bit width of code addresses for disassembly (32 or 64). Falls back to the optional header magic.</summary>
    public int Bitness => Machine switch
    {
        MachineType.Amd64 or MachineType.Arm64 or MachineType.Ia64 or MachineType.Arm64Ec or MachineType.Arm64X => 64,
        MachineType.I386 or MachineType.Arm or MachineType.ArmNt or MachineType.Thumb => 32,
        _ => Is64Bit ? 64 : 32,
    };

    /// <summary>Whether the machine type is one Spydate can disassemble natively (x86 / x64).</summary>
    public bool IsX86Family => Machine is MachineType.I386 or MachineType.Amd64;

    public DataDirectory GetDirectory(DataDirectoryIndex index)
    {
        int i = (int)index;
        return i < DataDirectories.Count ? DataDirectories[i] : default;
    }

    // ---------------------------------------------------------------------
    // Address translation
    // ---------------------------------------------------------------------

    public SectionHeader? SectionFromRva(uint rva)
    {
        foreach (var s in Sections)
        {
            if (s.ContainsRva(rva))
            {
                return s;
            }
        }

        return null;
    }

    public SectionHeader? SectionFromVa(ulong va) => VaToRva(va) is { } rva ? SectionFromRva(rva) : null;

    /// <summary>Converts an RVA to a file offset, or null when the RVA is not backed by file data.</summary>
    public uint? RvaToOffset(uint rva)
    {
        // Headers map 1:1.
        if (rva < OptionalHeader.SizeOfHeaders && rva < _data.Length)
        {
            return rva;
        }

        var s = SectionFromRva(rva);
        if (s is null)
        {
            return null;
        }

        uint delta = rva - s.VirtualAddress;
        if (delta >= s.SizeOfRawData)
        {
            return null; // in the zero-filled tail (VirtualSize > SizeOfRawData)
        }

        ulong offset = (ulong)s.PointerToRawData + delta;
        return offset < (ulong)_data.Length ? (uint)offset : null;
    }

    public uint? OffsetToRva(uint offset)
    {
        if (offset < OptionalHeader.SizeOfHeaders)
        {
            return offset;
        }

        foreach (var s in Sections)
        {
            if (offset >= s.PointerToRawData && offset < s.PointerToRawData + s.SizeOfRawData)
            {
                return s.VirtualAddress + (offset - s.PointerToRawData);
            }
        }

        return null;
    }

    public ulong RvaToVa(uint rva) => ImageBase + rva;

    public uint? VaToRva(ulong va)
    {
        if (va < ImageBase)
        {
            return null;
        }

        ulong rva = va - ImageBase;
        return rva <= uint.MaxValue ? (uint)rva : null;
    }

    public uint? VaToOffset(ulong va) => VaToRva(va) is { } rva ? RvaToOffset(rva) : null;

    /// <summary>
    /// Reads up to <paramref name="length"/> bytes at an RVA. Returns an empty span when the RVA is not file-backed;
    /// the returned span may be shorter than requested when it hits the end of the section's raw data.
    /// </summary>
    public ReadOnlyMemory<byte> ReadAtRva(uint rva, int length)
    {
        if (length <= 0 || RvaToOffset(rva) is not { } offset)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        int available = _data.Length - (int)offset;
        var s = SectionFromRva(rva);
        if (s is not null)
        {
            long sectionRemaining = (long)s.PointerToRawData + s.SizeOfRawData - offset;
            available = (int)Math.Min(available, Math.Max(0, sectionRemaining));
        }

        return _data.Slice((int)offset, Math.Min(length, available));
    }

    public ReadOnlyMemory<byte> ReadAtVa(ulong va, int length) => VaToRva(va) is { } rva ? ReadAtRva(rva, length) : ReadOnlyMemory<byte>.Empty;

    /// <summary>Reads a NUL-terminated ASCII string at an RVA (empty if unmapped).</summary>
    public string ReadAsciiZAtRva(uint rva, int max = 4096)
        => RvaToOffset(rva) is { } offset ? SpanReader.ReadAsciiZAt(_data.Span, (int)offset, max) : string.Empty;

    /// <summary>Reads a pointer-sized value (4/8 bytes) at an RVA, or null if unmapped.</summary>
    public ulong? ReadPointerAtRva(uint rva)
    {
        int size = Is64Bit ? 8 : 4;
        var mem = ReadAtRva(rva, size);
        if (mem.Length < size)
        {
            return null;
        }

        var r = new SpanReader(mem.Span);
        return r.ReadPointer(Is64Bit);
    }

    // ---------------------------------------------------------------------
    // Parsing helpers
    // ---------------------------------------------------------------------

    private T Guard<T>(Func<T> parse, string what, T fallback)
    {
        try
        {
            return parse();
        }
        catch (PeParseException ex)
        {
            _warnings.Add($"Could not parse {what}: {ex.Message}");
            return fallback;
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException or InvalidOperationException)
        {
            _warnings.Add($"Could not parse {what}: {ex.Message}");
            return fallback;
        }
    }

    private static DosHeader ParseDosHeader(ReadOnlySpan<byte> span)
    {
        if (span.Length < DosHeader.Size)
        {
            throw new PeParseException("File is too small to contain a DOS header.");
        }

        var r = new SpanReader(span);
        ushort magic = r.ReadU16();
        if (magic != DosHeader.ExpectedMagic)
        {
            throw new PeParseException("Missing MZ signature - not a PE file.");
        }

        var h = new DosHeader(
            Magic: magic,
            BytesOnLastPage: r.ReadU16(),
            PagesInFile: r.ReadU16(),
            Relocations: r.ReadU16(),
            HeaderParagraphs: r.ReadU16(),
            MinExtraParagraphs: r.ReadU16(),
            MaxExtraParagraphs: r.ReadU16(),
            InitialSs: r.ReadU16(),
            InitialSp: r.ReadU16(),
            Checksum: r.ReadU16(),
            InitialIp: r.ReadU16(),
            InitialCs: r.ReadU16(),
            RelocationTableOffset: r.ReadU16(),
            OverlayNumber: r.ReadU16(),
            OemId: 0,
            OemInfo: 0,
            NewHeaderOffset: 0);
        r.Skip(8);               // e_res[4]
        ushort oemId = r.ReadU16();
        ushort oemInfo = r.ReadU16();
        r.Skip(20);              // e_res2[10]
        uint lfanew = r.ReadU32();
        return h with { OemId = oemId, OemInfo = oemInfo, NewHeaderOffset = lfanew };
    }

    private static CoffFileHeader ParseFileHeader(ref SpanReader r) => new(
        Machine: (MachineType)r.ReadU16(),
        NumberOfSections: r.ReadU16(),
        TimeDateStamp: r.ReadU32(),
        PointerToSymbolTable: r.ReadU32(),
        NumberOfSymbols: r.ReadU32(),
        SizeOfOptionalHeader: r.ReadU16(),
        Characteristics: (ImageCharacteristics)r.ReadU16());

    private static (OptionalHeader, IReadOnlyList<DataDirectory>) ParseOptionalHeader(ref SpanReader r, ushort sizeOfOptionalHeader)
    {
        int start = r.Position;
        if (sizeOfOptionalHeader < 2 || !r.CanRead(sizeOfOptionalHeader))
        {
            throw new PeParseException($"Optional header (size 0x{sizeOfOptionalHeader:X}) exceeds file bounds.");
        }

        var magic = (OptionalHeaderMagic)r.ReadU16();
        bool is64 = magic switch
        {
            OptionalHeaderMagic.Pe32 => false,
            OptionalHeaderMagic.Pe32Plus => true,
            _ => throw new PeParseException($"Unsupported optional header magic 0x{(ushort)magic:X}."),
        };

        int minimum = is64 ? 112 : 96;
        if (sizeOfOptionalHeader < minimum)
        {
            throw new PeParseException($"Optional header too small ({sizeOfOptionalHeader} bytes) for {(is64 ? "PE32+" : "PE32")}.");
        }

        byte majorLinker = r.ReadU8();
        byte minorLinker = r.ReadU8();
        uint sizeOfCode = r.ReadU32();
        uint sizeOfInit = r.ReadU32();
        uint sizeOfUninit = r.ReadU32();
        uint entry = r.ReadU32();
        uint baseOfCode = r.ReadU32();
        uint baseOfData = is64 ? 0 : r.ReadU32();
        ulong imageBase = r.ReadPointer(is64);
        uint sectionAlign = r.ReadU32();
        uint fileAlign = r.ReadU32();
        ushort majorOs = r.ReadU16();
        ushort minorOs = r.ReadU16();
        ushort majorImage = r.ReadU16();
        ushort minorImage = r.ReadU16();
        ushort majorSubsystem = r.ReadU16();
        ushort minorSubsystem = r.ReadU16();
        uint win32Version = r.ReadU32();
        uint sizeOfImage = r.ReadU32();
        uint sizeOfHeaders = r.ReadU32();
        uint checksum = r.ReadU32();
        var subsystem = (Subsystem)r.ReadU16();
        var dllChars = (DllCharacteristics)r.ReadU16();
        ulong stackReserve = r.ReadPointer(is64);
        ulong stackCommit = r.ReadPointer(is64);
        ulong heapReserve = r.ReadPointer(is64);
        ulong heapCommit = r.ReadPointer(is64);
        uint loaderFlags = r.ReadU32();
        uint numDirs = r.ReadU32();

        var header = new OptionalHeader
        {
            Magic = magic,
            MajorLinkerVersion = majorLinker,
            MinorLinkerVersion = minorLinker,
            SizeOfCode = sizeOfCode,
            SizeOfInitializedData = sizeOfInit,
            SizeOfUninitializedData = sizeOfUninit,
            AddressOfEntryPoint = entry,
            BaseOfCode = baseOfCode,
            BaseOfData = baseOfData,
            ImageBase = imageBase,
            SectionAlignment = sectionAlign,
            FileAlignment = fileAlign,
            MajorOperatingSystemVersion = majorOs,
            MinorOperatingSystemVersion = minorOs,
            MajorImageVersion = majorImage,
            MinorImageVersion = minorImage,
            MajorSubsystemVersion = majorSubsystem,
            MinorSubsystemVersion = minorSubsystem,
            Win32VersionValue = win32Version,
            SizeOfImage = sizeOfImage,
            SizeOfHeaders = sizeOfHeaders,
            CheckSum = checksum,
            Subsystem = subsystem,
            DllCharacteristics = dllChars,
            SizeOfStackReserve = stackReserve,
            SizeOfStackCommit = stackCommit,
            SizeOfHeapReserve = heapReserve,
            SizeOfHeapCommit = heapCommit,
            LoaderFlags = loaderFlags,
            NumberOfRvaAndSizes = numDirs,
        };

        // Data directories: read as many as both the count and the header size allow, cap at 16.
        int available = (start + sizeOfOptionalHeader - r.Position) / 8;
        int count = (int)Math.Min(Math.Min(numDirs, 16u), (uint)Math.Max(available, 0));
        var dirs = new DataDirectory[16];
        for (int i = 0; i < count; i++)
        {
            dirs[i] = new DataDirectory(r.ReadU32(), r.ReadU32());
        }

        return (header, dirs);
    }

    private static IReadOnlyList<SectionHeader> ParseSectionTable(ReadOnlySpan<byte> span, int offset, int count)
    {
        if (count > MaxSections)
        {
            throw new PeParseException($"NumberOfSections ({count}) exceeds the loader limit of {MaxSections}.");
        }

        var list = new List<SectionHeader>(count);
        var r = new SpanReader(span, offset);
        for (int i = 0; i < count; i++)
        {
            if (!r.CanRead(SectionHeader.Size))
            {
                throw new PeParseException($"Section table entry {i} is truncated.");
            }

            list.Add(new SectionHeader
            {
                Index = i,
                Name = r.ReadFixedAscii(8),
                VirtualSize = r.ReadU32(),
                VirtualAddress = r.ReadU32(),
                SizeOfRawData = r.ReadU32(),
                PointerToRawData = r.ReadU32(),
                PointerToRelocations = r.ReadU32(),
                PointerToLinenumbers = r.ReadU32(),
                NumberOfRelocations = r.ReadU16(),
                NumberOfLinenumbers = r.ReadU16(),
                Characteristics = (SectionCharacteristics)r.ReadU32(),
            });
        }

        return list;
    }

    private int RequireOffset(uint rva, string what)
    {
        return RvaToOffset(rva) is { } o
            ? (int)o
            : throw new PeParseException($"{what} RVA 0x{rva:X} is not mapped to file data.");
    }

    private IReadOnlyList<ImportedModule> ParseImports()
    {
        var span = _data.Span;
        var dir = GetDirectory(DataDirectoryIndex.Import);
        if (!dir.IsPresent)
        {
            return Array.Empty<ImportedModule>();
        }

        var modules = new List<ImportedModule>();
        var r = new SpanReader(span, RequireOffset(dir.Rva, "Import directory"));
        for (int i = 0; i < MaxImportModules; i++)
        {
            if (!r.CanRead(20))
            {
                _warnings.Add("Import descriptor table is truncated (missing terminator).");
                break;
            }

            uint originalFirstThunk = r.ReadU32();
            uint timeDateStamp = r.ReadU32();
            uint forwarderChain = r.ReadU32();
            uint nameRva = r.ReadU32();
            uint firstThunk = r.ReadU32();
            if (originalFirstThunk == 0 && nameRva == 0 && firstThunk == 0)
            {
                break; // terminator
            }

            string name = ReadAsciiZAtRva(nameRva, 260);
            uint thunkRva = originalFirstThunk != 0 ? originalFirstThunk : firstThunk;
            var functions = ReadThunks(span, thunkRva, firstThunk, delayLoad: false);
            modules.Add(new ImportedModule
            {
                Name = string.IsNullOrEmpty(name) ? $"<unnamed@0x{nameRva:X}>" : name,
                Functions = functions,
                TimeDateStamp = timeDateStamp,
                ForwarderChain = forwarderChain,
                OriginalFirstThunkRva = originalFirstThunk,
                FirstThunkRva = firstThunk,
            });
        }

        return modules;
    }

    private IReadOnlyList<ImportedModule> ParseDelayImports()
    {
        var span = _data.Span;
        var dir = GetDirectory(DataDirectoryIndex.DelayImport);
        if (!dir.IsPresent)
        {
            return Array.Empty<ImportedModule>();
        }

        var modules = new List<ImportedModule>();
        var r = new SpanReader(span, RequireOffset(dir.Rva, "Delay-import directory"));
        for (int i = 0; i < MaxImportModules; i++)
        {
            if (!r.CanRead(32))
            {
                _warnings.Add("Delay-import descriptor table is truncated (missing terminator).");
                break;
            }

            uint attributes = r.ReadU32();
            uint nameRva = r.ReadU32();
            uint moduleHandleRva = r.ReadU32();
            uint iatRva = r.ReadU32();
            uint intRva = r.ReadU32();
            uint boundIatRva = r.ReadU32();
            uint unloadIatRva = r.ReadU32();
            uint timeDateStamp = r.ReadU32();
            _ = moduleHandleRva;
            _ = boundIatRva;
            _ = unloadIatRva;
            if (nameRva == 0 && iatRva == 0 && intRva == 0)
            {
                break;
            }

            // Attributes bit 0 == 1 -> fields are RVAs; otherwise legacy VAs.
            bool rvaBased = (attributes & 1) != 0;
            if (!rvaBased)
            {
                nameRva = VaToRva(nameRva) ?? nameRva;
                iatRva = VaToRva(iatRva) ?? iatRva;
                intRva = VaToRva(intRva) ?? intRva;
            }

            string name = ReadAsciiZAtRva(nameRva, 260);
            var functions = ReadThunks(span, intRva != 0 ? intRva : iatRva, iatRva, delayLoad: true);
            modules.Add(new ImportedModule
            {
                Name = string.IsNullOrEmpty(name) ? $"<unnamed@0x{nameRva:X}>" : name,
                Functions = functions,
                TimeDateStamp = timeDateStamp,
                OriginalFirstThunkRva = intRva,
                FirstThunkRva = iatRva,
                IsDelayLoad = true,
            });
        }

        return modules;
    }

    private List<ImportedFunction> ReadThunks(ReadOnlySpan<byte> span, uint thunkRva, uint iatRva, bool delayLoad)
    {
        var list = new List<ImportedFunction>();
        if (RvaToOffset(thunkRva) is not { } offset)
        {
            _warnings.Add($"Import thunk table RVA 0x{thunkRva:X} is not mapped.");
            return list;
        }

        int thunkSize = Is64Bit ? 8 : 4;
        ulong ordinalFlag = Is64Bit ? 0x8000000000000000UL : 0x80000000UL;
        var r = new SpanReader(span, (int)offset);
        for (int i = 0; i < MaxImportFunctions; i++)
        {
            if (!r.CanRead(thunkSize))
            {
                _warnings.Add($"Import thunk table at RVA 0x{thunkRva:X} is truncated.");
                break;
            }

            ulong thunk = r.ReadPointer(Is64Bit);
            if (thunk == 0)
            {
                break;
            }

            uint slotRva = iatRva + (uint)(i * thunkSize);
            if ((thunk & ordinalFlag) != 0)
            {
                list.Add(new ImportedFunction { Ordinal = (ushort)(thunk & 0xFFFF), IatRva = slotRva, IsDelayLoad = delayLoad });
            }
            else
            {
                uint hintNameRva = (uint)(thunk & 0x7FFFFFFF);
                if (RvaToOffset(hintNameRva) is { } hn && hn + 2 < span.Length)
                {
                    var hr = new SpanReader(span, (int)hn);
                    ushort hint = hr.ReadU16();
                    string fname = hr.ReadAsciiZ(1024);
                    list.Add(new ImportedFunction { Name = fname, Hint = hint, IatRva = slotRva, IsDelayLoad = delayLoad });
                }
                else
                {
                    list.Add(new ImportedFunction { Name = $"<bad name rva 0x{hintNameRva:X}>", IatRva = slotRva, IsDelayLoad = delayLoad });
                }
            }
        }

        return list;
    }

    private ExportTable? ParseExports()
    {
        var span = _data.Span;
        var dir = GetDirectory(DataDirectoryIndex.Export);
        if (!dir.IsPresent)
        {
            return null;
        }

        var r = new SpanReader(span, RequireOffset(dir.Rva, "Export directory"));
        if (!r.CanRead(40))
        {
            throw new PeParseException("Export directory is truncated.");
        }

        r.Skip(4); // Characteristics
        uint timeDateStamp = r.ReadU32();
        ushort major = r.ReadU16();
        ushort minor = r.ReadU16();
        uint nameRva = r.ReadU32();
        uint ordinalBase = r.ReadU32();
        uint numberOfFunctions = r.ReadU32();
        uint numberOfNames = r.ReadU32();
        uint addressOfFunctions = r.ReadU32();
        uint addressOfNames = r.ReadU32();
        uint addressOfNameOrdinals = r.ReadU32();

        if (numberOfFunctions > MaxExports || numberOfNames > MaxExports)
        {
            throw new PeParseException($"Export counts are implausible (functions={numberOfFunctions}, names={numberOfNames}).");
        }

        // Ordinal -> name map.
        var names = new Dictionary<uint, string>();
        if (numberOfNames > 0 && addressOfNames != 0 && addressOfNameOrdinals != 0)
        {
            int namesOffset = RequireOffset(addressOfNames, "Export name pointer table");
            int ordsOffset = RequireOffset(addressOfNameOrdinals, "Export ordinal table");
            var nr = new SpanReader(span, namesOffset);
            var or = new SpanReader(span, ordsOffset);
            for (uint i = 0; i < numberOfNames; i++)
            {
                if (!nr.CanRead(4) || !or.CanRead(2))
                {
                    _warnings.Add("Export name/ordinal tables are truncated.");
                    break;
                }

                uint fnNameRva = nr.ReadU32();
                ushort ordIndex = or.ReadU16();
                names.TryAdd(ordIndex, ReadAsciiZAtRva(fnNameRva, 2048));
            }
        }

        var entries = new List<ExportedFunction>((int)numberOfFunctions);
        if (numberOfFunctions > 0 && addressOfFunctions != 0)
        {
            var fr = new SpanReader(span, RequireOffset(addressOfFunctions, "Export address table"));
            for (uint i = 0; i < numberOfFunctions; i++)
            {
                if (!fr.CanRead(4))
                {
                    _warnings.Add("Export address table is truncated.");
                    break;
                }

                uint fnRva = fr.ReadU32();
                if (fnRva == 0)
                {
                    continue; // unused ordinal slot
                }

                names.TryGetValue(i, out string? fnName);
                string? forwarder = dir.Contains(fnRva) ? ReadAsciiZAtRva(fnRva, 512) : null;
                entries.Add(new ExportedFunction
                {
                    Name = fnName,
                    Ordinal = ordinalBase + i,
                    Rva = forwarder is null ? fnRva : 0,
                    ForwarderName = forwarder,
                });
            }
        }

        return new ExportTable
        {
            Name = ReadAsciiZAtRva(nameRva, 260),
            Base = ordinalBase,
            TimeDateStamp = timeDateStamp,
            MajorVersion = major,
            MinorVersion = minor,
            NumberOfFunctions = numberOfFunctions,
            NumberOfNames = numberOfNames,
            Entries = entries,
        };
    }

    private ClrHeader? ParseClrHeader()
    {
        var span = _data.Span;
        var dir = GetDirectory(DataDirectoryIndex.ClrRuntimeHeader);
        if (!dir.IsPresent)
        {
            return null;
        }

        var r = new SpanReader(span, RequireOffset(dir.Rva, "CLR header"));
        if (!r.CanRead(72))
        {
            throw new PeParseException("CLR header is truncated.");
        }

        DataDirectory ReadDir(ref SpanReader rr) => new(rr.ReadU32(), rr.ReadU32());

        uint cb = r.ReadU32();
        ushort major = r.ReadU16();
        ushort minor = r.ReadU16();
        var metadata = ReadDir(ref r);
        var flags = (CorFlags)r.ReadU32();
        uint entry = r.ReadU32();
        var resources = ReadDir(ref r);
        var strongName = ReadDir(ref r);
        var codeManager = ReadDir(ref r);
        var vtableFixups = ReadDir(ref r);
        var eatJumps = ReadDir(ref r);
        var managedNative = ReadDir(ref r);

        return new ClrHeader
        {
            Cb = cb,
            MajorRuntimeVersion = major,
            MinorRuntimeVersion = minor,
            MetaData = metadata,
            Flags = flags,
            EntryPointTokenOrRva = entry,
            Resources = resources,
            StrongNameSignature = strongName,
            CodeManagerTable = codeManager,
            VTableFixups = vtableFixups,
            ExportAddressTableJumps = eatJumps,
            ManagedNativeHeader = managedNative,
        };
    }

    private IReadOnlyList<DebugEntry> ParseDebug()
    {
        var span = _data.Span;
        var dir = GetDirectory(DataDirectoryIndex.Debug);
        if (!dir.IsPresent)
        {
            return Array.Empty<DebugEntry>();
        }

        int count = (int)Math.Min(dir.Size / 28, MaxDebugEntries);
        var list = new List<DebugEntry>(count);
        var r = new SpanReader(span, RequireOffset(dir.Rva, "Debug directory"));
        for (int i = 0; i < count; i++)
        {
            if (!r.CanRead(28))
            {
                _warnings.Add("Debug directory is truncated.");
                break;
            }

            uint characteristics = r.ReadU32();
            uint timeDateStamp = r.ReadU32();
            ushort major = r.ReadU16();
            ushort minor = r.ReadU16();
            var type = (DebugDirectoryType)r.ReadU32();
            uint sizeOfData = r.ReadU32();
            uint addressOfRawData = r.ReadU32();
            uint pointerToRawData = r.ReadU32();

            CodeViewInfo? cv = null;
            if (type == DebugDirectoryType.CodeView && sizeOfData >= 24)
            {
                // Prefer the file pointer; fall back to RVA mapping.
                long dataOffset = pointerToRawData != 0 && pointerToRawData + sizeOfData <= span.Length
                    ? pointerToRawData
                    : (long?)RvaToOffset(addressOfRawData) ?? -1;
                if (dataOffset >= 0 && dataOffset + 24 <= span.Length)
                {
                    var cr = new SpanReader(span, (int)dataOffset);
                    string sig = cr.ReadFixedAscii(4);
                    if (sig == "RSDS")
                    {
                        var guid = cr.ReadGuid();
                        uint age = cr.ReadU32();
                        string path = cr.ReadAsciiZ((int)Math.Min(sizeOfData, 4096));
                        cv = new CodeViewInfo(sig, guid, age, path);
                    }
                    else
                    {
                        _warnings.Add($"Unsupported CodeView signature '{sig}'.");
                    }
                }
            }

            list.Add(new DebugEntry
            {
                Characteristics = characteristics,
                TimeDateStamp = timeDateStamp,
                MajorVersion = major,
                MinorVersion = minor,
                Type = type,
                SizeOfData = sizeOfData,
                AddressOfRawData = addressOfRawData,
                PointerToRawData = pointerToRawData,
                CodeView = cv,
            });
        }

        return list;
    }

    private IReadOnlyList<RuntimeFunction> ParseExceptionTable()
    {
        var dir = GetDirectory(DataDirectoryIndex.Exception);
        if (!dir.IsPresent)
        {
            return Array.Empty<RuntimeFunction>();
        }

        return Machine switch
        {
            MachineType.Amd64 => ParseX64ExceptionTable(dir),
            MachineType.Arm64 or MachineType.Arm64Ec or MachineType.Arm64X => ParseArm64ExceptionTable(dir),
            _ => Array.Empty<RuntimeFunction>(),
        };
    }

    /// <summary>
    /// ARM64 .pdata: BeginAddress plus a word that is either packed unwind data (low bits non-zero)
    /// or an RVA to an .xdata record. Both encode the function length, which is what analysis needs.
    /// </summary>
    private IReadOnlyList<RuntimeFunction> ParseArm64ExceptionTable(DataDirectory dir)
    {
        int count = (int)Math.Min(dir.Size / RuntimeFunction.Arm64Size, 1_000_000);
        var list = new List<RuntimeFunction>(count);
        var r = new SpanReader(_data.Span, RequireOffset(dir.Rva, "Exception directory"));

        for (int i = 0; i < count; i++)
        {
            if (!r.CanRead(RuntimeFunction.Arm64Size))
            {
                _warnings.Add("Exception directory is truncated.");
                break;
            }

            uint begin = r.ReadU32();
            uint unwindData = r.ReadU32();
            if (begin == 0 && unwindData == 0)
            {
                continue; // padding
            }

            uint flag = unwindData & 3;
            if (flag != 0)
            {
                // Packed: bits 2-12 hold the length in 4-byte instruction words.
                uint length = ((unwindData >> 2) & 0x7FF) * 4;
                list.Add(new RuntimeFunction(begin, begin + length, 0)
                {
                    IsPacked = true,
                    IsChained = flag == 2, // packed fragment: a continuation of another function
                });
                continue;
            }

            // Unpacked: the word is an RVA to an .xdata header whose low 18 bits are the length
            // in words. A second word follows when the count does not fit, but the length does.
            uint headerRva = unwindData;
            var header = ReadAtRva(headerRva, 4);
            if (header.Length < 4)
            {
                _warnings.Add($"Unwind data at RVA 0x{headerRva:X} is not backed by file data.");
                list.Add(new RuntimeFunction(begin, begin, headerRva));
                continue;
            }

            uint word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.Span);
            uint words = word & 0x3FFFF;
            list.Add(new RuntimeFunction(begin, begin + (words * 4), headerRva));
        }

        return list;
    }

    private IReadOnlyList<RuntimeFunction> ParseX64ExceptionTable(DataDirectory dir)
    {
        var span = _data.Span;

        int count = (int)Math.Min(dir.Size / RuntimeFunction.Size, 1_000_000);
        var list = new List<RuntimeFunction>(count);
        var r = new SpanReader(span, RequireOffset(dir.Rva, "Exception directory"));
        for (int i = 0; i < count; i++)
        {
            if (!r.CanRead(RuntimeFunction.Size))
            {
                _warnings.Add("Exception directory is truncated.");
                break;
            }

            uint begin = r.ReadU32();
            uint end = r.ReadU32();
            uint unwind = r.ReadU32();
            if (begin == 0 && end == 0)
            {
                continue; // padding
            }

            bool chained = false;
            var unwindBytes = ReadAtRva(unwind, 1);
            if (unwindBytes.Length == 1)
            {
                // UNWIND_INFO: Version:3 | Flags:5 ; UNW_FLAG_CHAININFO = 0x4
                chained = ((unwindBytes.Span[0] >> 3) & 0x4) != 0;
            }

            list.Add(new RuntimeFunction(begin, end, unwind) { IsChained = chained });
        }

        return list;
    }

    private IReadOnlyList<RelocationBlock> ParseRelocations()
    {
        var dir = GetDirectory(DataDirectoryIndex.BaseRelocation);
        if (!dir.IsPresent)
        {
            return Array.Empty<RelocationBlock>();
        }

        var blocks = new List<RelocationBlock>();
        int start = RequireOffset(dir.Rva, "Base relocation directory");
        var r = new SpanReader(_data.Span, start);
        long end = (long)start + dir.Size;

        while (r.Position < end && blocks.Count < MaxRelocationBlocks)
        {
            if (!r.CanRead(8))
            {
                _warnings.Add("Base relocation table is truncated.");
                break;
            }

            uint pageRva = r.ReadU32();
            uint blockSize = r.ReadU32();
            if (blockSize < 8)
            {
                if (pageRva != 0 || blockSize != 0)
                {
                    _warnings.Add($"Base relocation block at RVA 0x{pageRva:X} has an invalid size ({blockSize}).");
                }

                break; // a zero-sized block would loop forever
            }

            int count = (int)Math.Min((blockSize - 8) / 2, MaxRelocationsPerBlock);
            var entries = new List<RelocationEntry>(count);
            for (int i = 0; i < count && r.CanRead(2); i++)
            {
                ushort raw = r.ReadU16();
                var type = (RelocationType)(raw >> 12);
                if (type == RelocationType.Absolute)
                {
                    continue; // padding entries
                }

                entries.Add(new RelocationEntry(type, pageRva + (uint)(raw & 0x0FFF)));
            }

            blocks.Add(new RelocationBlock { PageRva = pageRva, BlockSize = blockSize, Entries = entries });
        }

        return blocks;
    }

    private TlsDirectory? ParseTls()
    {
        var dir = GetDirectory(DataDirectoryIndex.Tls);
        if (!dir.IsPresent)
        {
            return null;
        }

        var r = new SpanReader(_data.Span, RequireOffset(dir.Rva, "TLS directory"));
        int pointerSize = Is64Bit ? 8 : 4;
        if (!r.CanRead((pointerSize * 4) + 8))
        {
            _warnings.Add("TLS directory is truncated.");
            return null;
        }

        ulong startData = r.ReadPointer(Is64Bit);
        ulong endData = r.ReadPointer(Is64Bit);
        ulong indexVa = r.ReadPointer(Is64Bit);
        ulong callbacksVa = r.ReadPointer(Is64Bit);
        uint zeroFill = r.ReadU32();
        uint characteristics = r.ReadU32();

        var callbacks = new List<ulong>();
        if (callbacksVa != 0 && VaToRva(callbacksVa) is { } listRva)
        {
            for (int i = 0; i < MaxTlsCallbacks; i++)
            {
                if (ReadPointerAtRva(listRva + (uint)(i * pointerSize)) is not { } callback || callback == 0)
                {
                    break;
                }

                callbacks.Add(callback);
            }
        }

        return new TlsDirectory
        {
            StartAddressOfRawData = startData,
            EndAddressOfRawData = endData,
            AddressOfIndex = indexVa,
            AddressOfCallBacks = callbacksVa,
            SizeOfZeroFill = zeroFill,
            Characteristics = characteristics,
            CallbackVas = callbacks,
        };
    }

    private LoadConfig? ParseLoadConfig()
    {
        var dir = GetDirectory(DataDirectoryIndex.LoadConfig);
        if (!dir.IsPresent)
        {
            return null;
        }

        int baseOffset = RequireOffset(dir.Rva, "Load config directory");
        var r = new SpanReader(_data.Span, baseOffset);
        if (!r.CanRead(12))
        {
            _warnings.Add("Load config directory is truncated.");
            return null;
        }

        uint size = r.ReadU32();
        uint timeStamp = r.ReadU32();
        ushort major = r.ReadU16();
        ushort minor = r.ReadU16();

        // Everything past the version fields is optional - older toolchains emit a short structure -
        // and the field offsets differ between PE32 and PE32+, so read them by explicit offset.
        ulong Pointer(int offset32, int offset64)
        {
            int offset = Is64Bit ? offset64 : offset32;
            int width = Is64Bit ? 8 : 4;
            if (offset + width > size || baseOffset + offset + width > _data.Length)
            {
                return 0;
            }

            var fr = new SpanReader(_data.Span, baseOffset + offset);
            return fr.ReadPointer(Is64Bit);
        }

        uint Dword(int offset32, int offset64)
        {
            int offset = Is64Bit ? offset64 : offset32;
            if (offset + 4 > size || baseOffset + offset + 4 > _data.Length)
            {
                return 0;
            }

            var fr = new SpanReader(_data.Span, baseOffset + offset);
            return fr.ReadU32();
        }

        ulong securityCookie = Pointer(0x3C, 0x58);
        ulong seHandlerTable = Pointer(0x40, 0x60);
        ulong seHandlerCount = Pointer(0x44, 0x68);
        ulong cfCheck = Pointer(0x48, 0x70);
        ulong cfDispatch = Pointer(0x4C, 0x78);
        ulong cfTable = Pointer(0x50, 0x80);
        ulong cfCount = Pointer(0x54, 0x88);
        uint rawGuardFlags = Dword(0x58, 0x90);

        // The top nibble holds extra metadata bytes appended to each table entry, not a flag.
        int stride = 4 + (int)((rawGuardFlags >> 28) & 0xF);
        var guardFlags = (GuardFlags)(rawGuardFlags & 0x0FFF_FFFF);
        var cfRvas = ReadRvaTable(cfTable, cfCount, stride, "Control Flow Guard function table");
        var sehRvas = Is64Bit ? Array.Empty<uint>() : ReadRvaTable(seHandlerTable, seHandlerCount, 4, "SafeSEH handler table");

        return new LoadConfig
        {
            Size = size,
            TimeDateStamp = timeStamp,
            MajorVersion = major,
            MinorVersion = minor,
            SecurityCookieVa = securityCookie,
            SeHandlerTableVa = seHandlerTable,
            SeHandlerCount = seHandlerCount,
            GuardCfCheckFunctionPointerVa = cfCheck,
            GuardCfDispatchFunctionPointerVa = cfDispatch,
            GuardCfFunctionTableVa = cfTable,
            GuardCfFunctionCount = cfCount,
            GuardFlags = guardFlags,
            GuardCfFunctionTableStride = stride,
            GuardCfFunctionRvas = cfRvas,
            SeHandlerRvas = sehRvas,
        };
    }

    /// <summary>Reads a table of 4-byte RVAs (optionally with trailing per-entry metadata) located at a VA.</summary>
    private IReadOnlyList<uint> ReadRvaTable(ulong tableVa, ulong count, int stride, string what)
    {
        if (tableVa == 0 || count == 0 || stride < 4)
        {
            return Array.Empty<uint>();
        }

        if (VaToRva(tableVa) is not { } tableRva)
        {
            _warnings.Add($"{what} VA 0x{tableVa:X} is outside the image.");
            return Array.Empty<uint>();
        }

        int wanted = (int)Math.Min(count, MaxGuardFunctions);
        var mem = ReadAtRva(tableRva, wanted * stride);
        if (mem.IsEmpty)
        {
            _warnings.Add($"{what} at RVA 0x{tableRva:X} is not backed by file data.");
            return Array.Empty<uint>();
        }

        int usable = mem.Length / stride;
        if (usable < wanted)
        {
            _warnings.Add($"{what} is truncated ({usable} of {wanted} entries readable).");
        }

        var rvas = new List<uint>(usable);
        var r = new SpanReader(mem.Span);
        for (int i = 0; i < usable; i++)
        {
            r.Seek(i * stride);
            rvas.Add(r.ReadU32());
        }

        return rvas;
    }

    private ResourceNode? ParseResources()
    {
        var dir = GetDirectory(DataDirectoryIndex.Resource);
        if (!dir.IsPresent)
        {
            return null;
        }

        uint rootRva = dir.Rva;
        int budget = MaxResourceNodes;
        var visited = new HashSet<uint>();

        ResourceNode? ReadDataEntry(uint entryOffset, int level, string? name, uint id)
        {
            var mem = ReadAtRva(rootRva + entryOffset, 16);
            if (mem.Length < 16)
            {
                _warnings.Add("Resource data entry is truncated.");
                return null;
            }

            var r = new SpanReader(mem.Span);
            return new ResourceNode
            {
                Name = name,
                Id = id,
                Level = level,
                DataRva = r.ReadU32(),
                DataSize = r.ReadU32(),
                CodePage = r.ReadU32(),
            };
        }

        ResourceNode? ReadDirectory(uint dirOffset, int level, string? name, uint id)
        {
            // Malformed images can point a subdirectory back at an ancestor; visited breaks the cycle.
            if (level > 3 || budget <= 0 || !visited.Add(dirOffset))
            {
                return null;
            }

            var header = ReadAtRva(rootRva + dirOffset, 16);
            if (header.Length < 16)
            {
                _warnings.Add($"Resource directory at 0x{dirOffset:X} is truncated.");
                return null;
            }

            var hr = new SpanReader(header.Span);
            hr.Skip(12); // Characteristics, TimeDateStamp, MajorVersion, MinorVersion
            int named = hr.ReadU16();
            int numbered = hr.ReadU16();
            int total = named + numbered;

            var children = new List<ResourceNode>(total);
            for (int i = 0; i < total && budget > 0; i++)
            {
                var entry = ReadAtRva(rootRva + dirOffset + 16 + (uint)(i * 8), 8);
                if (entry.Length < 8)
                {
                    _warnings.Add("Resource directory entries are truncated.");
                    break;
                }

                var er = new SpanReader(entry.Span);
                uint nameField = er.ReadU32();
                uint offsetField = er.ReadU32();
                budget--;

                string? childName = null;
                uint childId = nameField;
                if ((nameField & 0x8000_0000) != 0)
                {
                    childName = ReadResourceString(rootRva + (nameField & 0x7FFF_FFFF));
                    childId = 0;
                }

                uint childOffset = offsetField & 0x7FFF_FFFF;
                var child = (offsetField & 0x8000_0000) != 0
                    ? ReadDirectory(childOffset, level + 1, childName, childId)
                    : ReadDataEntry(childOffset, level + 1, childName, childId);
                if (child is not null)
                {
                    children.Add(child);
                }
            }

            return new ResourceNode { Name = name, Id = id, Level = level, Children = children };
        }

        return ReadDirectory(0, 0, null, 0);
    }

    /// <summary>Reads a length-prefixed UTF-16 resource name.</summary>
    private string ReadResourceString(uint rva)
    {
        var lengthMem = ReadAtRva(rva, 2);
        if (lengthMem.Length < 2)
        {
            return string.Empty;
        }

        int chars = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(lengthMem.Span);
        var mem = ReadAtRva(rva + 2, Math.Min(chars, 512) * 2);
        return mem.IsEmpty ? string.Empty : System.Text.Encoding.Unicode.GetString(mem.Span);
    }

    /// <summary>
    /// Decodes the undocumented "Rich" header the Microsoft linker hides in the DOS stub:
    /// XOR-encrypted (tool id, build, use count) triples between the DanS marker and the Rich signature.
    /// </summary>
    private RichHeader? ParseRichHeader()
    {
        var span = _data.Span;
        int limit = Math.Min((int)DosHeader.NewHeaderOffset, span.Length);
        if (limit < 0x80)
        {
            return null;
        }

        const uint RichSignature = 0x6863_6952; // "Rich"
        const uint DanSMarker = 0x536E_6144;    // "DanS"

        int richOffset = -1;
        for (int i = limit - 4; i >= 0x40; i -= 4)
        {
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(i, 4)) == RichSignature)
            {
                richOffset = i;
                break;
            }
        }

        if (richOffset < 0 || richOffset + 8 > span.Length)
        {
            return null;
        }

        uint key = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(richOffset + 4, 4));

        int startOffset = -1;
        for (int i = richOffset - 4; i >= 0; i -= 4)
        {
            if ((System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(i, 4)) ^ key) == DanSMarker)
            {
                startOffset = i;
                break;
            }
        }

        if (startOffset < 0)
        {
            _warnings.Add("Found a Rich signature without its DanS marker.");
            return null;
        }

        var entries = new List<RichEntry>();
        for (int i = startOffset + 16; i + 8 <= richOffset; i += 8)
        {
            uint idField = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(i, 4)) ^ key;
            uint count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(i + 4, 4)) ^ key;
            if (idField == 0 && count == 0)
            {
                continue; // padding
            }

            entries.Add(new RichEntry((ushort)(idField >> 16), (ushort)(idField & 0xFFFF), count));
        }

        return new RichHeader
        {
            Offset = (uint)startOffset,
            Checksum = key,
            ComputedChecksum = ComputeRichChecksum(span, startOffset, entries),
            Entries = entries,
        };
    }

/// <summary>
    /// Reads the certificate table. Unlike every other directory, its "RVA" is a raw file offset:
    /// the data sits in the overlay, outside any section, so the loader never maps it.
    /// </summary>
    private AuthenticodeSignature? ParseSignature()
    {
        var dir = GetDirectory(DataDirectoryIndex.Security);
        if (!dir.IsPresent)
        {
            return null;
        }

        long offset = dir.Rva;
        if (offset + 8 > _data.Length)
        {
            _warnings.Add($"Certificate table at file offset 0x{offset:X} is outside the file.");
            return null;
        }

        var r = new SpanReader(_data.Span, (int)offset);
        uint length = r.ReadU32();
        ushort revision = r.ReadU16();
        var type = (CertificateType)r.ReadU16();

        if (length < 8 || offset + length > _data.Length)
        {
            _warnings.Add($"Certificate table declares {length} bytes but only {_data.Length - offset} are present.");
            length = (uint)Math.Max(8, Math.Min(length, _data.Length - offset));
        }

        var signature = new AuthenticodeSignature
        {
            Offset = offset,
            Length = length,
            Revision = revision,
            Type = type,
        };

        if (type != CertificateType.PkcsSignedData)
        {
            return signature;
        }

        var blob = _data.Slice((int)offset + 8, (int)(length - 8));
        return DescribeSignedData(signature, blob);
    }

    /// <summary>
    /// Describes the PKCS#7 blob for display. Failure is never fatal: a malformed or unusual
    /// signature still tells the user the file is signed, it just cannot be summarised.
    /// </summary>
    private static AuthenticodeSignature DescribeSignedData(AuthenticodeSignature signature, ReadOnlyMemory<byte> blob)
    {
        try
        {
            var cms = new SignedCms();
            cms.Decode(blob.Span);

            var signer = cms.SignerInfos.Count > 0 ? cms.SignerInfos[0] : null;
            var certificate = signer?.Certificate;

            return signature with
            {
                CertificateCount = cms.Certificates.Count,
                SignerSubject = certificate?.Subject,
                SignerIssuer = certificate?.Issuer,
                SignerSerialNumber = certificate?.SerialNumber,
                NotBefore = certificate is null ? null : new DateTimeOffset(certificate.NotBefore.ToUniversalTime()),
                NotAfter = certificate is null ? null : new DateTimeOffset(certificate.NotAfter.ToUniversalTime()),
                DigestAlgorithm = signer?.DigestAlgorithm.FriendlyName ?? signer?.DigestAlgorithm.Value,
                Timestamp = signer is null ? null : FindTimestamp(signer),
            };
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            return signature with { ParseError = ex.Message };
        }
    }

    /// <summary>
    /// When the signature was timestamped. Two mechanisms exist: the modern RFC 3161 token, which
    /// is what Windows binaries carry, and the legacy PKCS#9 countersignature.
    /// </summary>
    private static DateTimeOffset? FindTimestamp(SignerInfo signer)
    {
        const string Rfc3161TokenOid = "1.3.6.1.4.1.311.3.3.1";
        const string SigningTimeOid = "1.2.840.113549.1.9.5";

        foreach (var attribute in signer.UnsignedAttributes)
        {
            if (attribute.Oid.Value == Rfc3161TokenOid
                && attribute.Values.Count > 0
                && Rfc3161TimestampToken.TryDecode(attribute.Values[0].RawData, out var token, out _))
            {
                return token.TokenInfo.Timestamp;
            }
        }

        foreach (var counter in signer.CounterSignerInfos)
        {
            foreach (var attribute in counter.SignedAttributes)
            {
                if (attribute.Oid.Value == SigningTimeOid && attribute.Values.Count > 0)
                {
                    return new Pkcs9SigningTime(attribute.Values[0].RawData).SigningTime;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Recomputes the Rich checksum: the DanS offset, plus every DOS stub byte rotated by its own
    /// position, plus every entry rotated by its use count. The e_lfanew field is excluded because
    /// the linker computes the sum before it knows where the PE header will land.
    /// </summary>
    private static uint ComputeRichChecksum(ReadOnlySpan<byte> span, int dansOffset, IReadOnlyList<RichEntry> entries)
    {
        uint sum = (uint)dansOffset;

        for (int i = 0; i < dansOffset && i < span.Length; i++)
        {
            if (i is >= 0x3C and < 0x40)
            {
                continue;
            }

            sum += RotateLeft(span[i], i & 0x1F);
        }

        foreach (var entry in entries)
        {
            uint value = ((uint)entry.ProductId << 16) | entry.BuildNumber;
            sum += RotateLeft(value, (int)(entry.UseCount & 0x1F));
        }

        return sum;
    }

    private static uint RotateLeft(uint value, int count) => (value << count) | (value >> (32 - count));

    private (uint, uint) ComputeOverlay()
    {
        ulong end = OptionalHeader.SizeOfHeaders;
        foreach (var s in Sections)
        {
            end = Math.Max(end, (ulong)s.PointerToRawData + s.SizeOfRawData);
        }

        // Authenticode certificate table lives in the overlay by definition; still counts as overlay.
        if (end < (ulong)_data.Length && end <= uint.MaxValue)
        {
            return ((uint)end, (uint)((ulong)_data.Length - end));
        }

        return (0, 0);
    }

    public override string ToString() => $"{FileName} ({Machine}, {(Is64Bit ? "PE32+" : "PE32")}{(IsManaged ? ", .NET" : string.Empty)})";
}
