using Spydate.Core.PE;
using Spydate.Core.Symbols;

namespace Spydate.Disassembly;

/// <summary>
/// Names the compiler-generated helpers that show up in every MSVC binary. Two sources, in
/// descending order of confidence: addresses the load config states outright, and instruction
/// shapes distinctive enough that a mismatch is unlikely. Anything less certain is left as
/// <c>sub_XXXX</c> — a wrong name is worse than no name.
/// </summary>
public static class CrtHelpers
{
    /// <summary>
    /// Adds the symbols the load config identifies directly: the stack cookie, and the Control Flow
    /// Guard check/dispatch routines whose addresses the loader patches into fixed pointers.
    /// </summary>
    public static int ApplyLoadConfigSymbols(PeImage image, SymbolTable symbols)
    {
        if (image.LoadConfig is not { } config)
        {
            return 0;
        }

        int added = 0;

        if (config.SecurityCookieVa != 0 && image.VaToOffset(config.SecurityCookieVa) is not null)
        {
            added += symbols.Add(new Symbol(config.SecurityCookieVa, "__security_cookie", SymbolKind.Data)) ? 1 : 0;
        }

        // These fields hold a *pointer* to the routine, which the loader fills in; the pointer's
        // initial value in the file is the routine itself.
        added += AddIndirect(image, symbols, config.GuardCfCheckFunctionPointerVa, "_guard_check_icall");
        added += AddIndirect(image, symbols, config.GuardCfDispatchFunctionPointerVa, "_guard_dispatch_icall");
        return added;
    }

    private static int AddIndirect(PeImage image, SymbolTable symbols, ulong pointerVa, string name)
    {
        if (pointerVa == 0 || image.VaToRva(pointerVa) is not { } rva || image.ReadPointerAtRva(rva) is not { } target || target == 0)
        {
            return 0;
        }

        var section = image.SectionFromVa(target);
        return section is { IsExecutable: true } && symbols.Add(new Symbol(target, name, SymbolKind.Function)) ? 1 : 0;
    }

    /// <summary>
    /// A canonical name for a discovered helper, or null when nothing matches confidently.
    /// Only called for functions discovery has not otherwise named.
    /// </summary>
    public static string? Identify(Function function, PeImage image)
    {
        // All of these are small; anything long is application code that happens to share an opcode.
        if (function.InstructionCount is 0 or > 32)
        {
            return null;
        }

        var text = function.Instructions.Select(i => i.Text).ToArray();
        bool is64 = image.Bitness == 64;

        if (IsStackProbe(text, is64))
        {
            return is64 ? "__chkstk" : "_chkstk";
        }

        if (image.LoadConfig is { SecurityCookieVa: not 0 } config)
        {
            string cookie = $"0x{config.SecurityCookieVa:x}";
            bool touchesCookie = text.Any(t => t.Contains(cookie, StringComparison.OrdinalIgnoreCase));

            // cmp against the cookie, a conditional jump to the failure path, and nothing else.
            if (touchesCookie && function.InstructionCount <= 8 && text[0].StartsWith("cmp ", StringComparison.Ordinal))
            {
                return "__security_check_cookie";
            }

            // The SEH prologue helper installs a frame from fs:[0] and mixes in the cookie.
            if (!is64 && touchesCookie && text.Any(t => t.Contains("fs:[0", StringComparison.OrdinalIgnoreCase)))
            {
                return "__SEH_prolog4";
            }
        }

        if (!is64 && IsEhPrologue(text))
        {
            return "__EH_prolog";
        }

        return null;
    }

    /// <summary>
    /// <c>__chkstk</c> walks the stack a page at a time so the guard page is hit in order. The page
    /// stride plus the borrow trick are what make it recognisable.
    /// </summary>
    private static bool IsStackProbe(string[] text, bool is64)
    {
        // Both forms walk down the stack one page at a time, so the page size is always present.
        bool pageStride = text.Any(t => t.Contains("0x1000", StringComparison.OrdinalIgnoreCase));
        if (!pageStride)
        {
            return false;
        }

        if (is64)
        {
            // ntdll's __chkstk: reads the thread stack limit from the TEB, walks down with
            // lea r11,[r11-0x1000] (not sub), and touches each page with test [r11], r11b.
            bool stackLimit = text.Any(t => t.Contains("gs:[0x10]", StringComparison.OrdinalIgnoreCase));
            bool touchesPage = text.Any(t => t.StartsWith("test ", StringComparison.Ordinal)
                                             || t.StartsWith("or ", StringComparison.Ordinal)
                                             || t.StartsWith("mov byte", StringComparison.Ordinal));
            bool touchesStack = text.Any(t => t.Contains("rsp", StringComparison.Ordinal));
            return touchesStack && (stackLimit || touchesPage);
        }

        // x86: push ecx; lea ecx,[esp+4]; sub ecx,eax; sbb eax,eax; not eax; and ecx,eax
        bool borrow = text.Any(t => t.StartsWith("sbb ", StringComparison.Ordinal) && t.Contains("eax, eax", StringComparison.Ordinal));
        bool negate = text.Any(t => t.StartsWith("not eax", StringComparison.Ordinal));
        return borrow && negate;
    }

    /// <summary><c>__EH_prolog</c> opens by pushing the -1 exception state onto the new frame.</summary>
    private static bool IsEhPrologue(string[] text)
        => text.Length >= 6
           && text[0].StartsWith("push ", StringComparison.Ordinal)
           && (text[0].EndsWith("0xffffffff", StringComparison.OrdinalIgnoreCase) || text[0].EndsWith("-1", StringComparison.Ordinal))
           && text.Any(t => t.StartsWith("lea ebp", StringComparison.Ordinal))
           && text.Any(t => t.StartsWith("sub esp, eax", StringComparison.Ordinal));
}
