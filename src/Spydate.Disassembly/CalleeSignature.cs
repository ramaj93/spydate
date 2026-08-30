using Iced.Intel;

namespace Spydate.Disassembly;

/// <summary>Where a <see cref="CalleeSignature"/> came from, so output can say why it believes it.</summary>
public enum SignatureSource
{
    /// <summary>Nothing was learned.</summary>
    None,

    /// <summary>The callee's <c>ret N</c> states how many bytes of arguments it removes (x86 stdcall).</summary>
    StackCleanup,

    /// <summary>The callee reads an argument register before writing it (x64, and x86 fastcall).</summary>
    RegisterUse,
}

/// <summary>
/// What a function takes, worked out from the function's own code rather than from a table of API
/// signatures. Two facts are recoverable and neither needs anything to be typed in:
///
/// <list type="bullet">
/// <item>On x86 a <c>__stdcall</c> callee removes its own arguments, so <c>ret N</c> states the byte
/// count exactly. That is a fact about the binary, not a guess.</item>
/// <item>On x64 there is no such instruction — every convention is caller-cleaned — but a callee that
/// reads <c>xmm2</c> before writing it was handed a float in the third slot, which is the one thing a
/// call site cannot show on its own.</item>
/// </list>
///
/// The x64 answer is a <em>lower bound</em>: only the entry block is read (see <see cref="RegisterUse"/>
/// for why), so a function can take more than it is seen to use. Callers must therefore only ever add or
/// retype an argument from a signature, never drop one the call site actually passes.
/// </summary>
public readonly record struct CalleeSignature
{
    /// <summary>
    /// Nothing is known about this callee. This is <c>default</c> on purpose: a struct read out of an
    /// uninitialised field or array slot has to mean "unknown", and the checks below are written so it
    /// does. Getting that wrong reads as "takes no arguments", which silently empties every call.
    /// </summary>
    public static readonly CalleeSignature Unknown = default;

    /// <summary>
    /// How many arguments the callee takes, or -1 when that is not known. From
    /// <see cref="SignatureSource.StackCleanup"/> this counts the arguments on the <em>stack</em>: a
    /// <c>__thiscall</c> callee that removes four bytes takes one stack argument and <c>this</c> in
    /// <c>ecx</c> besides, and the register ones are the other analysis's to add.
    /// </summary>
    public int ArgumentCount { get; init; } = -1;

    /// <summary>Bytes of arguments the callee removes from the stack, or -1 when that is not known.</summary>
    public int StackCleanupBytes { get; init; } = -1;

    /// <summary>Bit <c>i</c> set when argument <c>i</c> arrives in a float register rather than an integer one.</summary>
    public uint FloatMask { get; init; }

    public SignatureSource Source { get; init; }

    public CalleeSignature()
    {
    }

    /// <summary>A known count is at least one: neither way of reading a callee can produce zero.</summary>
    public bool HasArgumentCount => Source != SignatureSource.None && ArgumentCount >= 1;

    /// <summary>Zero cleanup bytes is a real answer - the caller cleans up - so the source is the gate.</summary>
    public bool HasStackCleanup => Source != SignatureSource.None && StackCleanupBytes >= 0;

    public bool IsFloat(int index) => index is >= 0 and < 32 && (FloatMask & (1u << index)) != 0;

    public override string ToString()
    {
        if (Source == SignatureSource.None)
        {
            return "unknown";
        }

        string args = HasArgumentCount ? $"{ArgumentCount} args" : "? args";
        string cleanup = HasStackCleanup ? $", cleans {StackCleanupBytes}" : string.Empty;
        string floats = FloatMask == 0 ? string.Empty : $", float mask 0x{FloatMask:X}";
        return $"{args}{cleanup}{floats}";
    }
}

/// <summary>
/// Reads a <see cref="CalleeSignature"/> out of a function's instructions. Everything here is derived
/// from bytes that are present; nothing is looked up.
/// </summary>
public static class CalleeSignatures
{
    /// <summary>Argument registers of the Microsoft x64 convention, by slot.</summary>
    private static readonly Register[] Win64IntegerArgs = { Register.RCX, Register.RDX, Register.R8, Register.R9 };

    private static readonly Register[] Win64FloatArgs = { Register.XMM0, Register.XMM1, Register.XMM2, Register.XMM3 };

    /// <summary>
    /// A stdcall callee removes at most this much. Anything larger is a decoding accident rather than
    /// an argument list — 64 arguments is already far past what real code passes on the stack.
    /// </summary>
    private const int MaxCleanupBytes = 256;

    public static CalleeSignature FromCode(Function function, int bitness)
    {
        ArgumentNullException.ThrowIfNull(function);
        return bitness == 64 ? FromRegisterUse(function) : FromStackCleanup(function);
    }

    /// <summary>
    /// x86: every <c>ret</c> in a function removes the same number of argument bytes, because they are
    /// all returning from the same declaration. Rets that disagree mean the "function" is really two
    /// functions the discovery ran together, and nothing is claimed.
    /// </summary>
    public static CalleeSignature FromStackCleanup(Function function)
    {
        ArgumentNullException.ThrowIfNull(function);

        int? bytes = null;
        bool sawReturn = false;
        foreach (var decoded in function.Instructions)
        {
            var instr = decoded.Native;
            if (instr.Mnemonic is not (Mnemonic.Ret or Mnemonic.Retf))
            {
                continue;
            }

            sawReturn = true;
            int removed = instr.OpCount == 1 && instr.Op0Kind == OpKind.Immediate16 ? instr.Immediate16 : 0;
            if (removed is < 0 or > MaxCleanupBytes || removed % 4 != 0)
            {
                return CalleeSignature.Unknown;
            }

            if (bytes is { } already && already != removed)
            {
                return CalleeSignature.Unknown;
            }

            bytes = removed;
        }

        if (!sawReturn || bytes is not { } cleanup)
        {
            return CalleeSignature.Unknown;
        }

        // `ret` with no immediate means the caller cleans up: that is a fact about the cleanup, but it
        // leaves the argument count unknown — a cdecl function with four arguments returns like one with
        // none. Only `ret N` states a count.
        return new CalleeSignature
        {
            StackCleanupBytes = cleanup,
            ArgumentCount = cleanup > 0 ? cleanup / 4 : -1,
            Source = SignatureSource.StackCleanup,
        };
    }

    /// <summary>
    /// x64: which of the four argument slots the callee is seen to read, and whether each arrived in an
    /// integer register or in the xmm register that shares its slot. A slot read in both is reported as
    /// an integer, since claiming a float would change the type printed at every call site on the
    /// strength of an ambiguity.
    /// </summary>
    public static CalleeSignature FromRegisterUse(Function function)
    {
        ArgumentNullException.ThrowIfNull(function);

        uint floats = 0;
        int highest = -1;
        for (int slot = 0; slot < Win64IntegerArgs.Length; slot++)
        {
            bool integer = RegisterUse.ReadsBeforeWriting(function, Win64IntegerArgs[slot]);
            bool number = RegisterUse.ReadsBeforeWriting(function, Win64FloatArgs[slot]);
            if (!integer && !number)
            {
                continue;
            }

            highest = slot;
            if (number && !integer)
            {
                floats |= 1u << slot;
            }
        }

        if (highest < 0)
        {
            return CalleeSignature.Unknown;
        }

        // Slots below the highest one are occupied whether or not the body happens to read them: an
        // argument cannot sit in r9 unless something was passed in rcx, rdx and r8 first.
        return new CalleeSignature
        {
            ArgumentCount = highest + 1,
            FloatMask = floats,
            Source = SignatureSource.RegisterUse,
        };
    }
}
