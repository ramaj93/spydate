namespace Spydate.Core.Binary;

/// <summary>The container a file was recognised as. The format decides how it is parsed, not how it is analysed.</summary>
public enum BinaryFormat
{
    Unknown,

    /// <summary>Portable Executable: a Windows .exe, .dll or .sys, native or .NET.</summary>
    Pe,

    /// <summary>Executable and Linkable Format: a Linux, BSD or Android native binary or shared object.</summary>
    Elf,

    /// <summary>A zip-based Java archive. An APK is a zip too; <see cref="BinaryImage.Detect"/> tells them apart.</summary>
    Jar,

    /// <summary>A Dalvik executable on its own: an Android app's code, as <c>d8</c> writes it, without its package.</summary>
    Dex,

    /// <summary>An Android package: a zip holding <c>AndroidManifest.xml</c>, Dalvik bytecode and native libraries.</summary>
    Apk,
}

/// <summary>
/// The instruction set of a binary's native code. It is what picks a decoder, so it is kept apart from the
/// container: an ELF and a PE for the same processor disassemble the same way.
/// </summary>
public enum Architecture
{
    Unknown,
    X86,
    X64,
    Arm,
    Arm64,
}

/// <summary>
/// One section of an image: a named, contiguous range of its address space with its own permissions. Addresses
/// are relative to <see cref="IBinaryImage.ImageBase"/>, the way the project file stores them.
/// </summary>
public interface IBinarySection
{
    int Index { get; }

    string Name { get; }

    /// <summary>Where the section starts, relative to the image base.</summary>
    uint Rva { get; }

    /// <summary>How much address space it covers once loaded.</summary>
    uint Extent { get; }

    /// <summary>
    /// The size the section declares for itself in memory, as written. It can be zero — a PE with no virtual
    /// size means the raw size — so code that needs "how much is loaded" reads <see cref="Extent"/>, and code
    /// that needs to reproduce a format's own arithmetic reads this.
    /// </summary>
    uint VirtualSize { get; }

    uint EndRva { get; }

    /// <summary>Where its bytes are in the file, and how many of them there are. Zero-filled space has none.</summary>
    uint RawOffset { get; }

    uint RawSize { get; }

    bool IsExecutable { get; }

    bool IsReadable { get; }

    bool IsWritable { get; }

    /// <summary>A compact "RWX" string, such as "R-X".</summary>
    string Permissions { get; }

    bool ContainsRva(uint rva);
}

/// <summary>
/// A symbol the image offers to others. A forwarder names another module's symbol and has no address of its own.
/// </summary>
public sealed record ExportedSymbol(string? Name, uint? Ordinal, uint Rva, string? Forwarder)
{
    public bool IsForwarder => Forwarder is not null;

    public string DisplayName => Name ?? $"#{Ordinal}";
}

/// <summary>
/// A symbol the image takes from another module, and the slot the loader fills in with its address — the
/// address code calls through, which is why it can be named.
/// </summary>
public sealed record ImportedSymbol(string Module, string? Name, ushort? Ordinal, uint SlotRva, bool IsDelayLoad)
{
    public string DisplayName => Name ?? $"#{Ordinal}";
}

/// <summary>
/// A loaded binary, as the analysis sees it: an address space made of sections, the bytes behind them, an entry
/// point, and the symbols it exports and imports.
///
/// This is deliberately small. It is exactly what disassembly, function discovery, the decompiler and the project
/// file ask of a binary — measured, not guessed — and nothing a format happens to have besides. A format's own
/// structures (PE's data directories, ELF's segments) stay on the concrete type, where the views that show them
/// can reach them. A feature a second format will also have is added as its own interface, such as
/// <see cref="IUnwindInfoSource"/>, rather than as a member every format must fake.
/// </summary>
public interface IBinaryImage
{
    BinaryFormat Format { get; }

    Architecture Architecture { get; }

    string? Path { get; }

    string FileName { get; }

    ReadOnlyMemory<byte> Data { get; }

    long Length { get; }

    int Bitness { get; }

    bool Is64Bit { get; }

    /// <summary>The address everything else is relative to. For an ELF, the lowest loadable address.</summary>
    ulong ImageBase { get; }

    /// <summary>How much address space the image occupies once loaded, from <see cref="ImageBase"/>.</summary>
    ulong ImageSize { get; }

    /// <summary>Zero when the image has no entry point.</summary>
    uint EntryPointRva { get; }

    ulong EntryPointVa { get; }

    /// <summary>A library rather than a program: a PE DLL, an ELF shared object.</summary>
    bool IsLibrary { get; }

    IReadOnlyList<IBinarySection> Sections { get; }

    IBinarySection? SectionFromRva(uint rva);

    IBinarySection? SectionFromVa(ulong va);

    ulong RvaToVa(uint rva);

    uint? VaToRva(ulong va);

    uint? RvaToOffset(uint rva);

    uint? OffsetToRva(uint offset);

    uint? VaToOffset(ulong va);

    /// <summary>Bytes at an address, or empty when it is not backed by the file.</summary>
    ReadOnlyMemory<byte> ReadAtVa(ulong va, int length);

    ReadOnlyMemory<byte> ReadAtRva(uint rva, int length);

    /// <summary>A pointer-sized value at an address, or null when it cannot be read.</summary>
    ulong? ReadPointerAtRva(uint rva);

    IReadOnlyList<ExportedSymbol> Exports { get; }

    IReadOnlyList<ImportedSymbol> Imports { get; }

    /// <summary>Things the parser tolerated rather than rejected. Untrusted input is reported on, never thrown on.</summary>
    IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// What tells this build of the file from another with the same name and size — what a project file is
    /// matched on, so annotations never land on a different build at addresses that mean something else there.
    /// </summary>
    string Fingerprint { get; }
}

/// <summary>
/// An image that declares where its functions begin and end, as unwind or exception tables do: PE's
/// <c>.pdata</c>, and in time ELF's <c>.eh_frame</c>. Exact bounds from the compiler beat anything inferred.
/// </summary>
public interface IUnwindInfoSource
{
    /// <summary>Each declared function's start and end, relative to the image base. Chained fragments are left out.</summary>
    IReadOnlyList<(uint BeginRva, uint EndRva)> UnwindRanges { get; }
}

/// <summary>What an <see cref="ImageSymbol"/> names.</summary>
public enum ImageSymbolKind
{
    Function,

    /// <summary>A variable or constant.</summary>
    Data,

    /// <summary>A linker-made stub that jumps to an import, such as an ELF PLT entry. Named after the import.</summary>
    Stub,
}

/// <summary>A named address the file itself declares, besides its exports: a local function, a global variable.</summary>
public sealed record ImageSymbol(string Name, uint Rva, uint Size, ImageSymbolKind Kind);

/// <summary>
/// An image that carries its own names for addresses beyond what it exports — an ELF's <c>.symtab</c>, and the
/// PLT stubs that stand for its imports. A PE's equivalent lives in a PDB, which is loaded separately.
/// </summary>
public interface ISymbolSource
{
    IReadOnlyList<ImageSymbol> Symbols { get; }
}
