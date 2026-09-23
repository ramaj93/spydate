using System.Runtime.ExceptionServices;
using System.Text;
using Spydate.Core.Jvm;
using Spydate.Core.Project;
using Spydate.Decompiler.Native.IR;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// The in-house Java view: a class or one member as Java-shaped pseudo-code. Each method is lifted
/// (<see cref="JvmLifter"/>), tidied (<see cref="JavaInliner"/>), structured with its try regions
/// (<see cref="JavaRegions"/>) and printed (<see cref="JavaEmitter"/>) under the names the project gave its
/// classes and members.
///
/// The work runs on a thread of its own with a large stack. Every tree is bounded where it is built, and this is
/// the second guard: the structurer recurses once per nesting level of the method, and a crafted method nested
/// thousands deep must cost a slow answer, not the process. One method that fails any other way is printed as a
/// comment saying so, and the rest of the class still prints.
/// </summary>
internal static class JavaDecompiler
{
    private const int StackSize = 256 * 1024 * 1024;

    public static string Type(JvmReading reading, JvmType type, CancellationToken cancellationToken)
        => OnLargeStack(() => new JavaClassWriter(reading, type, cancellationToken).Class(), cancellationToken);

    public static string Member(JvmReading reading, JvmType type, JvmMember member, CancellationToken cancellationToken)
        => OnLargeStack(() => new JavaClassWriter(reading, type, cancellationToken).Member(member), cancellationToken);

    private static string OnLargeStack(Func<string> work, CancellationToken cancellationToken)
    {
        string? result = null;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    result = work();
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
            },
            StackSize)
        {
            IsBackground = true,
            Name = "Java decompiler",
        };

        thread.Start();
        thread.Join();
        failure?.Throw();
        cancellationToken.ThrowIfCancellationRequested();
        return result ?? string.Empty;
    }
}

/// <summary>A class, or one member of it, written as Java: declarations from the class file, bodies from the decompiler.</summary>
internal sealed partial class JavaClassWriter
{
    private const string Indent = "    ";

    private readonly JvmReading _reading;
    private readonly JvmType _type;
    private readonly ClassFile _file;
    private readonly JavaNaming _naming;
    private readonly CancellationToken _cancellationToken;
    private readonly StringBuilder _sb = new();

    public JavaClassWriter(JvmReading reading, JvmType type, CancellationToken cancellationToken)
    {
        _reading = reading;
        _type = type;
        _file = type.File;
        _cancellationToken = cancellationToken;
        _naming = new JavaNaming(_file, key => reading.Annotations?.Get(key)?.Name);
    }

    public string Class()
    {
        if (_file.PackageName.Length > 0)
        {
            _sb.Append("package ").Append(_file.PackageName.Replace('/', '.')).Append(";\n\n");
        }

        Annotation(_reading.AnnotationKey(_type, null), 0);
        _sb.Append(Declaration()).Append(" {\n");
        bool first = true;
        foreach (var member in _type.Members.Cast<JvmMember>())
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (member.Field is not null)
            {
                Field(member, 1);
                first = false;
            }
        }

        foreach (var member in _type.Members.Cast<JvmMember>())
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (member.Method is not null && !IsCompilerNoise(member.Method))
            {
                if (!first)
                {
                    _sb.Append('\n');
                }

                first = false;
                Method(member, 1);
            }
        }

        foreach (var nested in _type.NestedTypes.Cast<JvmType>())
        {
            _sb.Append('\n').Append(Indent).Append("// ").Append(nested.KindName).Append(' ').Append(nested.FullName).Append(" — opened on its own\n");
        }

        _sb.Append("}\n");
        return _sb.ToString();
    }

    public string Member(JvmMember member)
    {
        _sb.Append("// in ").Append(_type.KindName).Append(' ').Append(_type.FullName).Append('\n');
        if (member.Field is not null)
        {
            Field(member, 0);
        }
        else
        {
            Method(member, 0);
        }

        return _sb.ToString();
    }

    /// <summary>An enum's generated <c>values()</c> and <c>valueOf</c> say nothing its declaration does not.</summary>
    private bool IsCompilerNoise(JvmMethod method)
        => (_file.Access & JvmAccess.Enum) != 0 && method.Name is "values" or "valueOf" or "$values" && (method.Access & JvmAccess.Static) != 0;

    private string Declaration()
    {
        string keyword = JvmModifiers.ClassKeyword(_file.Access, _file.SuperName);
        string modifiers = JvmModifiers.Class(_type.DeclaredAccess, _type.Outer is not null);
        if (keyword is "enum" or "record")
        {
            modifiers = string.Join(' ', modifiers.Split(' ').Where(m => m is not ("final" or "abstract")));
        }

        var sb = new StringBuilder();
        if (modifiers.Length > 0)
        {
            sb.Append(modifiers).Append(' ');
        }

        sb.Append(keyword).Append(' ').Append(_naming.ClassName(_file.Name).Split('.')[^1]);
        var generic = _file.Signature is { } signature ? Descriptors.ClassSignature(signature, simple: true) : null;
        if (generic is { TypeParameters.Length: > 0 } g)
        {
            sb.Append(Nested(g.TypeParameters));
        }

        if (keyword == "record" && _file.RecordComponents is { } components)
        {
            sb.Append('(').Append(string.Join(", ", components.Select(c => $"{GenericOr(c.Signature, c.Descriptor)} {c.Name}"))).Append(')');
        }

        if (_file.SuperName is { } super && keyword == "class" && super != "java/lang/Object")
        {
            sb.Append(" extends ").Append(generic is { } withSuper ? Nested(withSuper.Super) : _naming.ClassName(super));
        }

        var interfaces = generic is { } withInterfaces ? withInterfaces.Interfaces.Select(Nested).ToList() : _file.Interfaces.Select(_naming.ClassName).ToList();
        if (keyword == "@interface")
        {
            interfaces = interfaces.Where(i => i is not ("Annotation" or "java.lang.annotation.Annotation")).ToList();
        }

        if (interfaces.Count > 0)
        {
            sb.Append(_file.IsInterface ? " extends " : " implements ").Append(string.Join(", ", interfaces));
        }

        return sb.ToString();
    }

    private void Field(JvmMember member, int level)
    {
        var field = member.Field!;
        Annotation(_reading.AnnotationKey(_type, member), level);
        Pad(level);
        string modifiers = JvmModifiers.Field(field.Access);
        if (modifiers.Length > 0)
        {
            _sb.Append(modifiers).Append(' ');
        }

        _sb.Append(GenericOr(field.Signature, field.Descriptor)).Append(' ').Append(_naming.FieldName(_file.Name, field.Name, field.Descriptor));
        if (field.ConstantValue != 0 && _file.Pool.Get(field.ConstantValue) is { } constant)
        {
            _sb.Append(" = ").Append(ConstantText(constant, field.Descriptor));
        }

        _sb.Append(";\n");
    }

    private void Method(JvmMember member, int level)
    {
        var method = member.Method!;
        Annotation(_reading.AnnotationKey(_type, member), level);
        Pad(level);
        _sb.Append(MethodHeader(method));
        if (method.Code is not { } code)
        {
            _sb.Append(";\n");
            return;
        }

        _sb.Append(" {\n");
        try
        {
            var lifted = JvmLifter.Lift(_file, method, code);
            JavaInliner.Run(lifted.Function);

            // Conditions are merged across blocks, but never across the edge of a try: its boundaries stay blocks.
            var pinned = code.Handlers.SelectMany(h => new[] { (ulong)h.StartPc, (ulong)h.EndPc, (ulong)h.HandlerPc }).ToHashSet();
            JavaConditions.Merge(lifted.Function, pinned);

            var returns = lifted.Function.Blocks
                .Where(b => b.Statements is [IrReturn { Value: null or JExpr { IsSimple: true } }])
                .ToDictionary(b => b.StartVa, b => (IrReturn)b.Statements[0]);
            var body = JavaRegions.Structure(lifted);
            var emitter = new JavaEmitter(_naming);
            emitter.Body(method, lifted, body, level, returns);
            _sb.Append(emitter.Output);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Pad(level + 1);
            _sb.Append("// this method could not be decompiled (").Append(ex.GetType().Name).Append(": ").Append(ex.Message.ReplaceLineEndings(" ")).Append("); its bytecode view shows it\n");
            Pad(level);
            _sb.Append("}\n");
        }
    }

    private string MethodHeader(JvmMethod method)
    {
        var sb = new StringBuilder();
        if (method.IsStaticInitializer)
        {
            return "static";
        }

        string modifiers = JvmModifiers.Method(method.Access);
        if (_file.IsInterface && (method.Access & (JvmAccess.Abstract | JvmAccess.Static)) == 0 && method.Code is not null)
        {
            modifiers = (modifiers + " default").Trim();
        }

        if (modifiers.Length > 0)
        {
            sb.Append(modifiers).Append(' ');
        }

        var parameterTypes = Descriptors.ParameterDescriptors(method.Descriptor);
        var generic = method.Signature is { } signature ? Descriptors.MethodSignature(signature, simple: true) : null;
        bool useGeneric = generic is { } g && g.Parameters.Count == parameterTypes.Count;
        if (generic is { TypeParameters.Length: > 0 } withTypes)
        {
            sb.Append(Nested(withTypes.TypeParameters)).Append(' ');
        }

        if (!method.IsConstructor)
        {
            sb.Append(generic is { } withReturn ? Nested(withReturn.Return) : _naming.Type(JCall.ReturnType(method.Descriptor))).Append(' ');
            sb.Append(_naming.MethodName(_file.Name, method.Name, method.Descriptor));
        }
        else
        {
            sb.Append(_naming.ClassName(_file.Name).Split('.')[^1]);
        }

        // The same names the body uses, so a parameter reads the same in the signature and the code.
        var names = method.Code is { } code ? new LocalNamer(_file, method, code).Parameters.Select(p => p.Name).ToList() : null;
        bool varargs = (method.Access & JvmAccess.TransientOrVarargs) != 0;
        sb.Append('(');
        for (int i = 0; i < parameterTypes.Count; i++)
        {
            string type = useGeneric ? Nested(generic!.Value.Parameters[i]) : _naming.Type(parameterTypes[i]);
            if (varargs && i == parameterTypes.Count - 1 && type.EndsWith("[]", StringComparison.Ordinal))
            {
                type = type[..^2] + "...";
            }

            sb.Append(i > 0 ? ", " : string.Empty).Append(type).Append(' ').Append(names is not null && i < names.Count ? names[i] : $"arg{i}");
        }

        sb.Append(')');
        var thrown = generic is { Throws.Count: > 0 } withThrows ? withThrows.Throws.Select(Nested).ToList() : method.Exceptions.Select(_naming.ClassName).ToList();
        if (thrown.Count > 0)
        {
            sb.Append(" throws ").Append(string.Join(", ", thrown));
        }

        return sb.ToString();
    }

    /// <summary>
    /// A generic signature's text with nested classes as Java writes them — <c>Map.Entry</c>, not <c>Map$Entry</c>.
    /// Only a <c>$</c> between a name and a capital is a nesting; <c>lambda$main$0</c> and <c>Outer$1</c> stay.
    /// </summary>
    private static string Nested(string text) => NestingDollar().Replace(text, ".");

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<=[A-Za-z0-9_])\$(?=[A-Z])")]
    private static partial System.Text.RegularExpressions.Regex NestingDollar();

    private string GenericOr(string? signature, string descriptor)
        => signature is not null && Descriptors.FieldSignature(signature, simple: true) is { } generic ? Nested(generic) : _naming.Type(descriptor);

    private string ConstantText(Constant constant, string descriptor) => constant.Tag switch
    {
        ConstantTag.String => JavaEmitter.Quote(_file.Pool.Utf8(constant.A) ?? string.Empty),
        ConstantTag.Integer when descriptor == "Z" => constant.Bits == 0 ? "false" : "true",
        ConstantTag.Integer when descriptor == "C" => $"'\\u{(int)constant.Bits & 0xFFFF:X4}'",
        ConstantTag.Integer => ((int)constant.Bits).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ConstantTag.Long => constant.Bits.ToString(System.Globalization.CultureInfo.InvariantCulture) + "L",
        ConstantTag.Float => BitConverter.Int32BitsToSingle((int)constant.Bits).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f",
        ConstantTag.Double => BitConverter.Int64BitsToDouble(constant.Bits).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => "/* constant */",
    };

    private void Annotation(string? key, int level)
    {
        if (key is null || _reading.Annotations?.Get(key) is not { } annotation)
        {
            return;
        }

        string by = annotation.Source == AnnotationSource.Agent ? " (agent)" : string.Empty;
        if (annotation.Name is not null)
        {
            Pad(level);
            _sb.Append("// renamed: ").Append(annotation.Name).Append(by).Append('\n');
        }

        if (annotation.Comment is { } comment)
        {
            Pad(level);
            _sb.Append("// ").Append(comment).Append(annotation.Name is null ? by : string.Empty).Append('\n');
        }
    }

    private void Pad(int level)
    {
        for (int i = 0; i < level; i++)
        {
            _sb.Append(Indent);
        }
    }
}
