namespace Spydate.Disassembly;

/// <summary>
/// Byte patterns that a compiler-generated function starts with. Used to find functions in the gaps
/// no seed and no call reaches — leaf functions on x64, and most of an x86 image, which has no
/// unwind table at all.
/// </summary>
public static class FunctionPrologues
{
    /// <summary>Filler the linker puts between functions.</summary>
    public static bool IsPadding(byte b) => b is 0xCC or 0x90 or 0x00;

    /// <summary>
    /// True when the bytes look like the start of a function. Deliberately conservative: a false
    /// positive invents a function out of data, which is worse than missing one, since discovery
    /// then attributes real code to the wrong place.
    /// </summary>
    public static bool LooksLikeFunctionStart(ReadOnlySpan<byte> code, int bitness)
        => bitness == 64 ? MatchesX64(code) : MatchesX86(code);

    private static bool MatchesX64(ReadOnlySpan<byte> c)
    {
        if (c.Length < 4)
        {
            return false;
        }

        // mov [rsp+disp8], reg — MSVC's home-register spill, by far the most common opening.
        if (c[0] is 0x48 or 0x4C && c[1] == 0x89 && (c[2] & 0xC7) == 0x44 && c[3] == 0x24)
        {
            return true;
        }

        // sub rsp, imm8 / imm32
        if (c[0] == 0x48 && c[1] == 0x83 && c[2] == 0xEC)
        {
            return true;
        }

        if (c[0] == 0x48 && c[1] == 0x81 && c[2] == 0xEC)
        {
            return true;
        }

        // mov rax, rsp / mov r11, rsp — frame pointer setup before a big stack allocation.
        if ((c[0] == 0x48 && c[1] == 0x8B && c[2] == 0xC4) || (c[0] == 0x4C && c[1] == 0x8B && c[2] == 0xDC))
        {
            return true;
        }

        // push rbx/rbp/rsi/rdi, with or without a REX prefix, followed by more frame setup.
        int i = c[0] == 0x40 ? 1 : 0;
        if (i < c.Length && c[i] is 0x53 or 0x55 or 0x56 or 0x57)
        {
            var rest = c[(i + 1)..];
            // push reg alone is ambiguous; require a stack adjustment or another push after it.
            return rest.Length >= 3
                   && ((rest[0] == 0x48 && rest[1] == 0x83 && rest[2] == 0xEC)
                       || (rest[0] == 0x48 && rest[1] == 0x81 && rest[2] == 0xEC)
                       || (rest[0] == 0x48 && rest[1] == 0x8B && rest[2] == 0xEC)
                       || (rest[0] == 0x40 && rest[1] is 0x53 or 0x55 or 0x56 or 0x57)
                       || rest[0] is 0x53 or 0x55 or 0x56 or 0x57);
        }

        return IsImportThunk(c);
    }

    private static bool MatchesX86(ReadOnlySpan<byte> c)
    {
        if (c.Length < 3)
        {
            return false;
        }

        // mov edi, edi — the two-byte hot-patch pad that precedes push ebp; mov ebp, esp.
        if (c[0] == 0x8B && c[1] == 0xFF && c.Length >= 5 && c[2] == 0x55 && c[3] == 0x8B && c[4] == 0xEC)
        {
            return true;
        }

        // push ebp; mov ebp, esp
        if (c[0] == 0x55 && c[1] == 0x8B && c[2] == 0xEC)
        {
            return true;
        }

        // sub esp, imm8 / imm32
        if (c[0] == 0x83 && c[1] == 0xEC)
        {
            return true;
        }

        if (c[0] == 0x81 && c[1] == 0xEC)
        {
            return true;
        }

        // push imm8/imm32 pairs: the classic SEH frame setup (push offset handler; push fs:[0]).
        if (c[0] is 0x6A or 0x68 && c.Length >= 6 && c[c[0] == 0x6A ? 2 : 5] == 0x68)
        {
            return true;
        }

        // push reg followed by another push or a stack adjustment.
        if (c[0] is 0x53 or 0x55 or 0x56 or 0x57)
        {
            return c[1] is 0x53 or 0x55 or 0x56 or 0x57
                   || (c[1] == 0x8B && c[2] == 0xEC)
                   || (c[1] == 0x83 && c[2] == 0xEC)
                   || (c[1] == 0x81 && c[2] == 0xEC);
        }

        return IsImportThunk(c);
    }

    /// <summary>
    /// <c>jmp [import]</c> and <c>jmp rel32</c>: one-instruction thunks the linker emits for
    /// imported and incrementally-linked functions. They are real call targets, so they count.
    /// </summary>
    private static bool IsImportThunk(ReadOnlySpan<byte> c)
        => (c.Length >= 6 && c[0] == 0xFF && c[1] == 0x25)
           || (c.Length >= 7 && c[0] == 0x48 && c[1] == 0xFF && c[2] == 0x25)
           || (c.Length >= 5 && c[0] == 0xE9);
}
