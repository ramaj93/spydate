using Iced.Intel;

namespace Spydate.Disassembly;

/// <summary>
/// Answers "does this function use a register it was handed?" — which, for <c>ecx</c> and <c>edx</c> on
/// x86, is the same question as "is this a <c>__fastcall</c> or <c>__thiscall</c> function?". The answer
/// comes from the callee's own code rather than from a guess at the call site.
///
/// Only the entry block is read, and only a use that consumes the value counts. Both restrictions are
/// deliberate. A function that reads <c>ecx</c> somewhere down a rare path is not taking an argument in
/// it - with enough blocks, almost every function would look like one - and <c>push ecx</c> at the top of
/// an x86 function is how MSVC allocates four bytes of stack, not how it reads a parameter. The cost is
/// missing a convention; the alternative is inventing arguments, which is worse.
/// </summary>
public static class RegisterUse
{
    /// <summary>
    /// How many register arguments <paramref name="function"/> takes: 1 when it reads <c>ecx</c> before
    /// writing it (<c>__thiscall</c>), 2 when it reads <c>edx</c> as well (<c>__fastcall</c>), 0 otherwise.
    /// A register read only after a call does not count — the call would have clobbered it.
    /// </summary>
    public static int FastcallArgumentCount(Function function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (!ReadsBeforeWriting(function, Register.ECX))
        {
            return 0;
        }

        return ReadsBeforeWriting(function, Register.EDX) ? 2 : 1;
    }

    /// <summary>True when the function's entry block consumes <paramref name="register"/> before writing it.</summary>
    public static bool ReadsBeforeWriting(Function function, Register register)
    {
        ArgumentNullException.ThrowIfNull(function);
        return function.BlockByVa.TryGetValue(function.EntryVa, out var entry)
               && Scan(entry, register, new InstructionInfoFactory()) == Verdict.Read;
    }

    private enum Verdict
    {
        /// <summary>Nothing in the block decided it either way.</summary>
        Undecided,
        Read,
        Settled,
    }

    private static Verdict Scan(BasicBlock block, Register register, InstructionInfoFactory factory)
    {
        foreach (var decoded in block.Instructions)
        {
            var instr = decoded.Native;
            if (decoded.IsCall)
            {
                return Verdict.Settled; // whatever the register held, the callee may have replaced it
            }

            // `push ecx` at the top of an x86 function reserves four bytes of stack; it says nothing
            // about what ecx held.
            if (instr.Mnemonic == Mnemonic.Push)
            {
                continue;
            }

            // `xor ecx, ecx` and `sub ecx, ecx` read the register only to produce zero.
            if (instr.Mnemonic is Mnemonic.Xor or Mnemonic.Sub
                && instr.Op0Kind == OpKind.Register && instr.Op1Kind == OpKind.Register
                && instr.Op0Register == instr.Op1Register)
            {
                if (Covers(instr.Op0Register, register))
                {
                    return Verdict.Settled;
                }

                continue;
            }

            var info = factory.GetInfo(instr);
            foreach (var used in info.GetUsedRegisters())
            {
                if (!Covers(used.Register, register) && !Covers(register, used.Register))
                {
                    continue;
                }

                switch (used.Access)
                {
                    case OpAccess.Read:
                    case OpAccess.CondRead:
                    case OpAccess.ReadWrite:
                    case OpAccess.ReadCondWrite:
                        return Verdict.Read;
                    case OpAccess.Write when Covers(used.Register, register):
                    case OpAccess.CondWrite when Covers(used.Register, register):
                        return Verdict.Settled;
                }
            }
        }

        return Verdict.Undecided;
    }

    /// <summary>True when a write to <paramref name="wider"/> replaces the whole of <paramref name="part"/>.</summary>
    private static bool Covers(Register wider, Register part)
        => wider != Register.None && part != Register.None
           && wider.GetFullRegister() == part.GetFullRegister()
           && wider.GetSize() >= part.GetSize();
}
