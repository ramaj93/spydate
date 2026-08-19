namespace Spydate.Core.PE;

public enum MachineType : ushort
{
    Unknown = 0x0,
    I386 = 0x14C,
    R4000 = 0x166,
    WceMipsV2 = 0x169,
    Alpha = 0x184,
    Sh3 = 0x1A2,
    Sh3Dsp = 0x1A3,
    Sh4 = 0x1A6,
    Sh5 = 0x1A8,
    Arm = 0x1C0,
    Thumb = 0x1C2,
    ArmNt = 0x1C4,
    Am33 = 0x1D3,
    PowerPc = 0x1F0,
    PowerPcFp = 0x1F1,
    Ia64 = 0x200,
    Mips16 = 0x266,
    MipsFpu = 0x366,
    MipsFpu16 = 0x466,
    Ebc = 0xEBC,
    RiscV32 = 0x5032,
    RiscV64 = 0x5064,
    RiscV128 = 0x5128,
    LoongArch32 = 0x6232,
    LoongArch64 = 0x6264,
    Amd64 = 0x8664,
    M32R = 0x9041,
    Arm64 = 0xAA64,
    Arm64Ec = 0xA641,
    Arm64X = 0xA64E,
    Cee = 0xC0EE,
}

public enum OptionalHeaderMagic : ushort
{
    Pe32 = 0x10B,
    Pe32Plus = 0x20B,
    Rom = 0x107,
}

public enum Subsystem : ushort
{
    Unknown = 0,
    Native = 1,
    WindowsGui = 2,
    WindowsCui = 3,
    Os2Cui = 5,
    PosixCui = 7,
    NativeWindows = 8,
    WindowsCeGui = 9,
    EfiApplication = 10,
    EfiBootServiceDriver = 11,
    EfiRuntimeDriver = 12,
    EfiRom = 13,
    Xbox = 14,
    WindowsBootApplication = 16,
    XboxCodeCatalog = 17,
}

[Flags]
public enum ImageCharacteristics : ushort
{
    None = 0,
    RelocsStripped = 0x0001,
    ExecutableImage = 0x0002,
    LineNumsStripped = 0x0004,
    LocalSymsStripped = 0x0008,
    AggressiveWsTrim = 0x0010,
    LargeAddressAware = 0x0020,
    BytesReversedLo = 0x0080,
    Machine32Bit = 0x0100,
    DebugStripped = 0x0200,
    RemovableRunFromSwap = 0x0400,
    NetRunFromSwap = 0x0800,
    System = 0x1000,
    Dll = 0x2000,
    UpSystemOnly = 0x4000,
    BytesReversedHi = 0x8000,
}

[Flags]
public enum DllCharacteristics : ushort
{
    None = 0,
    HighEntropyVa = 0x0020,
    DynamicBase = 0x0040,
    ForceIntegrity = 0x0080,
    NxCompat = 0x0100,
    NoIsolation = 0x0200,
    NoSeh = 0x0400,
    NoBind = 0x0800,
    AppContainer = 0x1000,
    WdmDriver = 0x2000,
    GuardCf = 0x4000,
    TerminalServerAware = 0x8000,
}

[Flags]
public enum SectionCharacteristics : uint
{
    None = 0,
    TypeNoPad = 0x00000008,
    ContainsCode = 0x00000020,
    ContainsInitializedData = 0x00000040,
    ContainsUninitializedData = 0x00000080,
    LinkOther = 0x00000100,
    LinkInfo = 0x00000200,
    LinkRemove = 0x00000800,
    LinkComdat = 0x00001000,
    GpRel = 0x00008000,
    Align1Bytes = 0x00100000,
    Align2Bytes = 0x00200000,
    Align4Bytes = 0x00300000,
    Align8Bytes = 0x00400000,
    Align16Bytes = 0x00500000,
    Align32Bytes = 0x00600000,
    Align64Bytes = 0x00700000,
    Align128Bytes = 0x00800000,
    Align256Bytes = 0x00900000,
    Align512Bytes = 0x00A00000,
    Align1024Bytes = 0x00B00000,
    Align2048Bytes = 0x00C00000,
    Align4096Bytes = 0x00D00000,
    Align8192Bytes = 0x00E00000,
    AlignMask = 0x00F00000,
    LinkNRelocOvfl = 0x01000000,
    MemDiscardable = 0x02000000,
    MemNotCached = 0x04000000,
    MemNotPaged = 0x08000000,
    MemShared = 0x10000000,
    MemExecute = 0x20000000,
    MemRead = 0x40000000,
    MemWrite = 0x80000000,
}

public enum DataDirectoryIndex
{
    Export = 0,
    Import = 1,
    Resource = 2,
    Exception = 3,
    Security = 4,
    BaseRelocation = 5,
    Debug = 6,
    Architecture = 7,
    GlobalPointer = 8,
    Tls = 9,
    LoadConfig = 10,
    BoundImport = 11,
    Iat = 12,
    DelayImport = 13,
    ClrRuntimeHeader = 14,
    Reserved = 15,
}

public enum DebugDirectoryType : uint
{
    Unknown = 0,
    Coff = 1,
    CodeView = 2,
    Fpo = 3,
    Misc = 4,
    Exception = 5,
    Fixup = 6,
    OmapToSrc = 7,
    OmapFromSrc = 8,
    Borland = 9,
    Reserved10 = 10,
    Clsid = 11,
    VcFeature = 12,
    Pogo = 13,
    Iltcg = 14,
    Mpx = 15,
    Repro = 16,
    EmbeddedPortablePdb = 17,
    SpgoInfo = 18,
    PdbChecksum = 19,
    ExtendedDllCharacteristics = 20,
}

[Flags]
public enum CorFlags : uint
{
    None = 0,
    ILOnly = 0x00000001,
    Requires32Bit = 0x00000002,
    ILLibrary = 0x00000004,
    StrongNameSigned = 0x00000008,
    NativeEntryPoint = 0x00000010,
    TrackDebugData = 0x00010000,
    Prefers32Bit = 0x00020000,
}

/// <summary>IMAGE_REL_BASED_* base relocation kinds. Values 5-9 are machine specific.</summary>
public enum RelocationType : byte
{
    Absolute = 0,
    High = 1,
    Low = 2,
    HighLow = 3,
    HighAdj = 4,
    /// <summary>MIPS_JMPADDR / ARM_MOV32 / RISCV_HIGH20.</summary>
    ArmMov32 = 5,
    Reserved = 6,
    /// <summary>THUMB_MOV32 / RISCV_LOW12I.</summary>
    ThumbMov32 = 7,
    /// <summary>RISCV_LOW12S / LOONGARCH_MARK_LA.</summary>
    RiscvLow12S = 8,
    /// <summary>MIPS_JMPADDR16 / IA64_IMM64.</summary>
    MipsJmpAddr16 = 9,
    Dir64 = 10,
}

/// <summary>IMAGE_GUARD_* flags from the load config directory.</summary>
[Flags]
public enum GuardFlags : uint
{
    None = 0,
    CfInstrumented = 0x0000_0100,
    CfwInstrumented = 0x0000_0200,
    CfFunctionTablePresent = 0x0000_0400,
    SecurityCookieUnused = 0x0000_0800,
    ProtectDelayloadIat = 0x0000_1000,
    DelayloadIatInItsOwnSection = 0x0000_2000,
    CfExportSuppressionInfoPresent = 0x0000_4000,
    CfEnableExportSuppression = 0x0000_8000,
    CfLongjumpTablePresent = 0x0001_0000,
    RfInstrumented = 0x0002_0000,
    RfEnable = 0x0004_0000,
    RfStrict = 0x0008_0000,
    RetpolinePresent = 0x0010_0000,
    EhContinuationTablePresent = 0x0040_0000,
}

/// <summary>Well-known RT_* resource type ids (level 0 of the resource tree).</summary>
public enum ResourceType
{
    Cursor = 1,
    Bitmap = 2,
    Icon = 3,
    Menu = 4,
    Dialog = 5,
    String = 6,
    FontDir = 7,
    Font = 8,
    Accelerator = 9,
    RcData = 10,
    MessageTable = 11,
    GroupCursor = 12,
    GroupIcon = 14,
    Version = 16,
    DlgInclude = 17,
    PlugPlay = 19,
    Vxd = 20,
    AniCursor = 21,
    AniIcon = 22,
    Html = 23,
    Manifest = 24,
}
