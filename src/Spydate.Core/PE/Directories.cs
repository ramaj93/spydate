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

/// <summary>x64 RUNTIME_FUNCTION entry from the exception directory (.pdata).</summary>
public readonly record struct RuntimeFunction(uint BeginRva, uint EndRva, uint UnwindInfoRva)
{
    public const int Size = 12;

    /// <summary>True when the UNWIND_INFO is a chained entry (a fragment of another function, not a function start).</summary>
    public bool IsChained { get; init; }

    public uint Length => EndRva - BeginRva;
}

/// <summary>RSDS (PDB 7.0) CodeView record.</summary>
public sealed record CodeViewInfo(string Signature, Guid Guid, uint Age, string PdbPath)
{
    /// <summary>Symbol-server style key: <c>{GUID}{Age}</c> in upper-case hex.</summary>
    public string SymbolKey => $"{Guid:N}{Age:X}".ToUpperInvariant();
}
