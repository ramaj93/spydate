using System.Collections.ObjectModel;
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
        var span = _data.Span;
        var dir = GetDirectory(DataDirectoryIndex.Exception);
        // Only the x64 (and ARM64/ARM, different layout — not parsed) format is understood here.
        if (!dir.IsPresent || Machine != MachineType.Amd64)
        {
            return Array.Empty<RuntimeFunction>();
        }

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
