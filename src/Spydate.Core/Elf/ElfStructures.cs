using Spydate.Core.Binary;

namespace Spydate.Core.Elf;

/// <summary><c>e_type</c>: what the file is for.</summary>
public enum ElfType : ushort
{
    None = 0,

    /// <summary>An object file, before linking. Its sections have no addresses of their own.</summary>
    Relocatable = 1,

    Executable = 2,

    /// <summary>A shared object — a library, or a position-independent executable.</summary>
    Shared = 3,

    Core = 4,
}

/// <summary><c>e_machine</c>, for the processors worth naming. Anything else is shown as its number.</summary>
public enum ElfMachine : ushort
{
    None = 0,
    Sparc = 2,
    I386 = 3,
    M68K = 4,
    Mips = 8,
    PowerPc = 20,
    PowerPc64 = 21,
    S390 = 22,
    Arm = 40,
    SuperH = 42,
    Sparc64 = 43,
    IA64 = 50,
    X86_64 = 62,
    AArch64 = 183,
    RiscV = 243,
    LoongArch = 258,
}

/// <summary><c>p_type</c> of a program header.</summary>
public enum SegmentType : uint
{
    Null = 0,
    Load = 1,
    Dynamic = 2,
    Interp = 3,
    Note = 4,
    ShLib = 5,
    Phdr = 6,
    Tls = 7,
    GnuEhFrame = 0x6474E550,
    GnuStack = 0x6474E551,
    GnuRelro = 0x6474E552,
    GnuProperty = 0x6474E553,
}

/// <summary>The ELF header, as written.</summary>
public sealed record ElfHeader(
    bool Is64Bit,
    bool IsBigEndian,
    byte OsAbi,
    byte AbiVersion,
    ElfType Type,
    ushort Machine,
    uint Version,
    ulong Entry,
    ulong ProgramHeaderOffset,
    ulong SectionHeaderOffset,
    uint Flags,
    ushort HeaderSize,
    ushort ProgramHeaderEntrySize,
    int ProgramHeaderCount,
    ushort SectionHeaderEntrySize,
    int SectionHeaderCount,
    int SectionNameTableIndex)
{
    public string MachineName => Enum.IsDefined(typeof(ElfMachine), Machine) ? ((ElfMachine)Machine).ToString() : $"machine {Machine}";

    public string ClassName => Is64Bit ? "ELF64" : "ELF32";

    public string ByteOrder => IsBigEndian ? "big-endian" : "little-endian";

    public string OsAbiName => OsAbi switch
    {
        0 => "System V",
        3 => "Linux",
        6 => "Solaris",
        9 => "FreeBSD",
        12 => "OpenBSD",
        97 => "ARM",
        255 => "standalone",
        _ => $"ABI {OsAbi}",
    };
}

/// <summary>A program header: how the loader maps part of the file.</summary>
public sealed record ElfSegment(
    int Index,
    uint Type,
    uint Flags,
    ulong Offset,
    ulong VirtualAddress,
    ulong PhysicalAddress,
    ulong FileSize,
    ulong MemorySize,
    ulong Align)
{
    public const uint FlagExecute = 1;
    public const uint FlagWrite = 2;
    public const uint FlagRead = 4;

    public bool IsLoad => Type == (uint)SegmentType.Load;

    public string TypeName => Enum.IsDefined(typeof(SegmentType), Type) ? ((SegmentType)Type) switch
    {
        SegmentType.Null => "NULL",
        SegmentType.Load => "LOAD",
        SegmentType.Dynamic => "DYNAMIC",
        SegmentType.Interp => "INTERP",
        SegmentType.Note => "NOTE",
        SegmentType.ShLib => "SHLIB",
        SegmentType.Phdr => "PHDR",
        SegmentType.Tls => "TLS",
        SegmentType.GnuEhFrame => "GNU_EH_FRAME",
        SegmentType.GnuStack => "GNU_STACK",
        SegmentType.GnuRelro => "GNU_RELRO",
        SegmentType.GnuProperty => "GNU_PROPERTY",
        _ => $"0x{Type:X}",
    } : $"0x{Type:X}";

    public string Permissions => $"{((Flags & FlagRead) != 0 ? 'R' : '-')}{((Flags & FlagWrite) != 0 ? 'W' : '-')}{((Flags & FlagExecute) != 0 ? 'X' : '-')}";
}

/// <summary>A section header, as written. Non-allocated sections (symbol tables, debug info) have no address.</summary>
public sealed record ElfSectionHeader(
    int Index,
    string Name,
    uint Type,
    ulong Flags,
    ulong Address,
    ulong Offset,
    ulong Size,
    uint Link,
    uint Info,
    ulong AddressAlign,
    ulong EntrySize)
{
    public const uint TypeProgBits = 1;
    public const uint TypeSymTab = 2;
    public const uint TypeStrTab = 3;
    public const uint TypeRela = 4;
    public const uint TypeHash = 5;
    public const uint TypeDynamic = 6;
    public const uint TypeNote = 7;
    public const uint TypeNoBits = 8;
    public const uint TypeRel = 9;
    public const uint TypeDynSym = 11;
    public const uint TypeGnuVerNeed = 0x6FFFFFFE;
    public const uint TypeGnuVerSym = 0x6FFFFFFF;

    public const ulong FlagWrite = 0x1;
    public const ulong FlagAlloc = 0x2;
    public const ulong FlagExecute = 0x4;
    public const ulong FlagTls = 0x400;

    public bool IsAllocated => (Flags & FlagAlloc) != 0;

    public bool IsExecutable => (Flags & FlagExecute) != 0;

    public bool IsWritable => (Flags & FlagWrite) != 0;

    /// <summary>Occupies address space but no file bytes, such as <c>.bss</c>.</summary>
    public bool HasNoBits => Type == TypeNoBits;

    public string TypeName => Type switch
    {
        0 => "NULL",
        TypeProgBits => "PROGBITS",
        TypeSymTab => "SYMTAB",
        TypeStrTab => "STRTAB",
        TypeRela => "RELA",
        TypeHash => "HASH",
        TypeDynamic => "DYNAMIC",
        TypeNote => "NOTE",
        TypeNoBits => "NOBITS",
        TypeRel => "REL",
        TypeDynSym => "DYNSYM",
        14 => "INIT_ARRAY",
        15 => "FINI_ARRAY",
        16 => "PREINIT_ARRAY",
        17 => "GROUP",
        19 => "RELR",
        0x6FFFFFF6 => "GNU_HASH",
        0x6FFFFFFD => "GNU_VERDEF",
        TypeGnuVerNeed => "GNU_VERNEED",
        TypeGnuVerSym => "GNU_VERSYM",
        0x70000001 => "PROC(0x70000001)",
        _ => $"0x{Type:X}",
    };

    public string FlagText => string.Concat(
        IsWritable ? "W" : string.Empty,
        IsAllocated ? "A" : string.Empty,
        IsExecutable ? "X" : string.Empty,
        (Flags & 0x10) != 0 ? "M" : string.Empty,
        (Flags & 0x20) != 0 ? "S" : string.Empty,
        (Flags & FlagTls) != 0 ? "T" : string.Empty);
}

/// <summary><c>STB_*</c>: who can see a symbol.</summary>
public enum SymbolBinding : byte
{
    Local = 0,
    Global = 1,
    Weak = 2,
    GnuUnique = 10,
}

/// <summary><c>STT_*</c>: what a symbol names.</summary>
public enum ElfSymbolType : byte
{
    NoType = 0,
    Object = 1,
    Function = 2,
    Section = 3,
    File = 4,
    Common = 5,
    Tls = 6,
    GnuIndirectFunction = 10,
}

/// <summary>
/// One entry of <c>.dynsym</c> or <c>.symtab</c>. <see cref="Value"/> is the address the file states — for an
/// object file, an offset into the section the symbol belongs to, which <see cref="ElfImage"/> has already added
/// the section's address to.
/// </summary>
public sealed record ElfSymbol(
    int Index,
    string Name,
    ulong Value,
    ulong Size,
    SymbolBinding Binding,
    ElfSymbolType Type,
    byte Visibility,
    ushort SectionIndex,
    bool IsDynamic)
{
    public const ushort Undefined = 0;
    public const ushort Absolute = 0xFFF1;
    public const ushort CommonSection = 0xFFF2;

    public bool IsDefined => SectionIndex != Undefined;

    /// <summary>The library that provides it, when the file's symbol versions say so; imports only.</summary>
    public string? Library { get; init; }

    /// <summary>The version it was linked against, such as <c>GLIBC_2.2.5</c>.</summary>
    public string? Version { get; init; }

    public string BindingName => Binding switch
    {
        SymbolBinding.Local => "LOCAL",
        SymbolBinding.Global => "GLOBAL",
        SymbolBinding.Weak => "WEAK",
        SymbolBinding.GnuUnique => "UNIQUE",
        _ => $"{(byte)Binding}",
    };

    public string TypeName => Type switch
    {
        ElfSymbolType.NoType => "NOTYPE",
        ElfSymbolType.Object => "OBJECT",
        ElfSymbolType.Function => "FUNC",
        ElfSymbolType.Section => "SECTION",
        ElfSymbolType.File => "FILE",
        ElfSymbolType.Common => "COMMON",
        ElfSymbolType.Tls => "TLS",
        ElfSymbolType.GnuIndirectFunction => "IFUNC",
        _ => $"{(byte)Type}",
    };

    public string VisibilityName => (Visibility & 3) switch
    {
        0 => "DEFAULT",
        1 => "INTERNAL",
        2 => "HIDDEN",
        _ => "PROTECTED",
    };
}

/// <summary>One entry of the dynamic section, with its string already resolved where the tag names one.</summary>
public sealed record ElfDynamicEntry(long Tag, ulong Value, string? Text)
{
    public const long Null = 0;
    public const long Needed = 1;
    public const long PltRelSize = 2;
    public const long PltGot = 3;
    public const long Hash = 4;
    public const long StrTab = 5;
    public const long SymTab = 6;
    public const long Rela = 7;
    public const long RelaSize = 8;
    public const long StrSize = 10;
    public const long SoName = 14;
    public const long RPath = 15;
    public const long Rel = 17;
    public const long RelSize = 18;
    public const long PltRel = 20;
    public const long JmpRel = 23;
    public const long RunPath = 29;
    public const long Flags1 = 0x6FFFFFFB;
    public const long GnuHash = 0x6FFFFEF5;

    /// <summary><c>DF_1_PIE</c>: a position-independent executable, not a library.</summary>
    public const ulong Flags1Pie = 0x08000000;

    public string TagName => Tag switch
    {
        Null => "NULL",
        Needed => "NEEDED",
        PltRelSize => "PLTRELSZ",
        PltGot => "PLTGOT",
        Hash => "HASH",
        StrTab => "STRTAB",
        SymTab => "SYMTAB",
        Rela => "RELA",
        RelaSize => "RELASZ",
        9 => "RELAENT",
        StrSize => "STRSZ",
        11 => "SYMENT",
        12 => "INIT",
        13 => "FINI",
        SoName => "SONAME",
        RPath => "RPATH",
        16 => "SYMBOLIC",
        Rel => "REL",
        RelSize => "RELSZ",
        19 => "RELENT",
        PltRel => "PLTREL",
        21 => "DEBUG",
        22 => "TEXTREL",
        JmpRel => "JMPREL",
        24 => "BIND_NOW",
        25 => "INIT_ARRAY",
        26 => "FINI_ARRAY",
        27 => "INIT_ARRAYSZ",
        28 => "FINI_ARRAYSZ",
        RunPath => "RUNPATH",
        30 => "FLAGS",
        32 => "PREINIT_ARRAY",
        33 => "PREINIT_ARRAYSZ",
        0x6FFFFEF5 => "GNU_HASH",
        0x6FFFFFF0 => "VERSYM",
        0x6FFFFFF9 => "RELACOUNT",
        0x6FFFFFFA => "RELCOUNT",
        Flags1 => "FLAGS_1",
        0x6FFFFFFC => "VERDEF",
        0x6FFFFFFD => "VERDEFNUM",
        0x6FFFFFFE => "VERNEED",
        0x6FFFFFFF => "VERNEEDNUM",
        _ => $"0x{Tag:X}",
    };
}

/// <summary>A relocation the loader applies, with the symbol it names (if any) resolved.</summary>
public sealed record ElfRelocation(ulong Offset, uint Type, int SymbolIndex, long Addend, string Table);

/// <summary>
/// A range of the image's address space and the file bytes behind it — a section when the file has section
/// headers, a loadable segment when it does not. This is the view the analysis sees through
/// <see cref="IBinarySection"/>.
/// </summary>
public sealed class ElfAddressRange : IBinarySection
{
    internal ElfAddressRange(int index, string name, uint rva, uint extent, uint rawOffset, uint rawSize, bool read, bool write, bool execute)
    {
        Index = index;
        Name = name;
        Rva = rva;
        Extent = extent;
        RawOffset = rawOffset;
        RawSize = rawSize;
        IsReadable = read;
        IsWritable = write;
        IsExecutable = execute;
    }

    public int Index { get; }

    public string Name { get; }

    public uint Rva { get; }

    public uint Extent { get; }

    public uint VirtualSize => Extent;

    public uint EndRva => Rva + Extent;

    public uint RawOffset { get; }

    public uint RawSize { get; }

    public bool IsExecutable { get; }

    public bool IsReadable { get; }

    public bool IsWritable { get; }

    public string Permissions => $"{(IsReadable ? 'R' : '-')}{(IsWritable ? 'W' : '-')}{(IsExecutable ? 'X' : '-')}";

    public bool ContainsRva(uint rva) => rva >= Rva && rva - Rva < Extent;

    public override string ToString() => $"{Name} 0x{Rva:X}+0x{Extent:X} {Permissions}";
}
