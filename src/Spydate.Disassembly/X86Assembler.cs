using System.Globalization;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Spydate.Disassembly;

/// <summary>Encoded bytes, or why the text could not be turned into any.</summary>
public sealed record AssembleResult(byte[] Bytes, string? Problem)
{
    public bool Ok => Problem is null;

    public static AssembleResult Failed(string problem) => new([], problem);
}

/// <summary>
/// Turns typed instructions into bytes.
///
/// A deliberate subset, not an assembler. Iced encodes — that part is exact — but it is driven by a
/// fluent API rather than by text, so everything a caller may type has to be recognised here by
/// hand. The set below is what patching actually needs: stop a call happening, change what a
/// register holds, send a branch somewhere else. Anything outside it is refused by name rather than
/// guessed at, because a patch that assembles to the wrong instruction is far worse than one that
/// refuses to assemble.
///
/// <c>bytes:</c> takes hex instead, for anything this does not cover. It is a prefix rather than a
/// guess about the text, since <c>add</c> and <c>dead</c> are both perfectly good hex.
/// </summary>
public static class X86Assembler
{
    /// <summary>Mnemonics taking no operands.</summary>
    private static readonly HashSet<string> Nullary = new(StringComparer.OrdinalIgnoreCase)
    {
        "nop", "int3", "ret", "retn", "leave", "cdq", "cqo", "cdqe", "cwde", "hlt", "ud2", "stc", "clc", "cld", "pushfq", "popfq",
    };

    /// <summary>Conditional jumps, by the name people type.</summary>
    private static readonly HashSet<string> Jumps = new(StringComparer.OrdinalIgnoreCase)
    {
        "jmp", "call",
        "je", "jz", "jne", "jnz", "jg", "jnle", "jge", "jnl", "jl", "jnge", "jle", "jng",
        "ja", "jnbe", "jae", "jnb", "jb", "jnae", "jbe", "jna", "js", "jns", "jo", "jno", "jp", "jnp",
    };

    /// <summary>
    /// Assembles <paramref name="text"/> as if it were at <paramref name="rip"/> — which matters,
    /// because a jump is encoded as a distance from where it sits.
    /// </summary>
    public static AssembleResult Encode(string? text, bool is64Bit, ulong rip)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return AssembleResult.Failed("nothing to assemble");
        }

        string trimmed = text.Trim();
        if (trimmed.StartsWith("bytes:", StringComparison.OrdinalIgnoreCase))
        {
            return Hex(trimmed["bytes:".Length..]);
        }

        var assembler = new Assembler(is64Bit ? 64 : 32);

        foreach (string line in trimmed.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string statement = Strip(line);
            if (statement.Length == 0)
            {
                continue;
            }

            if (One(assembler, statement, is64Bit) is { } problem)
            {
                return AssembleResult.Failed(problem);
            }
        }

        try
        {
            var stream = new MemoryStream();
            assembler.Assemble(new StreamCodeWriter(stream), rip);
            byte[] bytes = stream.ToArray();

            return bytes.Length == 0
                ? AssembleResult.Failed("nothing to assemble")
                : new AssembleResult(bytes, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return AssembleResult.Failed($"could not encode that: {ex.Message}");
        }
    }

    private static AssembleResult Hex(string text)
    {
        string cleaned = new(text.Where(c => !char.IsWhiteSpace(c) && c != ',').ToArray());
        if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[2..];
        }

        if (cleaned.Length == 0 || cleaned.Length % 2 != 0)
        {
            return AssembleResult.Failed("give whole bytes, two hex digits each");
        }

        try
        {
            return new AssembleResult(Convert.FromHexString(cleaned), null);
        }
        catch (FormatException)
        {
            return AssembleResult.Failed("that is not hex");
        }
    }

    /// <summary>Drops a trailing comment, so a pasted line with one still assembles.</summary>
    private static string Strip(string line)
    {
        int hash = line.IndexOfAny([';', '#']);
        int slashes = line.IndexOf("//", StringComparison.Ordinal);
        int cut = hash >= 0 && slashes >= 0 ? Math.Min(hash, slashes) : Math.Max(hash, slashes);
        return (cut >= 0 ? line[..cut] : line).Trim();
    }

    /// <summary>Adds one statement to the assembler, or returns why it could not.</summary>
    private static string? One(Assembler assembler, string statement, bool is64Bit)
    {
        string[] parts = statement.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
        string mnemonic = parts[0].ToLowerInvariant();
        string[] operands = parts.Length > 1
            ? parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        if (Nullary.Contains(mnemonic) && operands.Length == 0)
        {
            return Nullary0(assembler, mnemonic);
        }

        if (mnemonic is "ret" or "retn" && operands.Length == 1 && Immediate(operands[0]) is { } popped)
        {
            assembler.ret((ushort)popped);
            return null;
        }

        if (Jumps.Contains(mnemonic) && operands.Length == 1)
        {
            if (Immediate(operands[0]) is not { } target)
            {
                return $"'{operands[0]}' is not an address — give one in hex, like 0x140001000";
            }

            return Branch(assembler, mnemonic, (ulong)target);
        }

        if (operands.Length == 1)
        {
            return Unary(assembler, mnemonic, operands[0]);
        }

        if (operands.Length == 2)
        {
            return Binary(assembler, mnemonic, operands[0], operands[1]);
        }

        return $"'{mnemonic}' is not one of the instructions this understands — use bytes: <hex> for anything else";
    }

    private static string? Nullary0(Assembler a, string mnemonic)
    {
        switch (mnemonic)
        {
            case "nop": a.nop(); return null;
            case "int3": a.int3(); return null;
            case "ret" or "retn": a.ret(); return null;
            case "leave": a.leave(); return null;
            case "cdq": a.cdq(); return null;
            case "cqo": a.cqo(); return null;
            case "cdqe": a.cdqe(); return null;
            case "cwde": a.cwde(); return null;
            case "hlt": a.hlt(); return null;
            case "ud2": a.ud2(); return null;
            case "stc": a.stc(); return null;
            case "clc": a.clc(); return null;
            case "cld": a.cld(); return null;
            case "pushfq": a.pushfq(); return null;
            case "popfq": a.popfq(); return null;
            default: return $"'{mnemonic}' takes operands";
        }
    }

    private static string? Branch(Assembler a, string mnemonic, ulong target)
    {
        switch (mnemonic)
        {
            case "jmp": a.jmp(target); return null;
            case "call": a.call(target); return null;
            case "je" or "jz": a.je(target); return null;
            case "jne" or "jnz": a.jne(target); return null;
            case "jg" or "jnle": a.jg(target); return null;
            case "jge" or "jnl": a.jge(target); return null;
            case "jl" or "jnge": a.jl(target); return null;
            case "jle" or "jng": a.jle(target); return null;
            case "ja" or "jnbe": a.ja(target); return null;
            case "jae" or "jnb": a.jae(target); return null;
            case "jb" or "jnae": a.jb(target); return null;
            case "jbe" or "jna": a.jbe(target); return null;
            case "js": a.js(target); return null;
            case "jns": a.jns(target); return null;
            case "jo": a.jo(target); return null;
            case "jno": a.jno(target); return null;
            case "jp": a.jp(target); return null;
            case "jnp": a.jnp(target); return null;
            default: return $"'{mnemonic}' is not a jump this understands";
        }
    }

    private static string? Unary(Assembler a, string mnemonic, string operand)
    {
        int width = WidthOf(operand);
        if (width == 0)
        {
            return $"'{operand}' is not a register this understands";
        }

        switch (width)
        {
            case 64:
                var r64 = Reg64(operand);
                switch (mnemonic)
                {
                    case "push": a.push(r64); return null;
                    case "pop": a.pop(r64); return null;
                    case "inc": a.inc(r64); return null;
                    case "dec": a.dec(r64); return null;
                    case "neg": a.neg(r64); return null;
                    case "not": a.not(r64); return null;
                    case "jmp": a.jmp(r64); return null;
                    case "call": a.call(r64); return null;
                    default: return Unknown(mnemonic);
                }

            case 32:
                var r32 = Reg32(operand);
                switch (mnemonic)
                {
                    case "push": a.push(r32); return null;
                    case "pop": a.pop(r32); return null;
                    case "inc": a.inc(r32); return null;
                    case "dec": a.dec(r32); return null;
                    case "neg": a.neg(r32); return null;
                    case "not": a.not(r32); return null;
                    default: return Unknown(mnemonic);
                }

            default:
                return $"'{mnemonic} {operand}' is not something this understands — use bytes: <hex>";
        }
    }

    private static string? Binary(Assembler a, string mnemonic, string left, string right)
    {
        int width = WidthOf(left);
        if (width == 0)
        {
            return $"'{left}' is not a register this understands";
        }

        int rightWidth = WidthOf(right);
        if (rightWidth != 0 && rightWidth != width)
        {
            return $"{left} and {right} are different sizes";
        }

        bool immediate = rightWidth == 0;
        long value = 0;
        if (immediate)
        {
            if (Immediate(right) is not { } parsed)
            {
                return $"'{right}' is neither a register nor a number";
            }

            value = parsed;
        }

        switch (width)
        {
            case 64:
                var l64 = Reg64(left);
                if (immediate)
                {
                    switch (mnemonic)
                    {
                        case "mov": a.mov(l64, value); return null;
                        case "add": a.add(l64, (int)value); return null;
                        case "sub": a.sub(l64, (int)value); return null;
                        case "and": a.and(l64, (int)value); return null;
                        case "or": a.or(l64, (int)value); return null;
                        case "xor": a.xor(l64, (int)value); return null;
                        case "cmp": a.cmp(l64, (int)value); return null;
                        case "test": a.test(l64, (int)value); return null;
                        default: return Unknown(mnemonic);
                    }
                }

                var r64 = Reg64(right);
                switch (mnemonic)
                {
                    case "mov": a.mov(l64, r64); return null;
                    case "add": a.add(l64, r64); return null;
                    case "sub": a.sub(l64, r64); return null;
                    case "and": a.and(l64, r64); return null;
                    case "or": a.or(l64, r64); return null;
                    case "xor": a.xor(l64, r64); return null;
                    case "cmp": a.cmp(l64, r64); return null;
                    case "test": a.test(l64, r64); return null;
                    default: return Unknown(mnemonic);
                }

            case 32:
                var l32 = Reg32(left);
                if (immediate)
                {
                    switch (mnemonic)
                    {
                        case "mov": a.mov(l32, (int)value); return null;
                        case "add": a.add(l32, (int)value); return null;
                        case "sub": a.sub(l32, (int)value); return null;
                        case "and": a.and(l32, (int)value); return null;
                        case "or": a.or(l32, (int)value); return null;
                        case "xor": a.xor(l32, (int)value); return null;
                        case "cmp": a.cmp(l32, (int)value); return null;
                        case "test": a.test(l32, (int)value); return null;
                        default: return Unknown(mnemonic);
                    }
                }

                var r32 = Reg32(right);
                switch (mnemonic)
                {
                    case "mov": a.mov(l32, r32); return null;
                    case "add": a.add(l32, r32); return null;
                    case "sub": a.sub(l32, r32); return null;
                    case "and": a.and(l32, r32); return null;
                    case "or": a.or(l32, r32); return null;
                    case "xor": a.xor(l32, r32); return null;
                    case "cmp": a.cmp(l32, r32); return null;
                    case "test": a.test(l32, r32); return null;
                    default: return Unknown(mnemonic);
                }

            case 8:
                var l8 = Reg8(left);
                if (immediate)
                {
                    switch (mnemonic)
                    {
                        case "mov": a.mov(l8, (byte)value); return null;
                        case "cmp": a.cmp(l8, (byte)value); return null;
                        default: return Unknown(mnemonic);
                    }
                }

                var r8 = Reg8(right);
                switch (mnemonic)
                {
                    case "mov": a.mov(l8, r8); return null;
                    case "xor": a.xor(l8, r8); return null;
                    case "cmp": a.cmp(l8, r8); return null;
                    case "test": a.test(l8, r8); return null;
                    default: return Unknown(mnemonic);
                }

            default:
                return $"'{mnemonic} {left}, {right}' is not something this understands — use bytes: <hex>";
        }
    }

    private static string Unknown(string mnemonic)
        => $"'{mnemonic}' is not one of the instructions this understands — use bytes: <hex> for anything else";

    private static long? Immediate(string text)
    {
        string t = text.Trim();
        bool negative = t.StartsWith('-');
        if (negative)
        {
            t = t[1..].Trim();
        }

        bool hex = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (hex)
        {
            t = t[2..];
        }
        else if (t.EndsWith('h') || t.EndsWith('H'))
        {
            t = t[..^1];
            hex = true;
        }

        bool parsed = hex
            ? ulong.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong u)
            : ulong.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out u);

        return parsed ? (negative ? -(long)u : (long)u) : null;
    }

    private static readonly string[] Names64 =
        ["rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15"];

    private static readonly string[] Names32 =
        ["eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi", "r8d", "r9d", "r10d", "r11d", "r12d", "r13d", "r14d", "r15d"];

    private static readonly string[] Names8 =
        ["al", "cl", "dl", "bl", "spl", "bpl", "sil", "dil", "r8b", "r9b", "r10b", "r11b", "r12b", "r13b", "r14b", "r15b"];

    private static int WidthOf(string name)
    {
        string n = name.Trim().ToLowerInvariant();
        if (Array.IndexOf(Names64, n) >= 0)
        {
            return 64;
        }

        if (Array.IndexOf(Names32, n) >= 0)
        {
            return 32;
        }

        return Array.IndexOf(Names8, n) >= 0 ? 8 : 0;
    }

    private static readonly AssemblerRegister64[] All64 =
        [rax, rcx, rdx, rbx, rsp, rbp, rsi, rdi, r8, r9, r10, r11, r12, r13, r14, r15];

    private static readonly AssemblerRegister32[] All32 =
        [eax, ecx, edx, ebx, esp, ebp, esi, edi, r8d, r9d, r10d, r11d, r12d, r13d, r14d, r15d];

    private static readonly AssemblerRegister8[] All8 =
        [al, cl, dl, bl, spl, bpl, sil, dil, r8b, r9b, r10b, r11b, r12b, r13b, r14b, r15b];

    private static AssemblerRegister64 Reg64(string name) => All64[Array.IndexOf(Names64, name.Trim().ToLowerInvariant())];

    private static AssemblerRegister32 Reg32(string name) => All32[Array.IndexOf(Names32, name.Trim().ToLowerInvariant())];

    private static AssemblerRegister8 Reg8(string name) => All8[Array.IndexOf(Names8, name.Trim().ToLowerInvariant())];
}
