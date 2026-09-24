using System.Globalization;
using System.Text;
using Spydate.Core.Dex;
using Spydate.Core.Jvm;

namespace Spydate.Decompiler.Jvm;

/// <summary>
/// A method's Dalvik code as a listing, shaped like smali but with names a reader need not decode: every string,
/// type, field and method index resolved to what it names in Java terms, a branch naming the address it goes to
/// (in code units, four hex digits), a switch listing its cases, and a register followed by the variable's name
/// where the debug table has one. Then the try ranges and the locals.
/// </summary>
internal static class DalvikListing
{
    private const int MaxLiteral = 400;

    public static void Code(StringBuilder sb, JvmMethod method, DexCode code, string indent)
    {
        var file = code.File;
        int firstParameter = code.Registers - code.Ins;
        sb.Append(indent).Append(CultureInfo.InvariantCulture,
            $"registers {code.Registers} (v{firstParameter}-v{code.Registers - 1} hold the arguments), {code.Insns.Length} code units\n");

        var lines = code.Debug?.Lines ?? [];
        int line = 0;
        foreach (var instruction in Dalvik.Decode(code.Insns.Span))
        {
            while (line < lines.Count && lines[line].Address <= instruction.Address)
            {
                if (lines[line].Address == instruction.Address)
                {
                    sb.Append(indent).Append(CultureInfo.InvariantCulture, $"// line {lines[line].Line}\n");
                }

                line++;
            }

            sb.Append(indent).Append(CultureInfo.InvariantCulture, $"{instruction.Address:X4}  ");
            if (instruction.Problem is { } problem && instruction.Cases is null && instruction.ArrayData is null)
            {
                sb.Append(CultureInfo.InvariantCulture, $"?? {instruction.Mnemonic}  // {problem}\n");
                continue;
            }

            string operands = Operands(instruction, file, code);
            sb.Append(instruction.Mnemonic);
            if (operands.Length > 0)
            {
                sb.Append(' ').Append(operands);
            }

            if (Names(instruction, code) is { Length: > 0 } names)
            {
                sb.Append("  // ").Append(names);
            }

            sb.Append('\n');
            if (instruction.Cases is { } cases)
            {
                foreach (var (key, target) in cases)
                {
                    sb.Append(indent).Append(CultureInfo.InvariantCulture, $"        case {key} -> {target:X4}\n");
                }
            }
        }

        if (code.Tries.Count > 0)
        {
            sb.Append(indent).Append("try ranges\n");
            foreach (var attempt in code.Tries)
            {
                foreach (var (type, address) in attempt.Handlers)
                {
                    sb.Append(indent).Append(CultureInfo.InvariantCulture,
                        $"  {attempt.Start:X4}-{attempt.Start + attempt.Length:X4} -> {address:X4}  {Descriptors.TypeName(type)}\n");
                }

                if (attempt.CatchAll is { } all)
                {
                    sb.Append(indent).Append(CultureInfo.InvariantCulture, $"  {attempt.Start:X4}-{attempt.Start + attempt.Length:X4} -> {all:X4}  any\n");
                }
            }
        }

        if (code.Debug is { Locals.Count: > 0 } debug)
        {
            sb.Append(indent).Append("locals\n");
            foreach (var local in debug.Locals.OrderBy(l => l.Register).ThenBy(l => l.Start))
            {
                string type = local.Type is null ? "?" : local.Signature is { } signature && Descriptors.FieldSignature(signature) is { } generic ? generic : Descriptors.TypeName(local.Type);
                sb.Append(indent).Append(CultureInfo.InvariantCulture,
                    $"  v{local.Register,-3} {local.Name ?? "?",-16} {type,-32} {local.Start:X4}-{local.End:X4}\n");
            }
        }

        _ = method;
    }

    private static string Operands(DalvikInstruction i, DexFile? file, DexCode code)
    {
        string V(int register) => $"v{register}";
        string Index() => Reference(i.IndexKind, i.Index, file);
        return i.Format switch
        {
            DalvikFormat.F10x => string.Empty,
            DalvikFormat.F12x or DalvikFormat.F22x or DalvikFormat.F32x => $"{V(i.A)}, {V(i.B)}",
            DalvikFormat.F11x => V(i.A),
            DalvikFormat.F11n or DalvikFormat.F21s or DalvikFormat.F21h or DalvikFormat.F31i or DalvikFormat.F51l => $"{V(i.A)}, {Literal(i)}",
            DalvikFormat.F10t or DalvikFormat.F20t or DalvikFormat.F30t => $"-> {i.Target:X4}",
            DalvikFormat.F21t => $"{V(i.A)}, -> {i.Target:X4}",
            DalvikFormat.F22t => $"{V(i.A)}, {V(i.B)}, -> {i.Target:X4}",
            DalvikFormat.F21c or DalvikFormat.F31c => $"{V(i.A)}, {Index()}",
            DalvikFormat.F22c => $"{V(i.A)}, {V(i.B)}, {Index()}",
            DalvikFormat.F23x => $"{V(i.A)}, {V(i.B)}, {V(i.C)}",
            DalvikFormat.F22b or DalvikFormat.F22s => $"{V(i.A)}, {V(i.B)}, #{i.Literal}",
            DalvikFormat.F31t when i.ArrayData is { } data => $"{V(i.A)}, {{{string.Join(", ", data.Values.Take(64))}{(data.Values.Count > 64 ? ", …" : string.Empty)}}}",
            DalvikFormat.F31t => $"{V(i.A)}, -> {i.Target:X4}",
            DalvikFormat.F35c or DalvikFormat.F3rc => $"{{{string.Join(", ", i.Registers.Select(V))}}}, {Index()}",
            DalvikFormat.F45cc or DalvikFormat.F4rcc
                => $"{{{string.Join(", ", i.Registers.Select(V))}}}, {Index()}, {Reference(DalvikIndex.Proto, i.Proto, file)}",
            _ => string.Empty,
        };
    }

    /// <summary>A constant as the instruction's width reads it: <c>const-wide</c> is a long, the rest ints, with the hex beside a large one.</summary>
    private static string Literal(DalvikInstruction i)
    {
        bool wide = i.Opcode is 0x16 or 0x17 or 0x18 or 0x19;
        string value = wide ? i.Literal.ToString(CultureInfo.InvariantCulture) + "L" : ((int)i.Literal).ToString(CultureInfo.InvariantCulture);
        bool large = wide ? i.Literal is > 0xFFFF or < -0xFFFF : (int)i.Literal is > 0xFFFF or < -0xFFFF;
        return large ? $"{value} (0x{(wide ? i.Literal : (uint)(int)i.Literal):X})" : value;
    }

    private static string Reference(DalvikIndex kind, int index, DexFile? file)
    {
        if (file is null)
        {
            return $"{kind.ToString().ToLowerInvariant()}@{index}";
        }

        switch (kind)
        {
            case DalvikIndex.String when index >= 0 && index < file.Strings.Count:
                string text = file.Strings[index];
                return Quote(text.Length > MaxLiteral ? text[..MaxLiteral] + "…" : text);
            case DalvikIndex.Type when index >= 0 && index < file.Types.Count:
                return Descriptors.TypeName(file.Types[index]);
            case DalvikIndex.Field when index >= 0 && index < file.FieldRefs.Count:
                var field = file.FieldRefs[index];
                return $"{Descriptors.TypeName(field.Owner)}.{field.Name} : {Descriptors.TypeName(field.Type)}";
            case DalvikIndex.Method when index >= 0 && index < file.MethodRefs.Count:
                var method = file.MethodRefs[index];
                return $"{Descriptors.TypeName(method.Owner)}.{method.Name}({string.Join(", ", method.Proto.Parameters.Select(p => Descriptors.TypeName(p)))}) : {Descriptors.TypeName(method.Proto.ReturnType)}";
            case DalvikIndex.Proto when index >= 0 && index < file.Protos.Count:
                var proto = file.Protos[index];
                return $"({string.Join(", ", proto.Parameters.Select(p => Descriptors.TypeName(p)))}) : {Descriptors.TypeName(proto.ReturnType)}";
            case DalvikIndex.CallSite when index >= 0 && index < file.CallSites.Count:
                var site = file.CallSites[index];
                string bootstrap = site.Bootstrap?.Method is { } handle ? $"{Descriptors.TypeName(handle.Owner)}.{handle.Name}" : "?";
                return $"call site {index}: {site.Name} via {bootstrap}";
            case DalvikIndex.MethodHandle when index >= 0 && index < file.MethodHandles.Count:
                var h = file.MethodHandles[index];
                return h.Method is { } m ? $"handle {Descriptors.TypeName(m.Owner)}.{m.Name}" : h.Field is { } f ? $"handle {Descriptors.TypeName(f.Owner)}.{f.Name}" : "handle ?";
            default:
                return $"{kind.ToString().ToLowerInvariant()}@{index} (past the file's table)";
        }
    }

    /// <summary>The names the debug table gives the registers an instruction uses, where it gives any.</summary>
    private static string Names(DalvikInstruction i, DexCode code)
    {
        if (code.Debug is not { Locals.Count: > 0 } debug)
        {
            return string.Empty;
        }

        IEnumerable<int> registers = i.Format switch
        {
            DalvikFormat.F35c or DalvikFormat.F3rc or DalvikFormat.F45cc or DalvikFormat.F4rcc => i.Registers,
            DalvikFormat.F12x or DalvikFormat.F22x or DalvikFormat.F32x or DalvikFormat.F22t or DalvikFormat.F22c or DalvikFormat.F22b or DalvikFormat.F22s => [i.A, i.B],
            DalvikFormat.F23x => [i.A, i.B, i.C],
            DalvikFormat.F11x or DalvikFormat.F11n or DalvikFormat.F21s or DalvikFormat.F21h or DalvikFormat.F31i or DalvikFormat.F51l
                or DalvikFormat.F21t or DalvikFormat.F21c or DalvikFormat.F31c or DalvikFormat.F31t => [i.A],
            _ => [],
        };
        var named = registers.Distinct()
            .Select(r => (Register: r, Local: debug.Locals.FirstOrDefault(l => l.Register == r && l.Start <= i.Address + i.Units && i.Address < l.End)))
            .Where(p => p.Local?.Name is not null)
            .Select(p => $"v{p.Register} {p.Local!.Name}");
        return string.Join(", ", named);
    }

    private static string Quote(string text)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in text)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' or (>= '\u007F' and <= '\u009F') or (>= '\uD800' and <= '\uDFFF') => $"\\u{(int)c:X4}",
                _ => c.ToString(),
            });
        }

        return sb.Append('"').ToString();
    }
}
