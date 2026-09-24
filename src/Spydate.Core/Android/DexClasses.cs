using Spydate.Core.Dex;
using Spydate.Core.Jvm;

namespace Spydate.Core.Android;

/// <summary>
/// A DEX class as a Java class file, so everything that reads classes — the tree, the Java view, cross-references,
/// annotations keyed by member — reads an APK's classes too.
///
/// DEX keeps in system annotations what a class file keeps in attributes: <c>dalvik.annotation.Signature</c> is the
/// generic signature, <c>InnerClass</c> with <c>EnclosingClass</c> or <c>EnclosingMethod</c> the nesting,
/// <c>Throws</c> the declared exceptions, <c>AnnotationDefault</c> an annotation interface's defaults,
/// <c>MethodParameters</c> the parameter names. They go back where a class file has them, and only the source's own
/// annotations stay annotations. A static field's value from the class's static values becomes its constant; each
/// method keeps its Dalvik code, with its try ranges and debug tables in <see cref="CodeAttribute"/> by code-unit
/// address and register.
/// </summary>
public static class DexClasses
{
    private const string System = "Ldalvik/annotation/";

    public static ClassFile ToClassFile(DexClass definition)
    {
        var constants = new List<Constant>();
        var ownAnnotations = definition.Annotations;
        string? signature = SignatureOf(ownAnnotations);

        // The class's own row: a member class names its outer class, a local or anonymous one its enclosing method.
        var inner = new List<InnerClassEntry>();
        string? enclosingClass = null;
        (string, string)? enclosingMethod = null;
        if (Find(ownAnnotations, "InnerClass") is { } innerClass)
        {
            var flags = (JvmAccess)(ushort)(innerClass["accessFlags"]?.Value is long f ? f : 0);
            string? simpleName = innerClass["name"]?.Value as string;
            string? outer = Find(ownAnnotations, "EnclosingClass")?["value"]?.Value is string outerType ? DexFile.InternalName(outerType) : null;
            if (Find(ownAnnotations, "EnclosingMethod")?["value"]?.Value is DexMethodRef method)
            {
                enclosingClass = DexFile.InternalName(method.Owner);
                enclosingMethod = (method.Name, method.Proto.Descriptor);
            }
            else if (outer is not null && simpleName is null)
            {
                enclosingClass = outer;
            }

            inner.Add(new InnerClassEntry(definition.Name, simpleName is null ? null : outer, simpleName, flags));
        }

        var defaults = Find(ownAnnotations, "AnnotationDefault")?["value"]?.Value as DexAnnotation;
        var fields = Ordered(definition).Select(f => Field(f, constants)).ToList();
        var methods = definition.Methods.Select(m => Method(m, defaults)).ToList();

        // D8 desugars a record into a class extending its RecordTag, with equals, hashCode and toString written out
        // over private $record$ helpers. It is the record again: its instance fields its components, and the methods
        // it wrote gone, since Java writes them itself.
        string? super = definition.Super is null ? null : DexFile.InternalName(definition.Super);
        IReadOnlyList<RecordComponent>? components = null;
        if (super == "com/android/tools/r8/RecordTag")
        {
            super = "java/lang/Record";
            components = definition.InstanceFields.Select(f => new RecordComponent(f.Ref.Name, f.Ref.Type, SignatureOf(f.Annotations))).ToList();
            var helpers = definition.Methods.Where(m => m.Ref.Name.StartsWith("$record$", StringComparison.Ordinal)).Select(m => m.Ref.Name).ToHashSet(StringComparer.Ordinal);
            methods = methods.Where(m => !helpers.Contains(m.Name)
                && !(m.Name is "equals" or "hashCode" or "toString" && ((m.Access & JvmAccess.Final) != 0 || CallsAny(definition, m, helpers)))).ToList();
        }
        else if (super == "java/lang/Record")
        {
            // Not desugared: a record still, but DEX has no Record attribute. Its instance fields are its components,
            // and the equals, hashCode and toString javac left to ObjectMethods are Java's own again.
            components = definition.InstanceFields.Select(f => new RecordComponent(f.Ref.Name, f.Ref.Type, SignatureOf(f.Annotations))).ToList();
            methods = methods.Where(m => !(m.Name is "equals" or "hashCode" or "toString" && CallsObjectMethods(definition, m))).ToList();
        }

        return ClassFile.Create(
            definition.Name,
            (JvmAccess)(ushort)definition.Access,
            super,
            definition.Interfaces.Select(DexFile.InternalName).ToList(),
            fields,
            methods,
            ConstantPool.Of(constants),
            string.IsNullOrEmpty(definition.SourceFile) ? null : definition.SourceFile, // R8 empties it when it minifies
            signature,
            inner,
            enclosingClass,
            enclosingMethod,
            Annotations(ownAnnotations),
            recordComponents: components);
    }

    /// <summary>
    /// The fields in the order the source declared them, as near as the code shows it: DEX sorts a class's fields by
    /// name, where a class file keeps them as written. Static fields go in the order the static initialiser first
    /// stores them, instance fields in the order the first constructor does; the rest keep their places after.
    /// </summary>
    private static List<DexField> Ordered(DexClass definition)
    {
        List<DexField> ByStores(IReadOnlyList<DexField> fields, string method, bool isStatic)
        {
            var code = definition.DirectMethods.FirstOrDefault(m => m.Ref.Name == method)?.Code;
            if (code?.File is not { } file || fields.Count < 2)
            {
                return [.. fields];
            }

            var order = new List<string>();
            foreach (var i in Dalvik.Decode(code.Insns.Span))
            {
                bool store = isStatic ? i.Opcode is >= 0x67 and <= 0x6D : i.Opcode is >= 0x59 and <= 0x5F;
                if (store && i.Index >= 0 && i.Index < file.FieldRefs.Count && file.FieldRefs[i.Index] is { } field
                    && field.Owner == definition.Descriptor && !order.Contains(field.Name))
                {
                    order.Add(field.Name);
                }
            }

            return fields.Select((f, index) => (f, index))
                .OrderBy(p => order.IndexOf(p.f.Ref.Name) is var at and >= 0 ? at : order.Count + p.index)
                .Select(p => p.f)
                .ToList();
        }

        return [.. ByStores(definition.StaticFields, "<clinit>", true), .. ByStores(definition.InstanceFields, "<init>", false)];
    }

    /// <summary>Whether a method's code is a call site bootstrapped by <c>ObjectMethods</c>, as a record's generated methods are.</summary>
    private static bool CallsObjectMethods(DexClass definition, JvmMethod method)
    {
        var code = definition.Methods.FirstOrDefault(m => m.Ref.Name == method.Name && m.Ref.Proto.Descriptor == method.Descriptor)?.Code;
        return code?.File is { } file && Dalvik.Decode(code.Insns.Span).Any(i => i.IndexKind == DalvikIndex.CallSite && i.Index >= 0
            && i.Index < file.CallSites.Count && file.CallSites[i.Index].Bootstrap?.Method?.Owner == "Ljava/lang/runtime/ObjectMethods;");
    }

    /// <summary>Whether a method's code calls one of the named methods of its own class.</summary>
    private static bool CallsAny(DexClass definition, JvmMethod method, IReadOnlySet<string> names)
    {
        var code = definition.Methods.FirstOrDefault(m => m.Ref.Name == method.Name && m.Ref.Proto.Descriptor == method.Descriptor)?.Code;
        if (code?.File is not { } file)
        {
            return false;
        }

        return Dalvik.Decode(code.Insns.Span).Any(i => i.IndexKind == DalvikIndex.Method && i.Index >= 0 && i.Index < file.MethodRefs.Count
                                                       && file.MethodRefs[i.Index].Owner == definition.Descriptor && names.Contains(file.MethodRefs[i.Index].Name));
    }

    private static JvmField Field(DexField field, List<Constant> constants)
    {
        int constant = 0;

        // A default value (zero, false, null) is what a field starts with anyway: only another one is written.
        if ((field.Access & DexAccess.Static) != 0 && field.StaticValue is { } value && Constant(value, field.Ref.Type) is { } entry && !IsDefault(value))
        {
            // A string constant is a String entry naming a Utf8 one, as in a class file.
            if (entry.Tag == ConstantTag.String)
            {
                constants.Add(new Constant(ConstantTag.Utf8, 0, 0, 0, entry.Text));
                entry = entry with { A = (ushort)constants.Count, Text = null };
            }

            constants.Add(entry);
            constant = constants.Count;
            if (entry.Tag is ConstantTag.Long or ConstantTag.Double)
            {
                constants.Add(default);
            }
        }

        return new JvmField((JvmAccess)(ushort)field.Access, field.Ref.Name, field.Ref.Type)
        {
            Signature = SignatureOf(field.Annotations),
            ConstantValue = constant,
            Annotations = Annotations(field.Annotations),
        };
    }

    private static JvmMethod Method(DexMethod method, DexAnnotation? defaults)
    {
        var access = (JvmAccess)(ushort)method.Access;
        if ((method.Access & DexAccess.DeclaredSynchronized) != 0)
        {
            access |= JvmAccess.SuperOrSynchronized;
        }

        var annotations = method.Annotations;
        var exceptions = Find(annotations, "Throws")?["value"]?.Value is IReadOnlyList<DexValue> thrown
            ? thrown.Select(t => t.Value as string).OfType<string>().Select(DexFile.InternalName).ToList()
            : [];
        IReadOnlyList<string?> names = method.Code?.Debug?.ParameterNames ?? [];
        if (Find(annotations, "MethodParameters")?["names"]?.Value is IReadOnlyList<DexValue> parameterNames)
        {
            names = parameterNames.Select(v => v.Value as string).ToList();
        }

        JvmElementValue? annotationDefault = defaults?[method.Ref.Name] is { } value ? Element(value) : null;
        return new JvmMethod(access, method.Ref.Name, method.Ref.Proto.Descriptor)
        {
            Signature = SignatureOf(annotations),
            Code = method.Code is { } code ? Code(code) : null,
            Dalvik = method.Code,
            Exceptions = exceptions,
            ParameterNames = names,
            Annotations = Annotations(annotations),
            ParameterAnnotations = method.ParameterAnnotations.Select(p => (IReadOnlyList<JvmAnnotation>)Annotations(p)).ToList(),
            AnnotationDefault = annotationDefault,
        };
    }

    /// <summary>What a class file's Code attribute says about a method, from its Dalvik code: ranges, lines, locals.</summary>
    private static CodeAttribute Code(DexCode code)
    {
        var handlers = new List<ExceptionHandler>();
        foreach (var attempt in code.Tries)
        {
            int end = attempt.Start + attempt.Length;
            handlers.AddRange(attempt.Handlers.Select(h => new ExceptionHandler(attempt.Start, end, h.Address, DexFile.InternalName(h.Type))));
            if (attempt.CatchAll is { } all)
            {
                handlers.Add(new ExceptionHandler(attempt.Start, end, all, null));
            }
        }

        var debug = code.Debug;
        return new CodeAttribute(
            0,
            code.Registers,
            ReadOnlyMemory<byte>.Empty,
            handlers,
            debug?.Lines.Select(l => new LineNumber(l.Address, l.Line)).ToList() ?? [],
            debug?.Locals.Where(l => l.Name is not null && l.Type is not null)
                .Select(l => new LocalVariable(l.Start, l.End - l.Start, l.Name!, l.Type!, l.Register) { Signature = l.Signature })
                .ToList() ?? []);
    }

    /// <summary>The source's own annotations: build and runtime ones, not the system ones DEX uses for attributes.</summary>
    private static List<JvmAnnotation> Annotations(IReadOnlyList<DexAnnotation> annotations)
        => annotations.Where(a => a.Visibility != DexVisibility.System)
            .Select(a => new JvmAnnotation(a.Type, a.Elements.Select(e => new JvmAnnotationElement(e.Name, Element(e.Value))).ToList(), a.Visibility == DexVisibility.Runtime))
            .ToList();

    private static JvmElementValue Element(DexValue value) => value.Kind switch
    {
        DexValueKind.Byte => new JvmConstantElement('B', (int)(long)value.Value!),
        DexValueKind.Short => new JvmConstantElement('S', (int)(long)value.Value!),
        DexValueKind.Char => new JvmConstantElement('C', (int)(long)value.Value!),
        DexValueKind.Int => new JvmConstantElement('I', (int)(long)value.Value!),
        DexValueKind.Long => new JvmConstantElement('J', (long)value.Value!),
        DexValueKind.Float => new JvmConstantElement('F', (float)value.Value!),
        DexValueKind.Double => new JvmConstantElement('D', (double)value.Value!),
        DexValueKind.Boolean => new JvmConstantElement('Z', (bool)value.Value! ? 1 : 0),
        DexValueKind.String => new JvmConstantElement('s', (string)value.Value!),
        DexValueKind.Type => new JvmClassElement((string)value.Value!),
        DexValueKind.Enum when value.Value is DexFieldRef field => new JvmEnumElement(field.Type, field.Name),
        DexValueKind.Array when value.Value is IReadOnlyList<DexValue> items => new JvmArrayElement(items.Select(Element).ToList()),
        DexValueKind.Annotation when value.Value is DexAnnotation nested
            => new JvmNestedAnnotation(new JvmAnnotation(nested.Type, nested.Elements.Select(e => new JvmAnnotationElement(e.Name, Element(e.Value))).ToList(), true)),
        _ => new JvmConstantElement('s', value.Value?.ToString()),
    };

    /// <summary>A static value as a constant pool entry of the field's type, or null for one a pool cannot hold.</summary>
    private static Constant? Constant(DexValue value, string type) => (value.Kind, type) switch
    {
        (DexValueKind.String, _) => new Constant(ConstantTag.String, 0, 0, 0, (string)value.Value!),
        (DexValueKind.Long, _) => new Constant(ConstantTag.Long, 0, 0, (long)value.Value!, null),
        (DexValueKind.Float, _) => new Constant(ConstantTag.Float, 0, 0, BitConverter.SingleToInt32Bits((float)value.Value!), null),
        (DexValueKind.Double, _) => new Constant(ConstantTag.Double, 0, 0, BitConverter.DoubleToInt64Bits((double)value.Value!), null),
        (DexValueKind.Boolean, _) => new Constant(ConstantTag.Integer, 0, 0, (bool)value.Value! ? 1 : 0, null),
        (DexValueKind.Byte or DexValueKind.Short or DexValueKind.Char or DexValueKind.Int, _) => new Constant(ConstantTag.Integer, 0, 0, (long)value.Value!, null),
        _ => null,
    };

    private static bool IsDefault(DexValue value) => value.Value switch
    {
        null => true,
        long l => l == 0,
        bool b => !b,
        float f => BitConverter.SingleToInt32Bits(f) == 0,
        double d => BitConverter.DoubleToInt64Bits(d) == 0,
        _ => false,
    };

    private static DexAnnotation? Find(IReadOnlyList<DexAnnotation> annotations, string name)
        => annotations.FirstOrDefault(a => a.Visibility == DexVisibility.System && a.Type == $"{System}{name};");

    /// <summary><c>dalvik.annotation.Signature</c> splits the signature into strings; together they are the attribute's.</summary>
    private static string? SignatureOf(IReadOnlyList<DexAnnotation> annotations)
        => Find(annotations, "Signature")?["value"]?.Value is IReadOnlyList<DexValue> parts
            ? string.Concat(parts.Select(p => p.Value as string))
            : null;
}
