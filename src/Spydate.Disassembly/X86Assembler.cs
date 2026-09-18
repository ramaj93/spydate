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
/// register or a field holds, send a branch somewhere else. Anything outside it is refused by name
/// rather than guessed at, because a patch that assembles to the wrong instruction is far worse than
/// one that refuses to assemble.
///
/// The one rule the subset has to keep is that it reads back what the listing shows. An analyst
/// patches by editing the line in front of them, and <c>mov byte ptr [rdi+4], 0</c> is what that
/// line says — so memory operands are part of the subset even though they cost more code than
/// registers do, and the sizes and segments are spelled the way the formatter spells them.
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

        // A branch through memory — jmp qword ptr [rax*8+0x14000] — is a jump table, not a target, so
        // it goes the memory route below rather than being read as an address.
        if (Jumps.Contains(mnemonic) && operands.Length == 1 && !LooksLikeMemory(operands[0]))
        {
            if (Immediate(operands[0]) is not { } target)
            {
                return WidthOf(operands[0]) != 0
                    ? Unary(assembler, mnemonic, operands[0], is64Bit)
                    : $"'{operands[0]}' is not an address — give one in hex, like 0x140001000";
            }

            return Branch(assembler, mnemonic, (ulong)target);
        }

        if (operands.Length == 1)
        {
            return Unary(assembler, mnemonic, operands[0], is64Bit);
        }

        if (operands.Length == 2)
        {
            return Binary(assembler, mnemonic, operands[0], operands[1], is64Bit);
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

    private static string? Unary(Assembler a, string mnemonic, string operand, bool is64Bit)
    {
        if (LooksLikeMemory(operand))
        {
            if (ParseMemory(operand, is64Bit, out string? memoryProblem) is not { } memory)
            {
                return memoryProblem;
            }

            if (memory.Size == 0)
            {
                return $"say how big '{operand}' is — byte ptr, word ptr, dword ptr or qword ptr";
            }

            if (Address(memory, memory.Size, out string? addressProblem) is not { } place)
            {
                return addressProblem;
            }

            switch (mnemonic)
            {
                case "inc": a.inc(place); return null;
                case "dec": a.dec(place); return null;
                case "neg": a.neg(place); return null;
                case "not": a.not(place); return null;
                case "push": a.push(place); return null;
                case "pop": a.pop(place); return null;
                case "jmp": a.jmp(place); return null;
                case "call": a.call(place); return null;
                default: return Unknown(mnemonic);
            }
        }

        int width = WidthOf(operand);
        if (width == 0)
        {
            return NotAnOperand(operand);
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

            case 16:
                var r16 = Reg16(operand);
                switch (mnemonic)
                {
                    case "push": a.push(r16); return null;
                    case "pop": a.pop(r16); return null;
                    case "inc": a.inc(r16); return null;
                    case "dec": a.dec(r16); return null;
                    case "neg": a.neg(r16); return null;
                    case "not": a.not(r16); return null;
                    default: return Unknown(mnemonic);
                }

            case 8:
                var r8 = Reg8(operand);
                switch (mnemonic)
                {
                    case "inc": a.inc(r8); return null;
                    case "dec": a.dec(r8); return null;
                    case "neg": a.neg(r8); return null;
                    case "not": a.not(r8); return null;
                    default: return Unknown(mnemonic);
                }

            default:
                return $"'{mnemonic} {operand}' is not something this understands — use bytes: <hex>";
        }
    }

    private static string? Binary(Assembler a, string mnemonic, string left, string right, bool is64Bit)
    {
        // Memory takes its own route on either side: the operand cannot even be built until its size
        // is settled, and the size comes either from an explicit "byte ptr" or from the register on
        // the other side.
        if (LooksLikeMemory(left))
        {
            return LooksLikeMemory(right)
                ? "only one side can be memory"
                : IntoMemory(a, mnemonic, left, right, is64Bit);
        }

        if (LooksLikeMemory(right))
        {
            return OutOfMemory(a, mnemonic, left, right, is64Bit);
        }

        int width = WidthOf(left);
        if (width == 0)
        {
            return NotAnOperand(left);
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

            case 16:
                var l16 = Reg16(left);
                if (immediate)
                {
                    switch (mnemonic)
                    {
                        case "mov": a.mov(l16, (short)value); return null;
                        case "add": a.add(l16, (short)value); return null;
                        case "sub": a.sub(l16, (short)value); return null;
                        case "and": a.and(l16, (short)value); return null;
                        case "or": a.or(l16, (short)value); return null;
                        case "xor": a.xor(l16, (short)value); return null;
                        case "cmp": a.cmp(l16, (short)value); return null;
                        case "test": a.test(l16, (short)value); return null;
                        default: return Unknown(mnemonic);
                    }
                }

                var r16 = Reg16(right);
                switch (mnemonic)
                {
                    case "mov": a.mov(l16, r16); return null;
                    case "add": a.add(l16, r16); return null;
                    case "sub": a.sub(l16, r16); return null;
                    case "and": a.and(l16, r16); return null;
                    case "or": a.or(l16, r16); return null;
                    case "xor": a.xor(l16, r16); return null;
                    case "cmp": a.cmp(l16, r16); return null;
                    case "test": a.test(l16, r16); return null;
                    default: return Unknown(mnemonic);
                }

            case 8:
                var l8 = Reg8(left);
                if (immediate)
                {
                    switch (mnemonic)
                    {
                        case "mov": a.mov(l8, (byte)value); return null;
                        case "add": a.add(l8, (byte)value); return null;
                        case "sub": a.sub(l8, (byte)value); return null;
                        case "and": a.and(l8, (byte)value); return null;
                        case "or": a.or(l8, (byte)value); return null;
                        case "xor": a.xor(l8, (byte)value); return null;
                        case "cmp": a.cmp(l8, (byte)value); return null;
                        case "test": a.test(l8, (byte)value); return null;
                        default: return Unknown(mnemonic);
                    }
                }

                var r8 = Reg8(right);
                switch (mnemonic)
                {
                    case "mov": a.mov(l8, r8); return null;
                    case "add": a.add(l8, r8); return null;
                    case "sub": a.sub(l8, r8); return null;
                    case "and": a.and(l8, r8); return null;
                    case "or": a.or(l8, r8); return null;
                    case "xor": a.xor(l8, r8); return null;
                    case "cmp": a.cmp(l8, r8); return null;
                    case "test": a.test(l8, r8); return null;
                    default: return Unknown(mnemonic);
                }

            default:
                return $"'{mnemonic} {left}, {right}' is not something this understands — use bytes: <hex>";
        }
    }

    /// <summary>mov byte ptr [rdi+4], 1 — a register or a number written into memory.</summary>
    private static string? IntoMemory(Assembler a, string mnemonic, string left, string right, bool is64Bit)
    {
        if (ParseMemory(left, is64Bit, out string? memoryProblem) is not { } memory)
        {
            return memoryProblem;
        }

        int sourceWidth = WidthOf(right);
        if (sourceWidth != 0 && memory.Size != 0 && sourceWidth != memory.Size)
        {
            return $"{right} is {sourceWidth}-bit and {left} is {memory.Size}-bit";
        }

        int size = memory.Size != 0 ? memory.Size : sourceWidth;
        if (size == 0)
        {
            return $"say how big '{left}' is — byte ptr, word ptr, dword ptr or qword ptr";
        }

        if (Address(memory, size, out string? addressProblem) is not { } place)
        {
            return addressProblem;
        }

        if (sourceWidth == 0)
        {
            if (Immediate(right) is not { } value)
            {
                return $"'{right}' is neither a register nor a number";
            }

            switch (mnemonic)
            {
                case "mov": a.mov(place, (int)value); return null;
                case "add": a.add(place, (int)value); return null;
                case "sub": a.sub(place, (int)value); return null;
                case "and": a.and(place, (int)value); return null;
                case "or": a.or(place, (int)value); return null;
                case "xor": a.xor(place, (int)value); return null;
                case "cmp": a.cmp(place, (int)value); return null;
                case "test": a.test(place, (int)value); return null;
                default: return Unknown(mnemonic);
            }
        }

        switch (sourceWidth)
        {
            case 64:
                var s64 = Reg64(right);
                switch (mnemonic)
                {
                    case "mov": a.mov(place, s64); return null;
                    case "add": a.add(place, s64); return null;
                    case "sub": a.sub(place, s64); return null;
                    case "and": a.and(place, s64); return null;
                    case "or": a.or(place, s64); return null;
                    case "xor": a.xor(place, s64); return null;
                    case "cmp": a.cmp(place, s64); return null;
                    case "test": a.test(place, s64); return null;
                    default: return Unknown(mnemonic);
                }

            case 32:
                var s32 = Reg32(right);
                switch (mnemonic)
                {
                    case "mov": a.mov(place, s32); return null;
                    case "add": a.add(place, s32); return null;
                    case "sub": a.sub(place, s32); return null;
                    case "and": a.and(place, s32); return null;
                    case "or": a.or(place, s32); return null;
                    case "xor": a.xor(place, s32); return null;
                    case "cmp": a.cmp(place, s32); return null;
                    case "test": a.test(place, s32); return null;
                    default: return Unknown(mnemonic);
                }

            case 16:
                var s16 = Reg16(right);
                switch (mnemonic)
                {
                    case "mov": a.mov(place, s16); return null;
                    case "add": a.add(place, s16); return null;
                    case "sub": a.sub(place, s16); return null;
                    case "and": a.and(place, s16); return null;
                    case "or": a.or(place, s16); return null;
                    case "xor": a.xor(place, s16); return null;
                    case "cmp": a.cmp(place, s16); return null;
                    case "test": a.test(place, s16); return null;
                    default: return Unknown(mnemonic);
                }

            default:
                var s8 = Reg8(right);
                switch (mnemonic)
                {
                    case "mov": a.mov(place, s8); return null;
                    case "add": a.add(place, s8); return null;
                    case "sub": a.sub(place, s8); return null;
                    case "and": a.and(place, s8); return null;
                    case "or": a.or(place, s8); return null;
                    case "xor": a.xor(place, s8); return null;
                    case "cmp": a.cmp(place, s8); return null;
                    case "test": a.test(place, s8); return null;
                    default: return Unknown(mnemonic);
                }
        }
    }

    /// <summary>
    /// mov eax, dword ptr [rdi+4] — memory read into a register, plus the two instructions whose
    /// whole point is that the sizes differ (<c>movzx</c>, <c>movsx</c>) and the one that takes the
    /// address rather than what is there (<c>lea</c>).
    /// </summary>
    private static string? OutOfMemory(Assembler a, string mnemonic, string left, string right, bool is64Bit)
    {
        int destination = WidthOf(left);
        if (destination == 0)
        {
            return NotAnOperand(left);
        }

        if (ParseMemory(right, is64Bit, out string? memoryProblem) is not { } memory)
        {
            return memoryProblem;
        }

        // lea never reads the memory, so its size is nobody's business; anything encodes the same.
        bool widening = mnemonic is "movzx" or "movsx";
        bool addressOnly = mnemonic is "lea";

        if (!widening && !addressOnly && memory.Size != 0 && memory.Size != destination)
        {
            return $"{left} is {destination}-bit and {right} is {memory.Size}-bit";
        }

        int size = memory.Size != 0 ? memory.Size : destination;
        if (widening && memory.Size == 0)
        {
            return $"say how big '{right}' is — {mnemonic} is the instruction for reading a smaller thing";
        }

        if (Address(memory, addressOnly ? destination : size, out string? addressProblem) is not { } place)
        {
            return addressProblem;
        }

        if (addressOnly)
        {
            switch (destination)
            {
                case 64: a.lea(Reg64(left), place); return null;
                case 32: a.lea(Reg32(left), place); return null;
                case 16: a.lea(Reg16(left), place); return null;
                default: return $"lea cannot load an address into {left}";
            }
        }

        if (widening)
        {
            if (size >= destination)
            {
                return $"{right} is not smaller than {left} — {mnemonic} widens";
            }

            switch (destination)
            {
                case 64: if (mnemonic is "movzx") { a.movzx(Reg64(left), place); } else { a.movsx(Reg64(left), place); } return null;
                case 32: if (mnemonic is "movzx") { a.movzx(Reg32(left), place); } else { a.movsx(Reg32(left), place); } return null;
                default: if (mnemonic is "movzx") { a.movzx(Reg16(left), place); } else { a.movsx(Reg16(left), place); } return null;
            }
        }

        switch (destination)
        {
            case 64:
                var d64 = Reg64(left);
                switch (mnemonic)
                {
                    case "mov": a.mov(d64, place); return null;
                    case "add": a.add(d64, place); return null;
                    case "sub": a.sub(d64, place); return null;
                    case "and": a.and(d64, place); return null;
                    case "or": a.or(d64, place); return null;
                    case "xor": a.xor(d64, place); return null;
                    case "cmp": a.cmp(d64, place); return null;
                    default: return Unknown(mnemonic);
                }

            case 32:
                var d32 = Reg32(left);
                switch (mnemonic)
                {
                    case "mov": a.mov(d32, place); return null;
                    case "add": a.add(d32, place); return null;
                    case "sub": a.sub(d32, place); return null;
                    case "and": a.and(d32, place); return null;
                    case "or": a.or(d32, place); return null;
                    case "xor": a.xor(d32, place); return null;
                    case "cmp": a.cmp(d32, place); return null;
                    default: return Unknown(mnemonic);
                }

            case 16:
                var d16 = Reg16(left);
                switch (mnemonic)
                {
                    case "mov": a.mov(d16, place); return null;
                    case "add": a.add(d16, place); return null;
                    case "sub": a.sub(d16, place); return null;
                    case "and": a.and(d16, place); return null;
                    case "or": a.or(d16, place); return null;
                    case "xor": a.xor(d16, place); return null;
                    case "cmp": a.cmp(d16, place); return null;
                    default: return Unknown(mnemonic);
                }

            default:
                var d8 = Reg8(left);
                switch (mnemonic)
                {
                    case "mov": a.mov(d8, place); return null;
                    case "add": a.add(d8, place); return null;
                    case "sub": a.sub(d8, place); return null;
                    case "and": a.and(d8, place); return null;
                    case "or": a.or(d8, place); return null;
                    case "xor": a.xor(d8, place); return null;
                    case "cmp": a.cmp(d8, place); return null;
                    default: return Unknown(mnemonic);
                }
        }
    }

    private static string Unknown(string mnemonic)
        => $"'{mnemonic}' is not one of the instructions this understands — use bytes: <hex> for anything else";

    private static string NotAnOperand(string operand)
        => $"'{operand}' is not a register this understands — a memory operand needs brackets, like byte ptr [rdi+4]";

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

    /// <summary>A memory operand as typed: <c>[base + index*scale + displacement]</c>.</summary>
    /// <param name="Size">8, 16, 32 or 64 when it was spelled out, 0 when it has to be inferred.</param>
    private sealed record MemoryRef(int Size, string? Segment, string? Base, string? Index, int Scale, long Displacement);

    /// <summary>
    /// Brackets are what makes an operand memory, so the caller can tell the two routes apart before
    /// committing to either — and a malformed bracket reports as a bad address rather than as a
    /// register nobody has heard of.
    /// </summary>
    private static bool LooksLikeMemory(string operand) => operand.Contains('[', StringComparison.Ordinal);

    /// <summary>Reads <c>byte ptr [rdi+rcx*4+8]</c>, or says what it could not make of it.</summary>
    private static MemoryRef? ParseMemory(string text, bool is64Bit, out string? problem)
    {
        problem = null;
        int open = text.IndexOf('[', StringComparison.Ordinal);
        int close = text.LastIndexOf(']');
        if (close < open)
        {
            problem = $"'{text}' is missing its ]";
            return null;
        }

        if (text[(close + 1)..].Trim() is { Length: > 0 } trailing)
        {
            problem = $"'{trailing}' is not expected after ]";
            return null;
        }

        int size = 0;
        string? segment = null;

        foreach (string word in text[..open].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            switch (word.TrimEnd(':').ToLowerInvariant())
            {
                case "byte": size = 8; break;
                case "word": size = 16; break;
                case "dword": size = 32; break;
                case "qword": size = 64; break;
                case "ptr": break;
                case "cs" or "ds" or "es" or "ss" or "fs" or "gs" when word.EndsWith(':'):
                    segment = word.TrimEnd(':').ToLowerInvariant();
                    break;
                default:
                    problem = $"'{word}' is not a size this understands — byte, word, dword or qword ptr";
                    return null;
            }
        }

        string inside = text[(open + 1)..close].Trim();

        // gs:[0x60] and [gs:0x60] are the same thing written by different formatters.
        if (inside.IndexOf(':', StringComparison.Ordinal) is > 0 and var colon
            && inside[..colon].Trim().ToLowerInvariant() is "cs" or "ds" or "es" or "ss" or "fs" or "gs")
        {
            segment = inside[..colon].Trim().ToLowerInvariant();
            inside = inside[(colon + 1)..].Trim();
        }

        string? baseRegister = null;
        string? index = null;
        int scale = 1;
        long displacement = 0;

        foreach (var (sign, term) in Terms(inside))
        {
            if (term.IndexOf('*', StringComparison.Ordinal) is >= 0 and var star)
            {
                string scaled = term[..star].Trim();
                string factor = term[(star + 1)..].Trim();

                // 4*rcx reads as naturally as rcx*4, and both turn up in pasted listings.
                if (WidthOf(scaled) == 0)
                {
                    (scaled, factor) = (factor, scaled);
                }

                if (WidthOf(scaled) == 0)
                {
                    problem = $"'{term}' is not a register times a scale";
                    return null;
                }

                if (Immediate(factor) is not (1 or 2 or 4 or 8))
                {
                    problem = $"'{factor}' is not a scale — 1, 2, 4 or 8";
                    return null;
                }

                if (index is not null)
                {
                    problem = "only one register in [...] can be scaled";
                    return null;
                }

                index = scaled;
                scale = (int)Immediate(factor)!.Value;
                continue;
            }

            if (WidthOf(term) != 0)
            {
                if (sign < 0)
                {
                    problem = $"'{term}' cannot be subtracted — only a displacement can";
                    return null;
                }

                if (baseRegister is null)
                {
                    baseRegister = term;
                }
                else if (index is null)
                {
                    index = term;
                }
                else
                {
                    problem = "too many registers in [...]";
                    return null;
                }

                continue;
            }

            if (Immediate(term) is not { } number)
            {
                problem = $"'{term}' is neither a register nor a number";
                return null;
            }

            displacement += sign * number;
        }

        int addressWidth = baseRegister is { } b ? WidthOf(b) : index is { } i ? WidthOf(i) : 0;
        if (baseRegister is { } both && index is { } other && WidthOf(both) != WidthOf(other))
        {
            problem = $"{both} and {other} are different sizes — an address is built from one size";
            return null;
        }

        if (addressWidth is not (0 or 32 or 64))
        {
            problem = "an address is held in 32- or 64-bit registers";
            return null;
        }

        if (addressWidth == 64 && !is64Bit)
        {
            problem = "this is a 32-bit image, so addresses are 32-bit";
            return null;
        }

        return new MemoryRef(size, segment, baseRegister, index, scale, displacement);
    }

    /// <summary>Splits <c>rdi + rcx*4 - 8</c> into its signed terms.</summary>
    private static List<(int Sign, string Text)> Terms(string inside)
    {
        var terms = new List<(int Sign, string Text)>();
        int start = 0;
        int sign = 1;

        for (int i = 0; i <= inside.Length; i++)
        {
            if (i != inside.Length && inside[i] is not ('+' or '-'))
            {
                continue;
            }

            if (inside[start..i].Trim() is { Length: > 0 } term)
            {
                terms.Add((sign, term));
            }

            if (i != inside.Length)
            {
                sign = inside[i] == '-' ? -1 : 1;
            }

            start = i + 1;
        }

        return terms;
    }

    /// <summary>Builds what Iced wants from what was typed, at the size the caller settled on.</summary>
    private static AssemblerMemoryOperand? Address(MemoryRef memory, int size, out string? problem)
    {
        problem = null;
        if (Sized(size, memory.Segment) is not { } factory)
        {
            problem = $"a {size}-bit memory operand is not something this understands — use bytes: <hex>";
            return null;
        }

        if (memory.Base is null && memory.Index is null)
        {
            return factory[memory.Displacement];
        }

        if (WidthOf(memory.Base ?? memory.Index!) == 64)
        {
            AssemblerMemoryOperand core;
            if (memory.Index is { } index64)
            {
                core = Reg64(index64) * memory.Scale;
                core = memory.Base is { } b64 ? Reg64(b64) + core : core;
                core += memory.Displacement;
            }
            else
            {
                core = Reg64(memory.Base!) + memory.Displacement;
            }

            return factory[core];
        }

        AssemblerMemoryOperand flat;
        if (memory.Index is { } index32)
        {
            flat = Reg32(index32) * memory.Scale;
            flat = memory.Base is { } b32 ? Reg32(b32) + flat : flat;
            flat += memory.Displacement;
        }
        else
        {
            flat = Reg32(memory.Base!) + memory.Displacement;
        }

        return factory[flat];
    }

    private static AssemblerMemoryOperandFactory? Sized(int size, string? segment)
    {
        AssemblerMemoryOperandFactory? sized = size switch
        {
            8 => __byte_ptr,
            16 => __word_ptr,
            32 => __dword_ptr,
            64 => __qword_ptr,
            _ => null,
        };

        if (sized is not { } factory)
        {
            return null;
        }

        return segment switch
        {
            null => factory,
            "cs" => factory.cs,
            "ds" => factory.ds,
            "es" => factory.es,
            "ss" => factory.ss,
            "fs" => factory.fs,
            "gs" => factory.gs,
            _ => null,
        };
    }

    private static readonly string[] Names64 =
        ["rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15"];

    private static readonly string[] Names32 =
        ["eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi", "r8d", "r9d", "r10d", "r11d", "r12d", "r13d", "r14d", "r15d"];

    private static readonly string[] Names16 =
        ["ax", "cx", "dx", "bx", "sp", "bp", "si", "di", "r8w", "r9w", "r10w", "r11w", "r12w", "r13w", "r14w", "r15w"];

    private static readonly string[] Names8 =
        ["al", "cl", "dl", "bl", "spl", "bpl", "sil", "dil", "r8b", "r9b", "r10b", "r11b", "r12b", "r13b", "r14b", "r15b",
         "ah", "ch", "dh", "bh"];

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

        if (Array.IndexOf(Names16, n) >= 0)
        {
            return 16;
        }

        return Array.IndexOf(Names8, n) >= 0 ? 8 : 0;
    }

    private static readonly AssemblerRegister64[] All64 =
        [rax, rcx, rdx, rbx, rsp, rbp, rsi, rdi, r8, r9, r10, r11, r12, r13, r14, r15];

    private static readonly AssemblerRegister32[] All32 =
        [eax, ecx, edx, ebx, esp, ebp, esi, edi, r8d, r9d, r10d, r11d, r12d, r13d, r14d, r15d];

    private static readonly AssemblerRegister16[] All16 =
        [ax, cx, dx, bx, sp, bp, si, di, r8w, r9w, r10w, r11w, r12w, r13w, r14w, r15w];

    private static readonly AssemblerRegister8[] All8 =
        [al, cl, dl, bl, spl, bpl, sil, dil, r8b, r9b, r10b, r11b, r12b, r13b, r14b, r15b,
         ah, ch, dh, bh];

    private static AssemblerRegister64 Reg64(string name) => All64[Array.IndexOf(Names64, name.Trim().ToLowerInvariant())];

    private static AssemblerRegister32 Reg32(string name) => All32[Array.IndexOf(Names32, name.Trim().ToLowerInvariant())];

    private static AssemblerRegister16 Reg16(string name) => All16[Array.IndexOf(Names16, name.Trim().ToLowerInvariant())];

    private static AssemblerRegister8 Reg8(string name) => All8[Array.IndexOf(Names8, name.Trim().ToLowerInvariant())];
}
