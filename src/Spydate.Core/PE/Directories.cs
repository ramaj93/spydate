namespace Spydate.Core.PE;

/// <summary>A function imported from a module (one IAT slot).</summary>
public sealed record ImportedFunction
{
    /// <summary>Import name, or null when imported by ordinal.</summary>
    public string? Name { get; init; }
    public ushort? Ordinal { get; init; }
    public ushort Hint { get; init; }
    /// <summary>RVA of the IAT slot the loader patches with the resolved address.</summary>
    public required uint IatRva { get; init; }
    public bool IsByOrdinal => Name is null;
    public bool IsDelayLoad { get; init; }

    public string DisplayName => Name ?? $"#{Ordinal}";

    public override string ToString() => DisplayName;
}

/// <summary>An imported DLL and the functions imported from it.</summary>
public sealed record ImportedModule
{
    public required string Name { get; init; }
    public required IReadOnlyList<ImportedFunction> Functions { get; init; }
    public uint TimeDateStamp { get; init; }
    public uint ForwarderChain { get; init; }
    public uint OriginalFirstThunkRva { get; init; }
    public uint FirstThunkRva { get; init; }
    public bool IsDelayLoad { get; init; }

    public override string ToString() => $"{Name} ({Functions.Count} functions)";
}

/// <summary>An exported symbol.</summary>
public sealed record ExportedFunction
{
    /// <summary>Public name, or null for ordinal-only exports.</summary>
    public string? Name { get; init; }
    public required uint Ordinal { get; init; }
    /// <summary>RVA of the code/data, or 0 for forwarders.</summary>
    public required uint Rva { get; init; }
    /// <summary>Forwarder target like <c>NTDLL.RtlAllocateHeap</c>, or null.</summary>
    public string? ForwarderName { get; init; }
    public bool IsForwarder => ForwarderName is not null;

    public string DisplayName => Name ?? $"#{Ordinal}";

    public override string ToString() => IsForwarder ? $"{DisplayName} -> {ForwarderName}" : $"{DisplayName} @ 0x{Rva:X}";
}

/// <summary>IMAGE_EXPORT_DIRECTORY plus resolved entries.</summary>
public sealed record ExportTable
{
    public required string Name { get; init; }
    public required uint Base { get; init; }
    public required uint TimeDateStamp { get; init; }
    public required ushort MajorVersion { get; init; }
    public required ushort MinorVersion { get; init; }
    public required uint NumberOfFunctions { get; init; }
    public required uint NumberOfNames { get; init; }
    public required IReadOnlyList<ExportedFunction> Entries { get; init; }
}

/// <summary>IMAGE_COR20_HEADER.</summary>
public sealed record ClrHeader
{
    public required uint Cb { get; init; }
    public required ushort MajorRuntimeVersion { get; init; }
    public required ushort MinorRuntimeVersion { get; init; }
    public required DataDirectory MetaData { get; init; }
    public required CorFlags Flags { get; init; }
    /// <summary>Managed entry point token, or native entry RVA when <see cref="CorFlags.NativeEntryPoint"/> is set.</summary>
    public required uint EntryPointTokenOrRva { get; init; }
    public required DataDirectory Resources { get; init; }
    public required DataDirectory StrongNameSignature { get; init; }
    public required DataDirectory CodeManagerTable { get; init; }
    public required DataDirectory VTableFixups { get; init; }
    public required DataDirectory ExportAddressTableJumps { get; init; }
    public required DataDirectory ManagedNativeHeader { get; init; }

    public bool IsILOnly => Flags.HasFlag(CorFlags.ILOnly);
}

/// <summary>IMAGE_DEBUG_DIRECTORY entry with decoded CodeView info when available.</summary>
public sealed record DebugEntry
{
    public required uint Characteristics { get; init; }
    public required uint TimeDateStamp { get; init; }
    public required ushort MajorVersion { get; init; }
    public required ushort MinorVersion { get; init; }
    public required DebugDirectoryType Type { get; init; }
    public required uint SizeOfData { get; init; }
    public required uint AddressOfRawData { get; init; }
    public required uint PointerToRawData { get; init; }
    public CodeViewInfo? CodeView { get; init; }

    public override string ToString() => CodeView is { } cv ? $"{Type}: {cv.PdbPath}" : Type.ToString();
}

/// <summary>
/// An entry from the exception directory (.pdata). x64 entries are 12 bytes and state the end
/// address; ARM64 entries are 8 bytes and encode the length, either packed into the word itself or
/// in an .xdata record — either way the end is computed, so consumers see the same shape.
/// </summary>
public readonly record struct RuntimeFunction(uint BeginRva, uint EndRva, uint UnwindInfoRva)
{
    /// <summary>Size of an x64 entry.</summary>
    public const int Size = 12;

    /// <summary>Size of an ARM64 / ARM entry.</summary>
    public const int Arm64Size = 8;

    /// <summary>True when the UNWIND_INFO is a chained entry (a fragment of another function, not a function start).</summary>
    public bool IsChained { get; init; }

    /// <summary>True when the unwind data is packed into the entry rather than stored in .xdata.</summary>
    public bool IsPacked { get; init; }

    public uint Length => EndRva - BeginRva;
}

/// <summary>RSDS (PDB 7.0) CodeView record.</summary>
public sealed record CodeViewInfo(string Signature, Guid Guid, uint Age, string PdbPath)
{
    /// <summary>Symbol-server style key: <c>{GUID}{Age}</c> in upper-case hex.</summary>
    public string SymbolKey => $"{Guid:N}{Age:X}".ToUpperInvariant();
}

/// <summary>One IMAGE_BASE_RELOCATION block: a 4 KiB page and the fix-ups inside it.</summary>
public sealed record RelocationBlock
{
    public required uint PageRva { get; init; }
    public required uint BlockSize { get; init; }
    public required IReadOnlyList<RelocationEntry> Entries { get; init; }

    public override string ToString() => $"0x{PageRva:X8} ({Entries.Count} fix-ups)";
}

/// <summary>A single base relocation: where it applies and how the loader patches it.</summary>
public readonly record struct RelocationEntry(RelocationType Type, uint Rva);

/// <summary>IMAGE_TLS_DIRECTORY plus the resolved callback list.</summary>
public sealed record TlsDirectory
{
    public required ulong StartAddressOfRawData { get; init; }
    public required ulong EndAddressOfRawData { get; init; }
    public required ulong AddressOfIndex { get; init; }
    public required ulong AddressOfCallBacks { get; init; }
    public required uint SizeOfZeroFill { get; init; }
    public required uint Characteristics { get; init; }

    /// <summary>
    /// Callback VAs read from <see cref="AddressOfCallBacks"/>. These run before the entry point,
    /// so they are function seeds for analysis. Empty when the list is absent or unmapped.
    /// </summary>
    public required IReadOnlyList<ulong> CallbackVas { get; init; }

    public ulong RawDataSize => EndAddressOfRawData > StartAddressOfRawData ? EndAddressOfRawData - StartAddressOfRawData : 0;
}

/// <summary>The parts of IMAGE_LOAD_CONFIG_DIRECTORY that matter for analysis.</summary>
public sealed record LoadConfig
{
    public required uint Size { get; init; }
    public required uint TimeDateStamp { get; init; }
    public required ushort MajorVersion { get; init; }
    public required ushort MinorVersion { get; init; }
    public required ulong SecurityCookieVa { get; init; }
    /// <summary>x86 SafeSEH handler table (VA) and its entry count; 0 on x64.</summary>
    public required ulong SeHandlerTableVa { get; init; }
    public required ulong SeHandlerCount { get; init; }
    public required ulong GuardCfCheckFunctionPointerVa { get; init; }
    public required ulong GuardCfDispatchFunctionPointerVa { get; init; }
    public required ulong GuardCfFunctionTableVa { get; init; }
    public required ulong GuardCfFunctionCount { get; init; }
    /// <summary>Flag bits only; the table-stride nibble is reported by <see cref="GuardCfFunctionTableStride"/>.</summary>
    public required GuardFlags GuardFlags { get; init; }
    /// <summary>Bytes per Control Flow Guard table entry: 4 RVA bytes plus any metadata.</summary>
    public required int GuardCfFunctionTableStride { get; init; }

    /// <summary>
    /// RVAs from the Control Flow Guard function table — every address the image declares as a
    /// valid indirect-call target. A high-quality seed set for function discovery.
    /// </summary>
    public required IReadOnlyList<uint> GuardCfFunctionRvas { get; init; }

    /// <summary>x86 SafeSEH exception handler RVAs (empty on x64).</summary>
    public required IReadOnlyList<uint> SeHandlerRvas { get; init; }

    public bool HasControlFlowGuard => GuardFlags.HasFlag(GuardFlags.CfInstrumented);
}

/// <summary>A node in the resource tree (type → name/id → language → data).</summary>
public sealed record ResourceNode
{
    /// <summary>Name for named entries, null for numeric ones.</summary>
    public string? Name { get; init; }
    public uint Id { get; init; }
    /// <summary>Depth in the tree: 0 = root, 1 = type, 2 = name, 3 = language.</summary>
    public required int Level { get; init; }
    public IReadOnlyList<ResourceNode>? Children { get; init; }
    /// <summary>Set on leaves: where the bytes live.</summary>
    public uint DataRva { get; init; }
    public uint DataSize { get; init; }
    public uint CodePage { get; init; }

    public bool IsDirectory => Children is not null;

    /// <summary>Human label: the entry name, the well-known RT_* type at level 1, or <c>#id</c>.</summary>
    public string DisplayName => Name
        ?? (Level == 1 && Enum.IsDefined(typeof(ResourceType), (int)Id) ? ((ResourceType)Id).ToString() : $"#{Id}");

    public override string ToString() => IsDirectory ? $"{DisplayName} ({Children!.Count})" : $"{DisplayName} ({DataSize} bytes)";
}

/// <summary>The undocumented "Rich" header Microsoft linkers stamp into the DOS stub.</summary>
public sealed record RichHeader
{
    /// <summary>File offset of the <c>DanS</c> marker.</summary>
    public required uint Offset { get; init; }
    /// <summary>XOR key, which doubles as a checksum over the DOS stub.</summary>
    public required uint Checksum { get; init; }
    public required IReadOnlyList<RichEntry> Entries { get; init; }
}

/// <summary>
/// One Rich header record: a build tool and how many objects it contributed. Product ids are
/// undocumented and build numbers are not ordered across Visual Studio releases (30729 is VS2008
/// SP1, 23026 is VS2015), so no version is inferred here - the optional header's linker version is
/// the reliable signal.
/// </summary>
public readonly record struct RichEntry(ushort ProductId, ushort BuildNumber, uint UseCount)
{
    public string Description => BuildNumber == 0
        ? $"tool id 0x{ProductId:X4} (imported object, no build stamp)"
        : $"tool id 0x{ProductId:X4} - build {BuildNumber}";

    public override string ToString() => $"id 0x{ProductId:X4} build {BuildNumber} x{UseCount}";
}
