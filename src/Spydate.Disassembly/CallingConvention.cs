using Spydate.Core.Binary;

namespace Spydate.Disassembly;

/// <summary>
/// Which registers carry a call's arguments and which a callee may destroy. On x86 it is the stack, whatever
/// the platform. On x64 it depends on the operating system the binary was built for, not on the processor:
/// Windows passes four arguments in <c>rcx, rdx, r8, r9</c> and lets an integer and a float share a slot;
/// Linux and the BSDs (System V) pass six in <c>rdi, rsi, rdx, rcx, r8, r9</c> and up to eight floats in
/// <c>xmm0</c>–<c>xmm7</c>, counted separately. Reading a Linux binary with the Windows rules gets every
/// argument wrong, so the decompiler asks the image which it is. ARM64 has one convention on every platform
/// (AAPCS64): eight integers in <c>x0</c>–<c>x7</c>, eight floats in <c>v0</c>–<c>v7</c>, and no return
/// address on the stack — it is in <c>x30</c>.
///
/// Register names are the IR's canonical ones (widest alias: <c>rcx</c>, <c>zmm0</c>, <c>x0</c>, <c>v0</c>) so
/// passes can compare them after <c>RegisterAliases.CanonicalOf</c>; the argument lists use the names the IR
/// writes.
/// </summary>
public sealed class CallingConvention
{
    private readonly HashSet<string> _volatile;
    private readonly HashSet<string> _preserved;
    private readonly bool _vectorsVolatile;

    private CallingConvention(
        string name,
        string[] integerArguments,
        string[] floatArguments,
        string[] canonicalFloatArguments,
        bool floatsShareSlots,
        string[] volatileRegisters,
        string[] preservedRegisters,
        bool vectorsVolatile,
        string stackPointer,
        string returnRegister,
        string floatReturnRegister,
        bool returnAddressOnStack)
    {
        Name = name;
        IntegerArguments = integerArguments;
        FloatArguments = floatArguments;
        CanonicalFloatArguments = canonicalFloatArguments;
        FloatsShareSlots = floatsShareSlots;
        _volatile = new HashSet<string>(volatileRegisters, StringComparer.Ordinal);
        _preserved = new HashSet<string>(preservedRegisters, StringComparer.Ordinal);
        _vectorsVolatile = vectorsVolatile;
        StackPointer = stackPointer;
        ReturnRegister = returnRegister;
        FloatReturnRegister = floatReturnRegister;
        ReturnAddressOnStack = returnAddressOnStack;
    }

    /// <summary>Windows x64.</summary>
    public static CallingConvention Microsoft64 { get; } = new(
        "Microsoft x64",
        ["rcx", "rdx", "r8", "r9"],
        ["xmm0", "xmm1", "xmm2", "xmm3"],
        ["zmm0", "zmm1", "zmm2", "zmm3"],
        floatsShareSlots: true,
        ["rax", "rcx", "rdx", "r8", "r9", "r10", "r11"],
        ["rbx", "rbp", "rdi", "rsi", "r12", "r13", "r14", "r15"],
        vectorsVolatile: true,
        "rsp",
        "rax",
        "zmm0",
        returnAddressOnStack: true);

    /// <summary>Linux, the BSDs and macOS on x64.</summary>
    public static CallingConvention SystemV64 { get; } = new(
        "System V AMD64",
        ["rdi", "rsi", "rdx", "rcx", "r8", "r9"],
        ["xmm0", "xmm1", "xmm2", "xmm3", "xmm4", "xmm5", "xmm6", "xmm7"],
        ["zmm0", "zmm1", "zmm2", "zmm3", "zmm4", "zmm5", "zmm6", "zmm7"],
        floatsShareSlots: false,
        ["rax", "rcx", "rdx", "rsi", "rdi", "r8", "r9", "r10", "r11"],
        ["rbx", "rbp", "r12", "r13", "r14", "r15"],
        vectorsVolatile: true,
        "rsp",
        "rax",
        "zmm0",
        returnAddressOnStack: true);

    /// <summary>32-bit x86: arguments on the stack (cdecl, stdcall), with <c>ecx</c>/<c>edx</c> for fastcall and thiscall.</summary>
    public static CallingConvention X86 { get; } = new(
        "x86",
        [],
        [],
        [],
        floatsShareSlots: false,
        ["rax", "rcx", "rdx"],
        ["rbx", "rbp", "rdi", "rsi"],
        vectorsVolatile: false,
        "esp",
        "rax",
        "zmm0",
        returnAddressOnStack: true);

    /// <summary>
    /// ARM64 (AAPCS64, and Windows on ARM, which follows it). <c>x16</c>–<c>x18</c> are scratch or reserved, and
    /// <c>x30</c> is the link register: a call destroys it, and a function that makes calls saves it with
    /// <c>x29</c> in its prologue and restores both before it returns — so it counts as preserved too, which is
    /// what lets that save and restore be dropped from the output.
    /// </summary>
    public static CallingConvention Aapcs64 { get; } = new(
        "AAPCS64",
        ["x0", "x1", "x2", "x3", "x4", "x5", "x6", "x7"],
        ["d0", "d1", "d2", "d3", "d4", "d5", "d6", "d7"],
        ["v0", "v1", "v2", "v3", "v4", "v5", "v6", "v7"],
        floatsShareSlots: false,
        [
            "x0", "x1", "x2", "x3", "x4", "x5", "x6", "x7", "x8", "x9", "x10", "x11", "x12", "x13", "x14", "x15", "x16", "x17",
            "x18", "x30",
            "v0", "v1", "v2", "v3", "v4", "v5", "v6", "v7",
            "v16", "v17", "v18", "v19", "v20", "v21", "v22", "v23", "v24", "v25", "v26", "v27", "v28", "v29", "v30", "v31",
        ],
        ["x19", "x20", "x21", "x22", "x23", "x24", "x25", "x26", "x27", "x28", "x29", "x30", "v8", "v9", "v10", "v11", "v12", "v13", "v14", "v15"],
        vectorsVolatile: false,
        "sp",
        "x0",
        "v0",
        returnAddressOnStack: false);

    /// <summary>The convention a binary's code follows, from its instruction set, bitness and the platform its format belongs to.</summary>
    public static CallingConvention For(IBinaryImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.Architecture == Architecture.Arm64 ? Aapcs64 : For(image.Bitness, image.Format);
    }

    public static CallingConvention For(int bitness, BinaryFormat format = BinaryFormat.Pe)
        => bitness != 64 ? X86 : format == BinaryFormat.Pe ? Microsoft64 : SystemV64;

    public string Name { get; }

    /// <summary>Integer argument registers, in order. Empty on x86.</summary>
    public IReadOnlyList<string> IntegerArguments { get; }

    /// <summary>Float argument registers, in order, as the IR names them.</summary>
    public IReadOnlyList<string> FloatArguments { get; }

    /// <summary>The float argument registers by their canonical names.</summary>
    public IReadOnlyList<string> CanonicalFloatArguments { get; }

    /// <summary>
    /// Windows: argument <c>n</c> is in the <c>n</c>th integer register or the <c>n</c>th xmm register, never
    /// both. System V and AAPCS64: integers and floats are numbered independently.
    /// </summary>
    public bool FloatsShareSlots { get; }

    public bool IsStackBased => IntegerArguments.Count == 0;

    /// <summary>The stack pointer, as the IR names it.</summary>
    public string StackPointer { get; }

    /// <summary>Where an integer result comes back (canonical name).</summary>
    public string ReturnRegister { get; }

    /// <summary>Where a floating-point result comes back (canonical name).</summary>
    public string FloatReturnRegister { get; }

    /// <summary>
    /// Whether a call leaves its return address on the stack, just above the callee's frame (x86, x64). ARM64
    /// keeps it in a register, so the first word above the frame is the first stack argument.
    /// </summary>
    public bool ReturnAddressOnStack { get; }

    /// <summary>A register a call may destroy (canonical name; every vector register is volatile in both x64 conventions).</summary>
    public bool IsVolatile(string canonical)
        => _volatile.Contains(canonical) || (_vectorsVolatile && canonical.StartsWith("zmm", StringComparison.Ordinal));

    /// <summary>A register a callee must give back as it found it (canonical name).</summary>
    public bool IsPreserved(string canonical) => _preserved.Contains(canonical);

    /// <summary>Every register a callee may read an argument from, integer then float, canonical names.</summary>
    public IEnumerable<string> ArgumentRegisters => IsStackBased
        ? ["rcx", "rdx"]   // fastcall and thiscall
        : IntegerArguments.Concat(CanonicalFloatArguments);

    public override string ToString() => Name;
}
