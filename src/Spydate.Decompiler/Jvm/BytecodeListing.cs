using System.Globalization;
using System.Text;
using Spydate.Core.Jvm;
using Spydate.Core.Project;

namespace Spydate.Decompiler.Jvm;

/// <summary>
/// A class or one member as bytecode, shaped like <c>javap -c -p</c> — declaration, then each method's code with
/// its exception table and locals — but with names a reader need not decode: <c>java.lang.String</c> rather than
/// <c>Ljava/lang/String;</c>, a constant pool reference resolved to what it names, a local slot followed by the
/// variable's name when the class kept one. Offsets are four hex digits, and a branch names the offset it goes to.
/// </summary>
internal sealed class BytecodeListing
{
    private const int MaxLiteral = 400;
    private readonly JvmReading _reading;
    private readonly StringBuilder _sb = new();
    private JvmType? _type;
    private ConstantPool? _pool;

    public BytecodeListing(JvmReading reading) => _reading = reading;

    public string Type(JvmType type, CancellationToken cancellationToken)
    {
        Use(type);
        Header(type);
        _sb.Append("{\n");
        bool first = true;
        foreach (var member in type.Members.Cast<JvmMember>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!first)
            {
                _sb.Append('\n');
            }

            first = false;
            Member(type, member, "  ");
        }

        if (type.NestedTypes.Count > 0)
        {
            _sb.Append('\n');
            foreach (var nested in type.NestedTypes.Cast<JvmType>())
            {
                _sb.Append(CultureInfo.InvariantCulture, $"  // nested {nested.KindName} {nested.FullName}\n");
            }
        }

        _sb.Append("}\n");
        return _sb.ToString();
    }

    public string Member(JvmType type, JvmMember member, CancellationToken cancellationToken)
    {
        Use(type);
        cancellationToken.ThrowIfCancellationRequested();
        _sb.Append(CultureInfo.InvariantCulture, $"// in {type.KindName} {type.FullName}\n");
        Member(type, member, string.Empty);
        return _sb.ToString();
    }

    private void Use(JvmType type)
    {
        _type = type;
        _pool = type.File.Pool;
    }

    private void Header(JvmType type)
    {
        var file = type.File;
        // A class translated from DEX says which DEX file holds it; a class file its version.
        var facts = _reading.Apk is null
            ? new List<string> { type.Class.Entry.Name, file.JavaVersion, $"class file {file.MajorVersion}.{file.MinorVersion}" }
            : new List<string> { type.Class.Entry.Name, _reading.FormatVersion };
        if (file.SourceFile is { } source)
        {
            facts.Add($"from {source}");
        }

        _sb.Append(CultureInfo.InvariantCulture, $"// {type.FullName}  ({string.Join(", ", facts)})\n");
        Annotation(_reading.AnnotationKey(type, null), string.Empty);

        string modifiers = JvmModifiers.Class(type.DeclaredAccess, type.Outer is not null);
        string keyword = JvmModifiers.ClassKeyword(file.Access, file.SuperName);
        var declaration = new StringBuilder();
        if (file.IsDeprecated)
        {
            declaration.Append("@Deprecated ");
        }

        declaration.Append(modifiers.Length > 0 ? modifiers + " " : string.Empty).Append(keyword).Append(' ').Append(type.FullName);

        var generic = file.Signature is { } signature ? Descriptors.ClassSignature(signature) : null;
        if (generic is { } g)
        {
            declaration.Append(g.TypeParameters);
        }

        if (file.SuperName is { } super && super != "java/lang/Object" && keyword is "class")
        {
            declaration.Append(" extends ").Append(generic?.Super ?? Descriptors.ClassName(super));
        }

        var interfaces = generic?.Interfaces ?? file.Interfaces.Select(i => Descriptors.ClassName(i)).ToList();
        if (keyword == "@interface")
        {
            interfaces = interfaces.Where(i => i != "java.lang.annotation.Annotation").ToList();
        }

        if (interfaces.Count > 0)
        {
            declaration.Append(file.IsInterface ? " extends " : " implements ").Append(string.Join(", ", interfaces));
        }

        _sb.Append(declaration).Append('\n');

        if (keyword is "enum" or "record" && file.SuperName is { } implied && implied != "java/lang/Object")
        {
            _sb.Append(CultureInfo.InvariantCulture, $"  // extends {Descriptors.ClassName(implied)}\n");
        }

        if (file.RecordComponents is { Count: > 0 } components)
        {
            _sb.Append(CultureInfo.InvariantCulture, $"  // components ({string.Join(", ", components.Select(c => $"{Type(c.Descriptor, c.Signature)} {c.Name}"))})\n");
        }

        if (file.PermittedSubclasses.Count > 0)
        {
            _sb.Append(CultureInfo.InvariantCulture, $"  // permits {string.Join(", ", file.PermittedSubclasses.Select(p => Descriptors.ClassName(p)))}\n");
        }

        if (file.EnclosingClass is { } enclosing)
        {
            string where = file.EnclosingMethod is { } method ? $"{Descriptors.ClassName(enclosing)}.{method.Name}{Params(method.Descriptor)}" : Descriptors.ClassName(enclosing);
            _sb.Append(CultureInfo.InvariantCulture, $"  // declared in {where}\n");
        }

        if (file.NestHost is { } host)
        {
            _sb.Append(CultureInfo.InvariantCulture, $"  // nest host {Descriptors.ClassName(host)}\n");
        }

        foreach (string warning in file.Warnings)
        {
            _sb.Append(CultureInfo.InvariantCulture, $"  // warning: {warning}\n");
        }
    }

    private void Member(JvmType type, JvmMember member, string indent)
    {
        Annotation(_reading.AnnotationKey(type, member), indent);
        if (member.Field is { } field)
        {
            Field(field, indent);
        }
        else if (member.Method is { } method)
        {
            Method(type, method, indent);
        }
    }

    private void Field(JvmField field, string indent)
    {
        string modifiers = JvmModifiers.Field(field.Access);
        _sb.Append(indent)
            .Append(modifiers.Length > 0 ? modifiers + " " : string.Empty)
            .Append(Type(field.Descriptor, field.Signature))
            .Append(' ')
            .Append(field.Name);

        if (field.ConstantValue != 0)
        {
            _sb.Append(" = ").Append(Constant(field.ConstantValue));
        }

        _sb.Append(";\n");
        var notes = JvmModifiers.Notes(field.Access, isMethod: false);
        _sb.Append(indent).Append("  descriptor ").Append(field.Descriptor);
        if (notes.Count > 0)
        {
            _sb.Append("  (").Append(string.Join(", ", notes)).Append(')');
        }

        _sb.Append('\n');
    }

    private void Method(JvmType type, JvmMethod method, string indent)
    {
        string modifiers = JvmModifiers.Method(method.Access);
        var parsed = Descriptors.Method(method.Descriptor);
        var generic = method.Signature is { } signature ? Descriptors.MethodSignature(signature) : null;

        // A generic signature can leave out parameters the descriptor has (an inner class's outer instance, an
        // enum constructor's name and ordinal), so it is only trusted for the types when the counts agree.
        var parameterTypes = generic is { } g && parsed is { } p && g.Parameters.Count == p.Parameters.Count
            ? g.Parameters
            : parsed?.Parameters ?? [];
        string returns = generic?.Return ?? parsed?.Return ?? "?";
        var names = ParameterNames(method, parameterTypes.Count);
        bool varargs = (method.Access & JvmAccess.TransientOrVarargs) != 0;

        var line = new StringBuilder(indent);
        if (modifiers.Length > 0)
        {
            line.Append(modifiers).Append(' ');
        }

        if (generic is { TypeParameters.Length: > 0 } withTypes)
        {
            line.Append(withTypes.TypeParameters).Append(' ');
        }

        if (method.IsStaticInitializer)
        {
            line.Append("static {}");
        }
        else
        {
            if (!method.IsConstructor)
            {
                line.Append(returns).Append(' ');
            }

            line.Append(method.IsConstructor ? type.Name : method.Name).Append('(');
            for (int i = 0; i < parameterTypes.Count; i++)
            {
                string parameterType = parameterTypes[i];
                if (varargs && i == parameterTypes.Count - 1 && parameterType.EndsWith("[]", StringComparison.Ordinal))
                {
                    parameterType = parameterType[..^2] + "...";
                }

                line.Append(i > 0 ? ", " : string.Empty).Append(parameterType).Append(' ').Append(names[i]);
            }

            line.Append(')');
        }

        var thrown = generic is { Throws.Count: > 0 } withThrows ? withThrows.Throws : method.Exceptions.Select(e => Descriptors.ClassName(e)).ToList();
        if (thrown.Count > 0)
        {
            line.Append(" throws ").Append(string.Join(", ", thrown));
        }

        _sb.Append(line).Append(method.Code is null ? ";\n" : "\n");

        var notes = JvmModifiers.Notes(method.Access, isMethod: true);
        _sb.Append(indent).Append("  descriptor ").Append(method.Descriptor);
        if (notes.Count > 0)
        {
            _sb.Append("  (").Append(string.Join(", ", notes)).Append(')');
        }

        _sb.Append('\n');
        if (method.Dalvik is { } dalvik)
        {
            DalvikListing.Code(_sb, method, dalvik, indent + "  ");
        }
        else if (method.Code is { } code)
        {
            Code(code, indent + "  ");
        }
    }

    /// <summary>
    /// Parameter names: from <c>MethodParameters</c> when the compiler kept them, else from the local variable
    /// table's slots at offset zero, else <c>arg0</c>, <c>arg1</c>.
    /// </summary>
    private static string[] ParameterNames(JvmMethod method, int count)
    {
        var names = new string[count];
        int slot = (method.Access & JvmAccess.Static) != 0 ? 0 : 1;
        var slotTypes = Descriptors.Method(method.Descriptor)?.Parameters ?? [];
        for (int i = 0; i < count; i++)
        {
            string? name = i < method.ParameterNames.Count ? method.ParameterNames[i] : null;
            name ??= method.Code?.Locals.FirstOrDefault(l => l.Slot == slot && l.StartPc == 0)?.Name;
            names[i] = string.IsNullOrEmpty(name) ? $"arg{i}" : name;
            slot += i < slotTypes.Count && slotTypes[i] is "long" or "double" ? 2 : 1;
        }

        return names;
    }

    private void Code(CodeAttribute code, string indent)
    {
        _sb.Append(indent).Append(CultureInfo.InvariantCulture, $"stack {code.MaxStack}, locals {code.MaxLocals}, {code.Code.Length} bytes\n");
        var instructions = Bytecode.Decode(code.Code.Span);
        int line = 0;
        foreach (var instruction in instructions)
        {
            while (line < code.Lines.Count && code.Lines[line].StartPc <= instruction.Offset)
            {
                if (code.Lines[line].StartPc == instruction.Offset)
                {
                    _sb.Append(indent).Append(CultureInfo.InvariantCulture, $"// line {code.Lines[line].Line}\n");
                }

                line++;
            }

            Instruction(code, instruction, indent);
        }

        if (code.Handlers.Count > 0)
        {
            _sb.Append(indent).Append("exception table\n");
            foreach (var handler in code.Handlers)
            {
                _sb.Append(indent).Append(CultureInfo.InvariantCulture,
                    $"  {handler.StartPc:X4}-{handler.EndPc:X4} -> {handler.HandlerPc:X4}  {(handler.CatchType is { } caught ? Descriptors.ClassName(caught) : "any")}\n");
            }
        }

        if (code.Locals.Count > 0)
        {
            _sb.Append(indent).Append("locals\n");
            foreach (var local in code.Locals.OrderBy(l => l.Slot).ThenBy(l => l.StartPc))
            {
                _sb.Append(indent).Append(CultureInfo.InvariantCulture,
                    $"  {local.Slot,3}  {local.Name,-16} {Type(local.Descriptor, local.Signature),-32} {local.StartPc:X4}-{local.StartPc + local.Length:X4}\n");
            }
        }
    }

    private void Instruction(CodeAttribute code, JvmInstruction instruction, string indent)
    {
        _sb.Append(indent).Append(CultureInfo.InvariantCulture, $"{instruction.Offset:X4}  ");
        if (instruction.Problem is { } problem)
        {
            _sb.Append(CultureInfo.InvariantCulture, $"?? {instruction.Mnemonic}  // {problem}; the remaining {instruction.Length} byte(s) are not decoded\n");
            return;
        }

        string mnemonic = instruction.IsWide ? $"wide {instruction.Mnemonic}" : instruction.Mnemonic;
        string? operands = Operands(code, instruction, indent, out string? comment);
        _sb.Append(operands is null ? mnemonic : $"{mnemonic,-15} {operands}");
        if (comment is not null)
        {
            _sb.Append("  // ").Append(comment);
        }

        _sb.Append('\n');
        if (instruction.Cases is { } cases)
        {
            foreach (var (match, target) in cases)
            {
                _sb.Append(indent).Append(CultureInfo.InvariantCulture, $"        {match,11}: {target:X4}\n");
            }

            _sb.Append(indent).Append(CultureInfo.InvariantCulture, $"        {"default",11}: {instruction.Default:X4}\n");
        }
    }

    private string? Operands(CodeAttribute code, JvmInstruction instruction, string indent, out string? comment)
    {
        comment = null;
        switch (instruction.Kind)
        {
            case OperandKind.SignedByte:
            case OperandKind.SignedShort:
                return instruction.Operand.ToString(CultureInfo.InvariantCulture);
            case OperandKind.Local:
                comment = LocalName(code, instruction.Operand, instruction.Offset + instruction.Length, instruction.Offset);
                return instruction.Operand.ToString(CultureInfo.InvariantCulture);
            case OperandKind.Increment:
                comment = LocalName(code, instruction.Operand, instruction.Offset, instruction.Offset);
                return string.Create(CultureInfo.InvariantCulture, $"{instruction.Operand}, {instruction.Operand2}");
            case OperandKind.Branch:
            case OperandKind.WideBranch:
                return instruction.Operand.ToString("X4", CultureInfo.InvariantCulture);
            case OperandKind.ArrayType:
                return Bytecode.ArrayTypeName(instruction.Operand);
            case OperandKind.PoolByte:
                return Constant(instruction.Operand);
            case OperandKind.Pool:
            case OperandKind.Interface:
                return PoolReference(instruction.Operand);
            case OperandKind.MultiArray:
                return string.Create(CultureInfo.InvariantCulture, $"{PoolReference(instruction.Operand)} {instruction.Operand2}");
            case OperandKind.Dynamic:
                return Dynamic(instruction.Operand, out comment);
            case OperandKind.TableSwitch:
            case OperandKind.LookupSwitch:
                return string.Create(CultureInfo.InvariantCulture, $"{instruction.Cases?.Count ?? 0} case(s)");
            default:
                // An implicit slot — iload_1, astore_3 — is still worth naming.
                if (ImplicitSlot(instruction.Opcode) is { } slot)
                {
                    bool store = instruction.Opcode >= 0x3B;
                    comment = LocalName(code, slot, store ? instruction.Offset + instruction.Length : instruction.Offset, instruction.Offset);
                }

                return null;
        }
    }

    /// <summary>The slot an <c>xload_n</c> or <c>xstore_n</c> names in its opcode.</summary>
    private static int? ImplicitSlot(byte opcode) => opcode switch
    {
        >= 0x1A and <= 0x2D => (opcode - 0x1A) % 4,
        >= 0x3B and <= 0x4E => (opcode - 0x3B) % 4,
        _ => null,
    };

    /// <summary>
    /// The name of the variable in a slot at a point in the code. A store's variable begins just after it, so
    /// the caller passes the offset after a store and the offset of a load; either end of the range is accepted.
    /// </summary>
    private static string? LocalName(CodeAttribute code, int slot, int at, int fallback)
    {
        foreach (int pc in (ReadOnlySpan<int>)[at, fallback])
        {
            foreach (var local in code.Locals)
            {
                if (local.Slot == slot && pc >= local.StartPc && pc <= local.StartPc + local.Length)
                {
                    return local.Name;
                }
            }
        }

        return null;
    }

    /// <summary>A pool reference as what it names: a class, a field with its type, a method with its signature.</summary>
    private string PoolReference(int index)
    {
        var pool = _pool!;
        if (pool.Member(index) is { } member)
        {
            return MemberText(member);
        }

        if (pool.ClassName(index) is { } className)
        {
            return Descriptors.ClassName(className);
        }

        return Constant(index);
    }

    /// <summary><c>java.io.PrintStream.println(java.lang.String) : void</c>, or <c>java.lang.System.out : java.io.PrintStream</c>.</summary>
    private static string MemberText(MemberReference member)
    {
        string owner = Descriptors.ClassName(member.Owner);
        return Descriptors.Method(member.Descriptor) is { } method
            ? $"{owner}.{member.Name}({string.Join(", ", method.Parameters)}) : {method.Return}"
            : $"{owner}.{member.Name} : {Descriptors.TypeName(member.Descriptor)}";
    }

    private string Dynamic(int index, out string? comment)
    {
        comment = null;
        var pool = _pool!;
        if (pool.Get(index) is not { Tag: ConstantTag.InvokeDynamic or ConstantTag.Dynamic } constant || pool.NameAndType(constant.B) is not { } nat)
        {
            return $"#{index}?";
        }

        comment = Bootstrap(constant.A);
        return Descriptors.Method(nat.Descriptor) is { } method
            ? $"{nat.Name}({string.Join(", ", method.Parameters)}) : {method.Return}"
            : $"{nat.Name} : {Descriptors.TypeName(nat.Descriptor)}";
    }

    /// <summary>A bootstrap method and its static arguments, which say what an invokedynamic really does.</summary>
    private string? Bootstrap(int index)
    {
        var methods = _type!.File.BootstrapMethods;
        if (index >= methods.Count)
        {
            return $"bootstrap #{index} is missing";
        }

        var bootstrap = methods[index];
        string handle = Handle(bootstrap.MethodHandle);
        return bootstrap.Arguments.Count == 0
            ? $"bootstrap {handle}"
            : $"bootstrap {handle}, args {string.Join(", ", bootstrap.Arguments.Select(Constant))}";
    }

    private string Handle(int index)
    {
        var pool = _pool!;
        if (pool.Get(index) is not { Tag: ConstantTag.MethodHandle } handle || pool.Member(handle.B) is not { } target)
        {
            return $"#{index}?";
        }

        return $"{Descriptors.ClassName(target.Owner)}.{target.Name}";
    }

    /// <summary>A loadable constant as a Java literal: a quoted string, <c>5L</c>, <c>1.5f</c>, <c>String.class</c>.</summary>
    private string Constant(int index)
    {
        // A dynamic constant's bootstrap arguments are constants, which may be dynamic constants in turn; a crafted
        // pool can make that a cycle. A real one is never more than a couple deep.
        if (_depth >= MaxConstantDepth)
        {
            return $"#{index} (nested too deep)";
        }

        _depth++;
        try
        {
            return ConstantText(index);
        }
        finally
        {
            _depth--;
        }
    }

    private const int MaxConstantDepth = 8;

    private int _depth;

    private string ConstantText(int index)
    {
        var pool = _pool!;
        if (pool.Get(index) is not { } constant)
        {
            return $"#{index}?";
        }

        return constant.Tag switch
        {
            ConstantTag.String => Quote(pool.Utf8(constant.A) ?? "?"),
            ConstantTag.Integer => ((int)constant.Bits).ToString(CultureInfo.InvariantCulture),
            ConstantTag.Float => BitConverter.Int32BitsToSingle((int)constant.Bits).ToString("R", CultureInfo.InvariantCulture) + "f",
            ConstantTag.Long => constant.Bits.ToString(CultureInfo.InvariantCulture) + "L",
            ConstantTag.Double => BitConverter.Int64BitsToDouble(constant.Bits).ToString("R", CultureInfo.InvariantCulture) + "d",
            ConstantTag.Class => (pool.Utf8(constant.A) is { } name ? Descriptors.ClassName(name) : "?") + ".class",
            ConstantTag.MethodType => $"(method type {pool.Utf8(constant.A)})",
            ConstantTag.MethodHandle => $"(handle {Handle(index)})",
            ConstantTag.Dynamic => $"(dynamic {Dynamic(index, out _)})",
            ConstantTag.Utf8 => Quote(constant.Text ?? string.Empty),
            // A field or method ref, or a structural entry no instruction should load. Never back into
            // PoolReference, which falls back to here: a crafted pool would loop the two until the stack gave out.
            _ => pool.Member(index) is { } member ? MemberText(member) : $"#{index} ({constant.Tag})",
        };
    }

    private static string Quote(string text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, MaxLiteral) + 2);
        sb.Append('"');
        foreach (char c in text.Length > MaxLiteral ? text[..MaxLiteral] : text)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' or (>= '\u007F' and < ' ') => $"\\u{(int)c:X4}",
                _ => c.ToString(),
            });
        }

        sb.Append('"');
        if (text.Length > MaxLiteral)
        {
            sb.Append(CultureInfo.InvariantCulture, $"... ({text.Length:N0} chars)");
        }

        return sb.ToString();
    }

    /// <summary>A field or local's type, from its generic signature when that parses, else from its descriptor.</summary>
    private static string Type(string descriptor, string? signature)
        => signature is not null && Descriptors.FieldSignature(signature) is { } generic ? generic : Descriptors.TypeName(descriptor);

    private static string Params(string descriptor)
        => Descriptors.Method(descriptor) is { } method ? $"({string.Join(", ", method.Parameters)})" : descriptor;

    private void Annotation(string? key, string indent)
    {
        if (key is null || _reading.Annotations?.Get(key) is not { } annotation)
        {
            return;
        }

        string by = annotation.Source == AnnotationSource.Agent ? " (agent)" : string.Empty;
        if (annotation.Name is { } name)
        {
            _sb.Append(indent).Append(CultureInfo.InvariantCulture, $"// renamed: {name}{by}\n");
        }

        if (annotation.Comment is { } comment)
        {
            _sb.Append(indent).Append(CultureInfo.InvariantCulture, $"// {comment}{(annotation.Name is null ? by : string.Empty)}\n");
        }
    }
}
