using Spydate.Core.Binary;

namespace Spydate.Disassembly;

/// <summary>
/// Which registers carry a call's arguments and which a callee may destroy. On x86 it is the stack, whatever
/// the platform. On x64 it depends on the operating system the binary was built for, not on the processor:
/// Windows passes four arguments in <c>rcx, rdx, r8, r9</c> and lets an integer and a float share a slot;
/// Linux and the BSDs (System V) pass six in <c>rdi, rsi, rdx, rcx, r8, r9</c> and up to eight floats in
/// <c>xmm0</c>–<c>xmm7</c>, counted separately. Reading a Linux binary with the Windows rules gets every
/// argument wrong, so the decompiler asks the image which it is.
///
/// Register names are the IR's canonical ones (widest alias: <c>rcx</c>, <c>zmm0</c>) so passes can compare
/// them after <c>RegisterAliases.CanonicalOf</c>; the argument lists use the names the IR writes.
/// </summary>
public sealed class CallingConvention
{
    private readonly HashSet<string> _volatile;
    private readonly HashSet<string> _preserved;

    private CallingConvention(
        string name,
        string[] integerArguments,
        string[] floatArguments,
        bool floatsShareSlots,
        string[] volatileRegisters,
        string[] preservedRegisters)
    {
        Name = name;
        IntegerArguments = integerArguments;
        FloatArguments = floatArguments;
        FloatsShareSlots = floatsShareSlots;
        _volatile = new HashSet<string>(volatileRegisters, StringComparer.Ordinal);
        _preserved = new HashSet<string>(preservedRegisters, StringComparer.Ordinal);
    }

    /// <summary>Windows x64.</summary>
    public static CallingConvention Microsoft64 { get; } = new(
        "Microsoft x64",
        ["rcx", "rdx", "r8", "r9"],
        ["xmm0", "xmm1", "xmm2", "xmm3"],
        floatsShareSlots: true,
        ["rax", "rcx", "rdx", "r8", "r9", "r10", "r11"],
        ["rbx", "rbp", "rdi", "rsi", "r12", "r13", "r14", "r15"]);

    /// <summary>Linux, the BSDs and macOS on x64.</summary>
    public static CallingConvention SystemV64 { get; } = new(
        "System V AMD64",
        ["rdi", "rsi", "rdx", "rcx", "r8", "r9"],
        ["xmm0", "xmm1", "xmm2", "xmm3", "xmm4", "xmm5", "xmm6", "xmm7"],
        floatsShareSlots: false,
        ["rax", "rcx", "rdx", "rsi", "rdi", "r8", "r9", "r10", "r11"],
        ["rbx", "rbp", "r12", "r13", "r14", "r15"]);

    /// <summary>32-bit x86: arguments on the stack (cdecl, stdcall), with <c>ecx</c>/<c>edx</c> for fastcall and thiscall.</summary>
    public static CallingConvention X86 { get; } = new(
        "x86",
        [],
        [],
        floatsShareSlots: false,
        ["rax", "rcx", "rdx"],
        ["rbx", "rbp", "rdi", "rsi"]);

    /// <summary>The convention a binary's code follows, from its bitness and the platform its format belongs to.</summary>
    public static CallingConvention For(IBinaryImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return For(image.Bitness, image.Format);
    }

    public static CallingConvention For(int bitness, BinaryFormat format = BinaryFormat.Pe)
        => bitness != 64 ? X86 : format == BinaryFormat.Pe ? Microsoft64 : SystemV64;

    public string Name { get; }

    /// <summary>Integer argument registers, in order. Empty on x86.</summary>
    public IReadOnlyList<string> IntegerArguments { get; }

    /// <summary>Float argument registers, in order.</summary>
    public IReadOnlyList<string> FloatArguments { get; }

    /// <summary>
    /// Windows: argument <c>n</c> is in the <c>n</c>th integer register or the <c>n</c>th xmm register, never
    /// both. System V: integers and floats are numbered independently.
    /// </summary>
    public bool FloatsShareSlots { get; }

    public bool IsStackBased => IntegerArguments.Count == 0;

    /// <summary>A register a call may destroy (canonical name; every vector register is volatile in both x64 conventions).</summary>
    public bool IsVolatile(string canonical)
        => _volatile.Contains(canonical) || (!IsStackBased && canonical.StartsWith("zmm", StringComparison.Ordinal));

    /// <summary>A register a callee must give back as it found it (canonical name).</summary>
    public bool IsPreserved(string canonical) => _preserved.Contains(canonical);

    /// <summary>Every register a callee may read an argument from, integer then float, canonical names.</summary>
    public IEnumerable<string> ArgumentRegisters => IsStackBased
        ? ["rcx", "rdx"]   // fastcall and thiscall
        : IntegerArguments.Concat(FloatArguments.Select(x => "zmm" + x[3..]));

    public override string ToString() => Name;
}
