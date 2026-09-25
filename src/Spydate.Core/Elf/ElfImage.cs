using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Spydate.Core.Binary;

namespace Spydate.Core.Elf;

/// <summary>
/// Immutable, parsed view of an ELF file held in memory: a Linux, BSD or Android program, shared object or
/// object file. Written in-house like the PE parser, and to the same rule: only a header that cannot be read
/// throws (<see cref="ElfParseException"/>); every table after it is parsed on its own, and one that is damaged
/// becomes a line in <see cref="Warnings"/> rather than a failure to open.
///
/// The address space is what the loader would build — the <c>PT_LOAD</c> segments — and
/// <see cref="ImageBase"/> is the lowest of them, so an RVA means "offset from the first loaded byte" exactly
/// as it does for a PE. Sections are the finer view the analysis reads; a file with no section headers (stripped
/// by a packer, say) is shown one range per segment instead.
/// </summary>
public sealed class ElfImage : IBinaryImage, IUnwindInfoSource, ISymbolSource
{
    private const int MaxProgramHeaders = 4096;
    private const int MaxSectionHeaders = 65536;
    private const int MaxDynamicEntries = 8192;
    private const int MaxSymbols = 2_000_000;
    private const int MaxRelocations = 4_000_000;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly List<string> _warnings = new();

    /// <summary>The loaded address space: where each piece of the file lands, and how much of it is file-backed.</summary>
    private readonly List<(ulong Va, ulong MemorySize, ulong Offset, ulong FileSize)> _mapped = new();

    private ElfImage(ReadOnlyMemory<byte> data, string? path)
    {
        _data = data;
        Path = path;
        FileName = path is null ? "<memory>" : System.IO.Path.GetFileName(path);

        Header = ParseHeader(data.Span);
        Segments = Guard(ParseSegments, "program headers", Array.Empty<ElfSegment>());
        SectionHeaders = Guard(ParseSectionHeaders, "section headers", Array.Empty<ElfSectionHeader>());

        (ImageBase, ImageSize, _sectionRvas) = Layout();
        Sections = BuildSections();

        Interpreter = Guard(ParseInterpreter, "interpreter path", null);
        Dynamic = Guard(ParseDynamic, "dynamic section", Array.Empty<ElfDynamicEntry>());
        DynamicSymbols = Guard(() => ParseSymbols(dynamic: true), "dynamic symbol table", Array.Empty<ElfSymbol>());
        StaticSymbols = Guard(() => ParseSymbols(dynamic: false), "symbol table", Array.Empty<ElfSymbol>());
        DynamicSymbols = Guard(ApplyVersions, "symbol versions", DynamicSymbols);
        Relocations = Guard(ParseRelocations, "relocations", Array.Empty<ElfRelocation>());
        BuildId = Guard(ParseBuildId, "build-id note", null);

        Imports = Guard(BuildImports, "imports", Array.Empty<ImportedSymbol>());
        Exports = Guard(BuildExports, "exports", Array.Empty<ExportedSymbol>());
        PltStubs = Guard(() => FindPltStubs(Imports.ToDictionary(i => i.SlotRva)), "PLT stubs", Array.Empty<(uint, ImportedSymbol)>());
        LocalPltStubs = Guard<IReadOnlyList<(uint Rva, string Name)>>(() => FindPltStubs(LocalSlots()).Select(s => (s.Rva, s.Import.Name!)).ToList(), "PLT stubs", Array.Empty<(uint, string)>());
        UnwindRanges = Guard(() => EhFrame.Read(this), ".eh_frame", Array.Empty<(uint, uint)>());

        EntryPointRva = Header.Entry != 0 && VaToRva(Header.Entry) is { } entry && RvaToOffset(entry) is not null ? entry : 0;
        if (Header.Entry != 0 && EntryPointRva == 0)
        {
            _warnings.Add($"The entry point 0x{Header.Entry:X} is not in any loaded part of the file.");
        }

        Fingerprint = BuildId ?? HeaderHash();
    }

    // ---------------------------------------------------------------------
    // Construction
    // ---------------------------------------------------------------------

    public static ElfImage Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ElfParseException($"Cannot read '{path}': {ex.Message}", ex);
        }

        return new ElfImage(bytes, path);
    }

    public static ElfImage Parse(ReadOnlyMemory<byte> data, string? path = null) => new(data, path);

    // ---------------------------------------------------------------------
    // Public surface
    // ---------------------------------------------------------------------

    public BinaryFormat Format => BinaryFormat.Elf;

    public string? Path { get; }

    public string FileName { get; }

    public ReadOnlyMemory<byte> Data => _data;

    public long Length => _data.Length;

    public ElfHeader Header { get; }

    public IReadOnlyList<ElfSegment> Segments { get; }

    /// <summary>Every section header, allocated or not, in file order.</summary>
    public IReadOnlyList<ElfSectionHeader> SectionHeaders { get; }

    /// <summary>The parts of the address space the analysis reads, ordered by address.</summary>
    public IReadOnlyList<IBinarySection> Sections { get; }

    /// <summary>The program interpreter (<c>PT_INTERP</c>), such as <c>/lib64/ld-linux-x86-64.so.2</c>.</summary>
    public string? Interpreter { get; }

    public IReadOnlyList<ElfDynamicEntry> Dynamic { get; }

    /// <summary><c>.dynsym</c>: what the image imports and exports.</summary>
    public IReadOnlyList<ElfSymbol> DynamicSymbols { get; }

    /// <summary><c>.symtab</c>: the full symbol table, when the file was not stripped.</summary>
    public IReadOnlyList<ElfSymbol> StaticSymbols { get; }

    /// <summary>The dynamic relocations — the ones the loader applies — with their symbols.</summary>
    public IReadOnlyList<ElfRelocation> Relocations { get; }

    /// <summary>The GNU build-id note, as hex, when the linker wrote one.</summary>
    public string? BuildId { get; }

    /// <summary>Each PLT stub and the import it jumps to.</summary>
    public IReadOnlyList<(uint Rva, ImportedSymbol Import)> PltStubs { get; }

    /// <summary>
    /// PLT stubs for functions this image defines itself but exports, so that another library can take their place:
    /// a shared library calls its own public functions through the PLT. Named <c>name@plt</c>, as objdump names them,
    /// so they stay apart from the definition.
    /// </summary>
    public IReadOnlyList<(uint Rva, string Name)> LocalPltStubs { get; }

    public IReadOnlyList<ExportedSymbol> Exports { get; }

    public IReadOnlyList<ImportedSymbol> Imports { get; }

    public IReadOnlyList<(uint BeginRva, uint EndRva)> UnwindRanges { get; }

    public IReadOnlyList<string> Warnings => new ReadOnlyCollection<string>(_warnings);

    public string Fingerprint { get; }

    public bool Is64Bit => Header.Is64Bit;

    public int Bitness => Is64Bit ? 64 : 32;

    public Architecture Architecture => (ElfMachine)Header.Machine switch
    {
        ElfMachine.I386 => Architecture.X86,
        ElfMachine.X86_64 => Architecture.X64,
        ElfMachine.Arm => Architecture.Arm,
        ElfMachine.AArch64 => Architecture.Arm64,
        _ => Architecture.Unknown,
    };

    public ulong ImageBase { get; }

    public ulong ImageSize { get; }

    public uint EntryPointRva { get; }

    public ulong EntryPointVa => EntryPointRva == 0 ? 0 : RvaToVa(EntryPointRva);

    /// <summary>The libraries named by <c>DT_NEEDED</c>, in load order.</summary>
    public IReadOnlyList<string> Needed => Dynamic.Where(d => d.Tag == ElfDynamicEntry.Needed && d.Text is { Length: > 0 }).Select(d => d.Text!).ToList();

    public string? SoName => Dynamic.FirstOrDefault(d => d.Tag == ElfDynamicEntry.SoName)?.Text;

    /// <summary>
    /// A shared object that is not a program. A position-independent executable is <see cref="ElfType.Shared"/>
    /// too; it gives itself away by naming an interpreter or by the <c>DF_1_PIE</c> flag.
    /// </summary>
    public bool IsLibrary => Header.Type == ElfType.Shared
                             && Interpreter is null
                             && !Dynamic.Any(d => d.Tag == ElfDynamicEntry.Flags1 && (d.Value & ElfDynamicEntry.Flags1Pie) != 0);

    /// <summary>What kind of file this is, in the words <c>file</c> would use.</summary>
    public string Kind => Header.Type switch
    {
        ElfType.Relocatable => "relocatable object",
        ElfType.Executable => "executable",
        ElfType.Shared => IsLibrary ? "shared object" : "position-independent executable",
        ElfType.Core => "core dump",
        _ => $"type {(ushort)Header.Type}",
    };

    IReadOnlyList<ImageSymbol> ISymbolSource.Symbols => _imageSymbols ??= BuildImageSymbols();

    private IReadOnlyList<ImageSymbol>? _imageSymbols;

    /// <summary>For an object file, the address each allocated section was laid out at, by section index.</summary>
    private readonly Dictionary<int, ulong> _sectionRvas;

    // ---------------------------------------------------------------------
    // Address translation
    // ---------------------------------------------------------------------

    public IBinarySection? SectionFromRva(uint rva)
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

    public IBinarySection? SectionFromVa(ulong va) => VaToRva(va) is { } rva ? SectionFromRva(rva) : null;

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

    public uint? RvaToOffset(uint rva) => VaToOffset(RvaToVa(rva));

    public uint? VaToOffset(ulong va)
    {
        foreach (var (start, _, offset, fileSize) in _mapped)
        {
            if (va >= start && va - start < fileSize)
            {
                ulong at = offset + (va - start);
                return at < (ulong)_data.Length ? (uint)at : null;
            }
        }

        return null;
    }

    public uint? OffsetToRva(uint offset)
    {
        foreach (var (start, _, fileOffset, fileSize) in _mapped)
        {
            if (offset >= fileOffset && offset - fileOffset < fileSize)
            {
                return VaToRva(start + (offset - fileOffset));
            }
        }

        return null;
    }

    public ReadOnlyMemory<byte> ReadAtRva(uint rva, int length) => ReadAtVa(RvaToVa(rva), length);

    public ReadOnlyMemory<byte> ReadAtVa(ulong va, int length)
    {
        if (length <= 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        foreach (var (start, _, offset, fileSize) in _mapped)
        {
            if (va < start || va - start >= fileSize)
            {
                continue;
            }

            ulong at = offset + (va - start);
            if (at >= (ulong)_data.Length)
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            ulong available = Math.Min(fileSize - (va - start), (ulong)_data.Length - at);
            return _data.Slice((int)at, (int)Math.Min((ulong)length, available));
        }

        return ReadOnlyMemory<byte>.Empty;
    }

    public ulong? ReadPointerAtRva(uint rva)
    {
        int size = Is64Bit ? 8 : 4;
        var bytes = ReadAtRva(rva, size);
        if (bytes.Length < size)
        {
            return null;
        }

        var r = new ElfReader(bytes.Span, Is64Bit, Header.IsBigEndian);
        return r.Word();
    }

    /// <summary>The bytes of a section as they are in the file; empty for one with no file bytes.</summary>
    public ReadOnlySpan<byte> SectionBytes(ElfSectionHeader section)
    {
        if (section.HasNoBits || section.Offset >= (ulong)_data.Length)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        ulong size = Math.Min(section.Size, (ulong)_data.Length - section.Offset);
        return _data.Span.Slice((int)section.Offset, (int)size);
    }

    /// <summary>Where a section sits in the image: its address, or for an object file, where it was laid out.</summary>
    public ulong AddressOf(ElfSectionHeader section)
        => _sectionRvas.TryGetValue(section.Index, out ulong rva) ? ImageBase + rva : section.Address;

    public ElfSectionHeader? SectionHeader(string name) => SectionHeaders.FirstOrDefault(s => s.Name == name);

    public override string ToString() => $"{FileName} ({Header.MachineName}, {Header.ClassName}, {Kind})";

    // ---------------------------------------------------------------------
    // Parsing
    // ---------------------------------------------------------------------

    private T Guard<T>(Func<T> parse, string what, T fallback)
    {
        try
        {
            return parse();
        }
        catch (Exception ex) when (ex is ElfParseException or OverflowException or ArgumentException or InvalidOperationException)
        {
            _warnings.Add($"Could not parse {what}: {ex.Message}");
            return fallback;
        }
    }

    private ElfReader Reader(long position = 0) => new(_data.Span, Header.Is64Bit, Header.IsBigEndian, position);

    private static ElfHeader ParseHeader(ReadOnlySpan<byte> span)
    {
        if (span.Length < 16 || span[0] != 0x7F || span[1] != (byte)'E' || span[2] != (byte)'L' || span[3] != (byte)'F')
        {
            throw new ElfParseException("The file does not start with the ELF magic number.");
        }

        bool is64 = span[4] switch
        {
            1 => false,
            2 => true,
            _ => throw new ElfParseException($"ELF class {span[4]} is neither 32-bit (1) nor 64-bit (2)."),
        };
        bool big = span[5] switch
        {
            1 => false,
            2 => true,
            _ => throw new ElfParseException($"ELF byte order {span[5]} is neither little-endian (1) nor big-endian (2)."),
        };

        int needed = is64 ? 64 : 52;
        if (span.Length < needed)
        {
            throw new ElfParseException($"The ELF header is cut short: the file is {span.Length} bytes and an {(is64 ? "ELF64" : "ELF32")} header is {needed}.");
        }

        var r = new ElfReader(span, is64, big, 16);
        var type = (ElfType)r.U16();
        ushort machine = r.U16();
        uint version = r.U32();
        ulong entry = r.Word();
        ulong phoff = r.Word();
        ulong shoff = r.Word();
        uint flags = r.U32();
        ushort ehsize = r.U16();
        ushort phentsize = r.U16();
        int phnum = r.U16();
        ushort shentsize = r.U16();
        int shnum = r.U16();
        int shstrndx = r.U16();

        // Extended numbering: more than 0xFFFF sections (or 0xFFFF program headers) put the real count in the
        // first section header, which exists for exactly this purpose.
        if ((shnum == 0 || shstrndx == 0xFFFF || phnum == 0xFFFF) && shoff != 0 && shoff < (ulong)span.Length)
        {
            try
            {
                var first = new ElfReader(span, is64, big, (long)shoff);
                first.Skip(is64 ? 32 : 20);   // name, type, flags, addr, offset
                ulong size = first.Word();
                uint link = first.U32();
                uint info = first.U32();
                if (shnum == 0 && size is > 0 and <= MaxSectionHeaders)
                {
                    shnum = (int)size;
                }

                if (shstrndx == 0xFFFF)
                {
                    shstrndx = (int)Math.Min(link, int.MaxValue);
                }

                if (phnum == 0xFFFF && info is > 0 and <= MaxProgramHeaders)
                {
                    phnum = (int)info;
                }
            }
            catch (ElfParseException)
            {
                // The counts stay as written; the section parser reports what it cannot read.
            }
        }

        return new ElfHeader(is64, big, span[7], span[8], type, machine, version, entry, phoff, shoff, flags, ehsize, phentsize, phnum, shentsize, shnum, shstrndx);
    }

    private IReadOnlyList<ElfSegment> ParseSegments()
    {
        if (Header.ProgramHeaderCount == 0 || Header.ProgramHeaderOffset == 0)
        {
            return Array.Empty<ElfSegment>();
        }

        int expected = Header.Is64Bit ? 56 : 32;
        if (Header.ProgramHeaderEntrySize < expected)
        {
            throw new ElfParseException($"program header entries are {Header.ProgramHeaderEntrySize} bytes; an {Header.ClassName} entry is {expected}.");
        }

        int count = Math.Min(Header.ProgramHeaderCount, MaxProgramHeaders);
        var list = new List<ElfSegment>(count);
        for (int i = 0; i < count; i++)
        {
            var r = Reader(checked((long)Header.ProgramHeaderOffset + (long)i * Header.ProgramHeaderEntrySize));
            if (Header.Is64Bit)
            {
                uint type = r.U32();
                uint flags = r.U32();
                list.Add(new ElfSegment(i, type, flags, r.U64(), r.U64(), r.U64(), r.U64(), r.U64(), r.U64()));
            }
            else
            {
                uint type = r.U32();
                ulong offset = r.U32();
                ulong vaddr = r.U32();
                ulong paddr = r.U32();
                ulong filesz = r.U32();
                ulong memsz = r.U32();
                uint flags = r.U32();
                ulong align = r.U32();
                list.Add(new ElfSegment(i, type, flags, offset, vaddr, paddr, filesz, memsz, align));
            }
        }

        return list;
    }

    private IReadOnlyList<ElfSectionHeader> ParseSectionHeaders()
    {
        if (Header.SectionHeaderCount == 0 || Header.SectionHeaderOffset == 0)
        {
            return Array.Empty<ElfSectionHeader>();
        }

        int expected = Header.Is64Bit ? 64 : 40;
        if (Header.SectionHeaderEntrySize < expected)
        {
            throw new ElfParseException($"section header entries are {Header.SectionHeaderEntrySize} bytes; an {Header.ClassName} entry is {expected}.");
        }

        int count = Math.Min(Header.SectionHeaderCount, MaxSectionHeaders);
        var raw = new List<(uint Name, uint Type, ulong Flags, ulong Addr, ulong Offset, ulong Size, uint Link, uint Info, ulong Align, ulong EntSize)>(count);
        for (int i = 0; i < count; i++)
        {
            var r = Reader(checked((long)Header.SectionHeaderOffset + (long)i * Header.SectionHeaderEntrySize));
            raw.Add((r.U32(), r.U32(), r.Word(), r.Word(), r.Word(), r.Word(), r.U32(), r.U32(), r.Word(), r.Word()));
        }

        // Names live in the section-name string table; a bad index leaves them empty rather than failing.
        ulong namesOffset = 0;
        ulong namesSize = 0;
        if (Header.SectionNameTableIndex > 0 && Header.SectionNameTableIndex < raw.Count)
        {
            namesOffset = raw[Header.SectionNameTableIndex].Offset;
            namesSize = raw[Header.SectionNameTableIndex].Size;
        }
        else if (Header.SectionNameTableIndex != 0)
        {
            _warnings.Add($"The section-name table index {Header.SectionNameTableIndex} is not a section; sections are unnamed.");
        }

        var list = new List<ElfSectionHeader>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            var s = raw[i];
            string name = namesSize > 0 && s.Name < namesSize ? ElfReader.StringAt(_data.Span, (long)Math.Min(namesOffset + s.Name, long.MaxValue), 256) : string.Empty;
            list.Add(new ElfSectionHeader(i, name, s.Type, s.Flags, s.Addr, s.Offset, s.Size, s.Link, s.Info, s.Align, s.EntSize));
        }

        return list;
    }

    /// <summary>
    /// Builds the address space: from the loadable segments, which is what a loader maps; or, for an object file
    /// that has none, by laying its allocated sections out one after another, since they have no addresses yet.
    /// </summary>
    private (ulong Base, ulong Size, Dictionary<int, ulong> SectionRvas) Layout()
    {
        var sectionRvas = new Dictionary<int, ulong>();
        var loads = Segments.Where(s => s.IsLoad && s.MemorySize > 0).ToList();
        if (loads.Count > 0)
        {
            ulong low = loads.Min(s => s.VirtualAddress) & ~0xFFFUL;
            foreach (var s in loads.OrderBy(s => s.VirtualAddress))
            {
                ulong fileSize = Math.Min(s.FileSize, s.MemorySize);
                if (s.MemorySize > uint.MaxValue || s.VirtualAddress - low > uint.MaxValue - s.MemorySize)
                {
                    _warnings.Add($"Segment {s.Index} at 0x{s.VirtualAddress:X} lies more than 4 GB above the image base; it is left out.");
                    continue;
                }

                _mapped.Add((s.VirtualAddress, s.MemorySize, s.Offset, fileSize));
            }

            ulong high = _mapped.Count == 0 ? low : _mapped.Max(m => m.Va + m.MemorySize);
            return (low, high - low, sectionRvas);
        }

        var allocated = SectionHeaders.Where(s => s.IsAllocated && s.Size > 0).ToList();
        if (allocated.Count == 0)
        {
            return (0, 0, sectionRvas);
        }

        if (allocated.All(s => s.Address == 0))
        {
            // An object file: nothing has an address until it is linked, so give each section the next free one.
            // References between sections are still unrelocated, and the warning says so.
            ulong cursor = 0x1000;
            foreach (var s in allocated)
            {
                ulong align = s.AddressAlign is > 1 and <= 0x10000 ? s.AddressAlign : 1;
                cursor = (cursor + align - 1) / align * align;
                if (s.Size > uint.MaxValue - cursor)
                {
                    break;
                }

                sectionRvas[s.Index] = cursor;
                _mapped.Add((cursor, s.Size, s.Offset, s.HasNoBits ? 0 : s.Size));
                cursor += s.Size;
            }

            _warnings.Add("This is an object file: its sections have no addresses until it is linked, so they are laid out one after another, and calls and references between them are not relocated.");
            return (0, cursor, sectionRvas);
        }

        ulong lowest = allocated.Min(s => s.Address) & ~0xFFFUL;
        foreach (var s in allocated)
        {
            if (s.Size <= uint.MaxValue && s.Address - lowest <= uint.MaxValue - s.Size)
            {
                _mapped.Add((s.Address, s.Size, s.Offset, s.HasNoBits ? 0 : s.Size));
            }
        }

        return (lowest, _mapped.Count == 0 ? 0 : _mapped.Max(m => m.Va + m.MemorySize) - lowest, sectionRvas);
    }

    private IReadOnlyList<IBinarySection> BuildSections()
    {
        var list = new List<ElfAddressRange>();
        foreach (var s in SectionHeaders)
        {
            // A thread-local .tbss takes no address space where its address says: it overlaps whatever follows.
            if (!s.IsAllocated || s.Size == 0 || (s.HasNoBits && (s.Flags & ElfSectionHeader.FlagTls) != 0))
            {
                continue;
            }

            ulong va = AddressOf(s);
            if (VaToRva(va) is not { } rva || s.Size > uint.MaxValue - rva)
            {
                continue;
            }

            list.Add(new ElfAddressRange(
                s.Index,
                s.Name,
                rva,
                (uint)s.Size,
                s.HasNoBits ? 0 : (uint)Math.Min(s.Offset, uint.MaxValue),
                s.HasNoBits ? 0 : (uint)Math.Min(s.Size, uint.MaxValue),
                read: true,
                write: s.IsWritable,
                execute: s.IsExecutable));
        }

        if (list.Count == 0)
        {
            // No section headers to go by: the segments are the only map there is.
            int n = 0;
            foreach (var seg in Segments.Where(s => s.IsLoad && s.MemorySize > 0))
            {
                if (VaToRva(seg.VirtualAddress) is not { } rva || seg.MemorySize > uint.MaxValue - rva)
                {
                    continue;
                }

                list.Add(new ElfAddressRange(
                    seg.Index,
                    $"LOAD{n++}",
                    rva,
                    (uint)seg.MemorySize,
                    (uint)Math.Min(seg.Offset, uint.MaxValue),
                    (uint)Math.Min(Math.Min(seg.FileSize, seg.MemorySize), uint.MaxValue),
                    read: (seg.Flags & ElfSegment.FlagRead) != 0,
                    write: (seg.Flags & ElfSegment.FlagWrite) != 0,
                    execute: (seg.Flags & ElfSegment.FlagExecute) != 0));
            }
        }

        return list.OrderBy(s => s.Rva).ToList<IBinarySection>();
    }

    private string? ParseInterpreter()
    {
        var interp = Segments.FirstOrDefault(s => s.Type == (uint)SegmentType.Interp);
        if (interp is null || interp.Offset >= (ulong)_data.Length)
        {
            return null;
        }

        string path = ElfReader.StringAt(_data.Span, (long)interp.Offset, (int)Math.Min(interp.FileSize, 4096));
        return path.Length == 0 ? null : path;
    }

    private IReadOnlyList<ElfDynamicEntry> ParseDynamic()
    {
        // The dynamic segment is what the loader reads; the section is the same bytes, when there is one.
        long offset;
        ulong size;
        var section = SectionHeaders.FirstOrDefault(s => s.Type == ElfSectionHeader.TypeDynamic);
        var segment = Segments.FirstOrDefault(s => s.Type == (uint)SegmentType.Dynamic);
        if (segment is not null)
        {
            offset = (long)segment.Offset;
            size = segment.FileSize;
        }
        else if (section is not null)
        {
            offset = (long)section.Offset;
            size = section.Size;
        }
        else
        {
            return Array.Empty<ElfDynamicEntry>();
        }

        int entrySize = Header.Is64Bit ? 16 : 8;
        int count = (int)Math.Min(size / (ulong)entrySize, MaxDynamicEntries);
        var raw = new List<(long Tag, ulong Value)>(count);
        var r = Reader(offset);
        for (int i = 0; i < count; i++)
        {
            long tag = r.SWord();
            ulong value = r.Word();
            raw.Add((tag, value));
            if (tag == ElfDynamicEntry.Null)
            {
                break;
            }
        }

        // DT_STRTAB is an address; find its bytes. A section's own string table (sh_link) is the fallback.
        long strings = -1;
        ulong stringsSize = 0;
        var strtab = raw.FirstOrDefault(d => d.Tag == ElfDynamicEntry.StrTab);
        if (strtab.Tag == ElfDynamicEntry.StrTab && VaToOffset(strtab.Value) is { } at)
        {
            strings = at;
            stringsSize = raw.FirstOrDefault(d => d.Tag == ElfDynamicEntry.StrSize).Value;
        }
        else if (section is not null && section.Link < SectionHeaders.Count)
        {
            strings = (long)SectionHeaders[(int)section.Link].Offset;
            stringsSize = SectionHeaders[(int)section.Link].Size;
        }

        return raw.Select(d =>
        {
            string? text = null;
            if (d.Tag is ElfDynamicEntry.Needed or ElfDynamicEntry.SoName or ElfDynamicEntry.RPath or ElfDynamicEntry.RunPath
                && strings >= 0 && (stringsSize == 0 || d.Value < stringsSize))
            {
                text = ElfReader.StringAt(_data.Span, strings + (long)Math.Min(d.Value, int.MaxValue), 1024);
            }

            return new ElfDynamicEntry(d.Tag, d.Value, text);
        }).ToList();
    }

    private IReadOnlyList<ElfSymbol> ParseSymbols(bool dynamic)
    {
        uint type = dynamic ? ElfSectionHeader.TypeDynSym : ElfSectionHeader.TypeSymTab;
        var table = SectionHeaders.FirstOrDefault(s => s.Type == type);
        long offset;
        int count;
        long strings;
        ulong stringsSize;
        int entrySize = Header.Is64Bit ? 24 : 16;

        if (table is not null)
        {
            if (table.Link >= SectionHeaders.Count)
            {
                throw new ElfParseException($"{table.Name} names string table {table.Link}, which is not a section.");
            }

            var names = SectionHeaders[(int)table.Link];
            offset = (long)table.Offset;
            count = (int)Math.Min(table.Size / (ulong)entrySize, MaxSymbols);
            strings = (long)names.Offset;
            stringsSize = names.Size;
        }
        else if (dynamic && DynamicSymbolsFromDynamic() is { } fromDynamic)
        {
            (offset, count, strings, stringsSize) = fromDynamic;
        }
        else
        {
            return Array.Empty<ElfSymbol>();
        }

        var list = new List<ElfSymbol>(count);
        var r = Reader(offset);
        for (int i = 0; i < count; i++)
        {
            uint name;
            ulong value;
            ulong size;
            byte info;
            byte other;
            ushort shndx;
            if (Header.Is64Bit)
            {
                name = r.U32();
                info = r.U8();
                other = r.U8();
                shndx = r.U16();
                value = r.U64();
                size = r.U64();
            }
            else
            {
                name = r.U32();
                value = r.U32();
                size = r.U32();
                info = r.U8();
                other = r.U8();
                shndx = r.U16();
            }

            // An object file's symbol is an offset into its section; give it the address the section was laid out at.
            if (Header.Type == ElfType.Relocatable && shndx is > 0 and < 0xFF00 && _sectionRvas.TryGetValue(shndx, out ulong sectionRva))
            {
                value += ImageBase + sectionRva;
            }

            string text = name < stringsSize ? ElfReader.StringAt(_data.Span, strings + name, 1024) : string.Empty;
            list.Add(new ElfSymbol(i, text, value, size, (SymbolBinding)(info >> 4), (ElfSymbolType)(info & 0xF), other, shndx, dynamic));
        }

        return list;
    }

    /// <summary>
    /// Finds <c>.dynsym</c> through the dynamic section when there are no section headers. The table's length is
    /// not stated anywhere; the hash table's chain count gives it.
    /// </summary>
    private (long Offset, int Count, long Strings, ulong StringsSize)? DynamicSymbolsFromDynamic()
    {
        ulong Value(long tag) => Dynamic.FirstOrDefault(d => d.Tag == tag)?.Value ?? 0;

        if (VaToOffset(Value(ElfDynamicEntry.SymTab)) is not { } symtab || VaToOffset(Value(ElfDynamicEntry.StrTab)) is not { } strtab)
        {
            return null;
        }

        int count = 0;
        if (VaToOffset(Value(ElfDynamicEntry.Hash)) is { } hash)
        {
            var r = Reader(hash);
            r.U32();   // nbucket
            count = (int)Math.Min(r.U32(), MaxSymbols);
        }
        else if (VaToOffset(Value(ElfDynamicEntry.GnuHash)) is { } gnu)
        {
            count = GnuHashSymbolCount(gnu);
        }

        return count == 0 ? null : (symtab, count, strtab, Value(ElfDynamicEntry.StrSize));
    }

    /// <summary>The GNU hash table does not state its symbol count: it is one past the last chain's end.</summary>
    private int GnuHashSymbolCount(long offset)
    {
        var r = Reader(offset);
        uint buckets = r.U32();
        uint symbolOffset = r.U32();
        uint bloomSize = r.U32();
        r.U32();   // bloom shift
        if (buckets > MaxSymbols || bloomSize > MaxSymbols)
        {
            return 0;
        }

        r.Skip(checked((int)bloomSize * (Header.Is64Bit ? 8 : 4)));
        uint last = 0;
        for (uint i = 0; i < buckets; i++)
        {
            last = Math.Max(last, r.U32());
        }

        if (last < symbolOffset)
        {
            return (int)symbolOffset;
        }

        long chains = r.Position;
        r.Seek(chains + (long)(last - symbolOffset) * 4);
        while ((r.U32() & 1) == 0 && last < MaxSymbols)
        {
            last++;
        }

        return (int)last + 1;
    }

    /// <summary>
    /// Attaches the library and version each imported symbol was linked against, from <c>.gnu.version</c> and
    /// <c>.gnu.version_r</c>. An ELF import names only a symbol; the version is what says which library it came from.
    /// </summary>
    private IReadOnlyList<ElfSymbol> ApplyVersions()
    {
        var versym = SectionHeaders.FirstOrDefault(s => s.Type == ElfSectionHeader.TypeGnuVerSym);
        var verneed = SectionHeaders.FirstOrDefault(s => s.Type == ElfSectionHeader.TypeGnuVerNeed);
        if (versym is null || verneed is null || DynamicSymbols.Count == 0 || verneed.Link >= SectionHeaders.Count)
        {
            return DynamicSymbols;
        }

        var strings = SectionHeaders[(int)verneed.Link];
        var needed = new Dictionary<ushort, (string File, string Version)>();
        var r = Reader((long)verneed.Offset);
        long entry = (long)verneed.Offset;
        for (int n = 0; n < 4096; n++)
        {
            r.Seek(entry);
            r.U16();   // vn_version
            ushort auxCount = r.U16();
            uint file = r.U32();
            uint aux = r.U32();
            uint next = r.U32();
            string fileName = ElfReader.StringAt(_data.Span, (long)strings.Offset + file, 256);
            long auxAt = entry + aux;
            for (int a = 0; a < auxCount && a < 4096; a++)
            {
                r.Seek(auxAt);
                r.U32();   // vna_hash
                r.U16();   // vna_flags
                ushort other = r.U16();
                uint name = r.U32();
                uint auxNext = r.U32();
                needed[other] = (fileName, ElfReader.StringAt(_data.Span, (long)strings.Offset + name, 256));
                if (auxNext == 0)
                {
                    break;
                }

                auxAt += auxNext;
            }

            if (next == 0)
            {
                break;
            }

            entry += next;
        }

        var versions = Reader((long)versym.Offset);
        var list = new List<ElfSymbol>(DynamicSymbols.Count);
        foreach (var symbol in DynamicSymbols)
        {
            ushort index = versions.CanRead(2) ? (ushort)(versions.U16() & 0x7FFF) : (ushort)0;
            list.Add(!symbol.IsDefined && needed.TryGetValue(index, out var v)
                ? symbol with { Library = v.File, Version = v.Version }
                : symbol);
        }

        return list;
    }

    private IReadOnlyList<ElfRelocation> ParseRelocations()
    {
        var list = new List<ElfRelocation>();
        var tables = SectionHeaders
            .Where(s => s.Type is ElfSectionHeader.TypeRela or ElfSectionHeader.TypeRel
                        && s.Link < SectionHeaders.Count && SectionHeaders[(int)s.Link].Type == ElfSectionHeader.TypeDynSym)
            .Select(s => (Offset: (long)s.Offset, Size: s.Size, Rela: s.Type == ElfSectionHeader.TypeRela, s.Name))
            .ToList();

        if (tables.Count == 0 && SectionHeaders.Count == 0)
        {
            // No section headers: the dynamic section says where the same tables are.
            ulong Value(long tag) => Dynamic.FirstOrDefault(d => d.Tag == tag)?.Value ?? 0;
            void From(long at, long size, bool rela, string name)
            {
                if (Value(at) is > 0 and var va && VaToOffset(va) is { } offset)
                {
                    tables.Add((offset, Value(size), rela, name));
                }
            }

            From(ElfDynamicEntry.Rela, ElfDynamicEntry.RelaSize, rela: true, "DT_RELA");
            From(ElfDynamicEntry.Rel, ElfDynamicEntry.RelSize, rela: false, "DT_REL");
            From(ElfDynamicEntry.JmpRel, ElfDynamicEntry.PltRelSize, rela: Value(ElfDynamicEntry.PltRel) == (ulong)ElfDynamicEntry.Rela, "DT_JMPREL");
        }

        foreach (var (offset, size, rela, name) in tables)
        {
            int entrySize = (Header.Is64Bit ? 16 : 8) + (rela ? (Header.Is64Bit ? 8 : 4) : 0);
            long count = (long)Math.Min(size / (ulong)entrySize, (ulong)(MaxRelocations - list.Count));
            var r = Reader(offset);
            for (long i = 0; i < count; i++)
            {
                ulong at = r.Word();
                ulong info = r.Word();
                long addend = rela ? r.SWord() : 0;
                int symbol = (int)(Header.Is64Bit ? info >> 32 : info >> 8);
                uint type = (uint)(Header.Is64Bit ? info & 0xFFFFFFFF : info & 0xFF);
                list.Add(new ElfRelocation(at, type, symbol, addend, name));
            }
        }

        return list;
    }

    /// <summary>The GNU build-id: what the toolchain itself uses to tell one build from another.</summary>
    private string? ParseBuildId()
    {
        var notes = SectionHeaders.Where(s => s.Type == ElfSectionHeader.TypeNote).Select(s => ((long)s.Offset, s.Size))
            .Concat(Segments.Where(s => s.Type == (uint)SegmentType.Note).Select(s => ((long)s.Offset, s.FileSize)));
        foreach (var (offset, size) in notes)
        {
            if (offset < 0 || offset >= _data.Length)
            {
                continue;
            }

            var r = Reader(offset);
            long end = offset + (long)Math.Min(size, (ulong)(_data.Length - offset));
            while (r.Position + 12 <= end)
            {
                uint nameSize = r.U32();
                uint descSize = r.U32();
                uint type = r.U32();
                if (nameSize > 256 || descSize > 4096)
                {
                    break;
                }

                string name = ElfReader.StringAt(_data.Span, r.Position, (int)nameSize);
                r.Skip((int)((nameSize + 3) & ~3u));
                var desc = r.Bytes((int)descSize);
                r.Skip((int)(((descSize + 3) & ~3u) - descSize));
                if (type == 3 && name == "GNU" && desc.Length > 0)
                {
                    return Convert.ToHexStringLower(desc);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Without a build-id, the headers stand in for one: the ELF header, program headers and section headers
    /// change with every rebuild that moves anything, and a byte patch to the code changes none of them — so a
    /// project follows a patched copy of the binary, as it does for a PE.
    /// </summary>
    private string HeaderHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var span = _data.Span;
        hash.AppendData(span[..Math.Min(span.Length, Header.Is64Bit ? 64 : 52)]);

        void Append(ulong offset, long length)
        {
            var all = _data.Span;
            if (offset < (ulong)all.Length && length > 0)
            {
                hash.AppendData(all.Slice((int)offset, (int)Math.Min(length, all.Length - (long)offset)));
            }
        }

        Append(Header.ProgramHeaderOffset, (long)Header.ProgramHeaderCount * Header.ProgramHeaderEntrySize);
        Append(Header.SectionHeaderOffset, (long)Header.SectionHeaderCount * Header.SectionHeaderEntrySize);
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..32];
    }

    // ---------------------------------------------------------------------
    // What the analysis sees
    // ---------------------------------------------------------------------

    /// <summary>
    /// A relocation that makes the loader write another module's address into a slot: the GOT entries code calls
    /// and loads through. Copies (the symbol moves into this image), relative and IFUNC relocations name no import.
    /// </summary>
    private bool IsImportSlot(uint type) => (ElfMachine)Header.Machine switch
    {
        ElfMachine.X86_64 => type is 1 or 6 or 7,     // R_X86_64_64, GLOB_DAT, JUMP_SLOT
        ElfMachine.I386 => type is 1 or 6 or 7,       // R_386_32, GLOB_DAT, JMP_SLOT
        ElfMachine.AArch64 => type is 257 or 1025 or 1026,   // ABS64, GLOB_DAT, JUMP_SLOT
        ElfMachine.Arm => type is 2 or 21 or 22,      // ABS32, GLOB_DAT, JUMP_SLOT
        _ => true,
    };

    /// <summary>
    /// The GOT slots of functions this image defines and calls through its own PLT, each as the <c>name@plt</c> its stub
    /// is called by.
    /// </summary>
    private Dictionary<uint, ImportedSymbol> LocalSlots()
    {
        var slots = new Dictionary<uint, ImportedSymbol>();
        foreach (var reloc in Relocations)
        {
            if (reloc.SymbolIndex <= 0 || reloc.SymbolIndex >= DynamicSymbols.Count || !IsImportSlot(reloc.Type))
            {
                continue;
            }

            var symbol = DynamicSymbols[reloc.SymbolIndex];
            if (symbol.IsDefined && symbol.Name.Length > 0 && VaToRva(reloc.Offset) is { } slot)
            {
                slots.TryAdd(slot, new ImportedSymbol(string.Empty, symbol.Name + "@plt", null, slot, IsDelayLoad: false));
            }
        }

        return slots;
    }

    private IReadOnlyList<ImportedSymbol> BuildImports()
    {
        var list = new List<ImportedSymbol>();
        var seen = new HashSet<uint>();
        string? onlyLibrary = Needed is [var single] ? single : null;
        foreach (var reloc in Relocations)
        {
            if (reloc.SymbolIndex <= 0 || reloc.SymbolIndex >= DynamicSymbols.Count || !IsImportSlot(reloc.Type))
            {
                continue;
            }

            var symbol = DynamicSymbols[reloc.SymbolIndex];
            if (symbol.IsDefined || symbol.Name.Length == 0 || VaToRva(reloc.Offset) is not { } slot || !seen.Add(slot))
            {
                continue;
            }

            list.Add(new ImportedSymbol(symbol.Library ?? onlyLibrary ?? string.Empty, symbol.Name, null, slot, IsDelayLoad: false));
        }

        return list;
    }

    private IReadOnlyList<ExportedSymbol> BuildExports()
    {
        // A program's own globals are in .dynsym only when something links against it; a library's are its API.
        var list = new List<ExportedSymbol>();
        var seen = new HashSet<(string, uint)>();
        foreach (var symbol in DynamicSymbols)
        {
            if (!symbol.IsDefined || symbol.Name.Length == 0 || symbol.Binding == SymbolBinding.Local
                || symbol.Type is not (ElfSymbolType.Function or ElfSymbolType.Object or ElfSymbolType.GnuIndirectFunction or ElfSymbolType.NoType)
                || (symbol.Visibility & 3) is 1 or 2
                || symbol.SectionIndex == ElfSymbol.Absolute
                || VaToRva(symbol.Value) is not { } rva || rva == 0
                || !seen.Add((symbol.Name, rva)))
            {
                continue;
            }

            list.Add(new ExportedSymbol(symbol.Name, null, rva, null));
        }

        return list;
    }

    /// <summary>
    /// Code calls an import through a PLT stub: a few bytes that jump through the import's GOT slot. Finding the
    /// jump in each stub is what lets <c>call 0x401030</c> read as <c>call printf</c>. The stubs are recognised
    /// by their bytes rather than by counting entries, because the layout depends on the linker and on whether
    /// control-flow protection is on (<c>.plt.sec</c>).
    /// </summary>
    private IReadOnlyList<(uint Rva, ImportedSymbol Import)> FindPltStubs(IReadOnlyDictionary<uint, ImportedSymbol> bySlot)
    {
        if (bySlot.Count == 0)
        {
            return Array.Empty<(uint, ImportedSymbol)>();
        }

        if (Architecture == Architecture.Arm64)
        {
            return FindArm64PltStubs(bySlot);
        }

        if (Architecture is not (Architecture.X86 or Architecture.X64))
        {
            return Array.Empty<(uint, ImportedSymbol)>();
        }

        ulong gotBase = Dynamic.FirstOrDefault(d => d.Tag == ElfDynamicEntry.PltGot)?.Value
                        ?? (SectionHeader(".got.plt") is { } gotPlt ? AddressOf(gotPlt) : 0);
        var list = new List<(uint, ImportedSymbol)>();
        var claimed = new HashSet<uint>();
        foreach (var section in SectionHeaders.Where(s => s.IsExecutable && s.Name.StartsWith(".plt", StringComparison.Ordinal)))
        {
            var bytes = SectionBytes(section);
            ulong start = AddressOf(section);
            int entry = section.EntrySize is >= 8 and <= 32 ? (int)section.EntrySize : 16;
            for (int i = 0; i + 6 <= bytes.Length; i++)
            {
                ulong? slotVa = null;
                if (bytes[i] == 0xFF && bytes[i + 1] == 0x25)
                {
                    int disp = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(i + 2, 4));
                    // x64: jmp [rip+disp]; x86: jmp [abs32].
                    slotVa = Is64Bit ? start + (ulong)(i + 6) + (ulong)(long)disp : (uint)disp;
                }
                else if (!Is64Bit && bytes[i] == 0xFF && bytes[i + 1] == 0xA3 && gotBase != 0)
                {
                    // x86 position-independent: jmp [ebx+disp], with ebx holding the GOT's address.
                    int disp = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(i + 2, 4));
                    slotVa = (uint)(gotBase + (ulong)(long)disp);
                }

                if (slotVa is not { } va || VaToRva(va) is not { } slot || !bySlot.TryGetValue(slot, out var import))
                {
                    continue;
                }

                uint stub = (uint)(VaToRva(start) ?? 0) + (uint)(i - (i % entry));
                if (claimed.Add(stub))
                {
                    list.Add((stub, import));
                }

                i += 5;
            }
        }

        return list;
    }

    /// <summary>
    /// AArch64 PLT stubs: <c>adrp x16, page</c>, <c>ldr x17, [x16, #off]</c> (the GOT slot), <c>add x16, x16, #off</c>,
    /// <c>br x17</c> — opened by <c>bti c</c> when branch protection is on, which is then where calls land.
    /// </summary>
    private IReadOnlyList<(uint Rva, ImportedSymbol Import)> FindArm64PltStubs(IReadOnlyDictionary<uint, ImportedSymbol> bySlot)
    {
        const uint BtiC = 0xD503245F;
        var list = new List<(uint, ImportedSymbol)>();
        var claimed = new HashSet<uint>();
        foreach (var section in SectionHeaders.Where(s => s.IsExecutable && s.Name.StartsWith(".plt", StringComparison.Ordinal)))
        {
            var bytes = SectionBytes(section);
            ulong start = AddressOf(section);
            for (int i = 0; i + 8 <= bytes.Length; i += 4)
            {
                uint adrp = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i, 4));
                uint ldr = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i + 4, 4));
                if ((adrp & 0x9F00001F) != 0x90000010 || (ldr & 0xFFC003FF) != 0xF9400211)
                {
                    continue;
                }

                // adrp's 21-bit page offset is immhi:immlo; the ldr's 12-bit offset is scaled by 8.
                long pages = ((long)(((adrp >> 5) & 0x7FFFF) << 2 | ((adrp >> 29) & 3)) << 43) >> 43;
                ulong pc = start + (ulong)i;
                ulong slotVa = (ulong)((long)(pc & ~0xFFFUL) + (pages << 12)) + (((ldr >> 10) & 0xFFF) * 8);
                if (VaToRva(slotVa) is not { } slot || !bySlot.TryGetValue(slot, out var import))
                {
                    continue;
                }

                bool landingPad = i >= 4 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i - 4, 4)) == BtiC;
                uint stub = (uint)(VaToRva(start) ?? 0) + (uint)(landingPad ? i - 4 : i);
                if (claimed.Add(stub))
                {
                    list.Add((stub, import));
                }

                i += 4;
            }
        }

        return list;
    }

    /// <summary>
    /// A C program's <c>_start</c> hands <c>main</c> to <c>__libc_start_main</c> as its first argument, so in a
    /// stripped binary that argument is the one place <c>main</c> is named at all. Read from the bytes of
    /// <c>_start</c>: the last <c>mov rdi, imm32</c> or <c>lea rdi, [rip+disp]</c> (x64), or the last
    /// <c>push imm32</c> (x86), before the first call. Only when the program imports <c>__libc_start_main</c>.
    /// </summary>
    private uint? MainFromStart()
    {
        if (EntryPointRva == 0 || !Imports.Any(i => i.Name == "__libc_start_main"))
        {
            return null;
        }

        var code = ReadAtRva(EntryPointRva, 64).Span;
        ulong? candidate = null;
        for (int i = 0; i < code.Length; i++)
        {
            if (code[i] == 0xE8 || (i + 1 < code.Length && code[i] == 0xFF && code[i + 1] == 0x15))
            {
                break;   // the call: main was loaded before it
            }

            // mov r64, imm32 and lea r64, [rip+disp32] are seven bytes; step over each whole, so an immediate that
            // happens to hold an E8 is not taken for the call.
            if (Is64Bit && i + 7 <= code.Length && code[i] is 0x48 or 0x49 && code[i + 1] == 0xC7 && (code[i + 2] & 0xF8) == 0xC0)
            {
                if (code[i] == 0x48 && code[i + 2] == 0xC7)
                {
                    candidate = (ulong)System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(code.Slice(i + 3, 4));
                }

                i += 6;
            }
            else if (Is64Bit && i + 7 <= code.Length && code[i] is 0x48 or 0x4C && code[i + 1] == 0x8D && (code[i + 2] & 0xC7) == 0x05)
            {
                if (code[i] == 0x48 && code[i + 2] == 0x3D)
                {
                    int disp = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(code.Slice(i + 3, 4));
                    candidate = RvaToVa(EntryPointRva) + (ulong)(i + 7) + (ulong)(long)disp;
                }

                i += 6;
            }
            else if (!Is64Bit && i + 5 <= code.Length && code[i] == 0x68)
            {
                candidate = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(code.Slice(i + 1, 4));
                i += 4;
            }
        }

        return candidate is { } va && VaToRva(va) is { } rva && SectionFromRva(rva) is { IsExecutable: true } ? rva : null;
    }

    private IReadOnlyList<ImageSymbol> BuildImageSymbols()
    {
        var list = new List<ImageSymbol>();
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rva, import) in PltStubs)
        {
            // Name the stub after the import, once: calls to it then read as calls to the import itself.
            if (import.Name is { } name && named.Add(name))
            {
                list.Add(new ImageSymbol(name, rva, 0, ImageSymbolKind.Stub));
            }
        }

        foreach (var (rva, name) in LocalPltStubs)
        {
            if (named.Add(name))
            {
                list.Add(new ImageSymbol(name, rva, 0, ImageSymbolKind.Stub));
            }
        }

        if (!StaticSymbols.Any(s => s.Name == "main") && MainFromStart() is { } main)
        {
            list.Add(new ImageSymbol("main", main, 0, ImageSymbolKind.Function));
        }

        foreach (var symbol in StaticSymbols.Concat(DynamicSymbols))
        {
            if (!symbol.IsDefined || symbol.Name.Length == 0 || symbol.SectionIndex >= 0xFF00
                || VaToRva(symbol.Value) is not { } rva || rva == 0 || SectionFromRva(rva) is not { } section)
            {
                continue;
            }

            var kind = symbol.Type switch
            {
                ElfSymbolType.Function or ElfSymbolType.GnuIndirectFunction when section.IsExecutable => ImageSymbolKind.Function,
                ElfSymbolType.Object => ImageSymbolKind.Data,
                _ => (ImageSymbolKind?)null,
            };
            if (kind is { } k)
            {
                list.Add(new ImageSymbol(symbol.Name, rva, (uint)Math.Min(symbol.Size, uint.MaxValue), k));
            }
        }

        return list;
    }
}
