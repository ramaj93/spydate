namespace Spydate.Core.Jvm;

/// <summary>
/// Access and property flags, shared by classes, fields and methods. Some bits mean different things on each —
/// 0x20 is <c>ACC_SUPER</c> on a class and <c>synchronized</c> on a method; 0x40 is <c>volatile</c> or a bridge
/// method; 0x80 <c>transient</c> or varargs — which <see cref="JvmModifiers"/> spells out per kind.
/// </summary>
[Flags]
public enum JvmAccess : ushort
{
    None = 0,
    Public = 0x0001,
    Private = 0x0002,
    Protected = 0x0004,
    Static = 0x0008,
    Final = 0x0010,
    SuperOrSynchronized = 0x0020,
    VolatileOrBridge = 0x0040,
    TransientOrVarargs = 0x0080,
    Native = 0x0100,
    Interface = 0x0200,
    Abstract = 0x0400,
    Strict = 0x0800,
    Synthetic = 0x1000,
    Annotation = 0x2000,
    Enum = 0x4000,
    ModuleOrMandated = 0x8000,
}

/// <summary>One row of a method's exception table: a range of code, its handler, and what it catches (null = any).</summary>
public sealed record ExceptionHandler(int StartPc, int EndPc, int HandlerPc, string? CatchType);

/// <summary>Where a source line starts in a method's code.</summary>
public sealed record LineNumber(int StartPc, int Line);

/// <summary>A named local variable slot, live over a range of code. The signature carries its generic type, when it has one.</summary>
public sealed record LocalVariable(int StartPc, int Length, string Name, string Descriptor, int Slot)
{
    public string? Signature { get; init; }
}

/// <summary>A method's <c>Code</c> attribute: its bytecode, its exception table and the debug tables that name things in it.</summary>
public sealed record CodeAttribute(
    int MaxStack,
    int MaxLocals,
    ReadOnlyMemory<byte> Code,
    IReadOnlyList<ExceptionHandler> Handlers,
    IReadOnlyList<LineNumber> Lines,
    IReadOnlyList<LocalVariable> Locals);

/// <summary>
/// One row of the <c>InnerClasses</c> attribute. <see cref="Outer"/> is null for a local or anonymous class, and
/// <see cref="SimpleName"/> is null for an anonymous one.
/// </summary>
public sealed record InnerClassEntry(string Inner, string? Outer, string? SimpleName, JvmAccess Access);

/// <summary>A bootstrap method for <c>invokedynamic</c> or a dynamic constant: a MethodHandle and its static arguments, as pool indices.</summary>
public sealed record BootstrapMethod(int MethodHandle, IReadOnlyList<int> Arguments);

/// <summary>A record component: its name and type, which the canonical constructor and accessors follow.</summary>
public sealed record RecordComponent(string Name, string Descriptor, string? Signature);

public sealed record JvmField(JvmAccess Access, string Name, string Descriptor)
{
    public string? Signature { get; init; }

    /// <summary>The constant pool index of a <c>static final</c> field's compile-time value, or zero.</summary>
    public int ConstantValue { get; init; }

    /// <summary>Every attribute it carries, by name, known or not.</summary>
    public IReadOnlyList<string> Attributes { get; init; } = [];
}

public sealed record JvmMethod(JvmAccess Access, string Name, string Descriptor)
{
    public string? Signature { get; init; }

    /// <summary>Null for an abstract or native method, which has no body.</summary>
    public CodeAttribute? Code { get; init; }

    /// <summary>The checked exceptions it declares, as internal names.</summary>
    public IReadOnlyList<string> Exceptions { get; init; } = [];

    /// <summary>Parameter names from <c>MethodParameters</c>, when the compiler kept them; empty otherwise.</summary>
    public IReadOnlyList<string?> ParameterNames { get; init; } = [];

    public IReadOnlyList<string> Attributes { get; init; } = [];

    public bool IsConstructor => Name == "<init>";

    public bool IsStaticInitializer => Name == "<clinit>";
}

/// <summary>
/// A parsed Java class file: its constant pool, its identity, its fields and methods with their code, and the
/// class-level attributes that say where it came from and how it nests.
///
/// Parsing is strict about structure — the header, the pool, the counts — and lenient about attributes: an
/// attribute whose body does not parse is kept by name and reported in <see cref="Warnings"/>, because a class
/// with one damaged debug table is still a class worth reading.
/// </summary>
public sealed class ClassFile
{
    public const uint Magic = 0xCAFEBABE;

    private ClassFile()
    {
    }

    public ushort MinorVersion { get; private init; }

    public ushort MajorVersion { get; private init; }

    public required ConstantPool Pool { get; init; }

    public JvmAccess Access { get; private init; }

    /// <summary>The internal name: <c>com/example/Greeter$Inner</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Null only for <c>java/lang/Object</c> and <c>module-info</c>.</summary>
    public string? SuperName { get; private init; }

    public IReadOnlyList<string> Interfaces { get; private init; } = [];

    public IReadOnlyList<JvmField> Fields { get; private init; } = [];

    public IReadOnlyList<JvmMethod> Methods { get; private init; } = [];

    public string? SourceFile { get; private set; }

    public string? Signature { get; private set; }

    public IReadOnlyList<InnerClassEntry> InnerClasses { get; private set; } = [];

    /// <summary>For a local or anonymous class, the class whose code declares it.</summary>
    public string? EnclosingClass { get; private set; }

    /// <summary>For a local or anonymous class declared inside a method, that method's name and descriptor.</summary>
    public (string Name, string Descriptor)? EnclosingMethod { get; private set; }

    public string? NestHost { get; private set; }

    public IReadOnlyList<string> PermittedSubclasses { get; private set; } = [];

    public IReadOnlyList<RecordComponent>? RecordComponents { get; private set; }

    public IReadOnlyList<BootstrapMethod> BootstrapMethods { get; private set; } = [];

    public bool IsDeprecated { get; private set; }

    public IReadOnlyList<string> Attributes { get; private set; } = [];

    public IReadOnlyList<string> Warnings => _warnings;

    private readonly List<string> _warnings = [];

    public bool IsInterface => (Access & JvmAccess.Interface) != 0;

    public bool IsModule => (Access & JvmAccess.ModuleOrMandated) != 0;

    /// <summary>The Java release this class file's version belongs to: <c>Java 17</c> for 61.0, <c>Java 1.4</c> for 48.0.</summary>
    public string JavaVersion => JavaRelease(MajorVersion);

    public static string JavaRelease(int major) => major switch
    {
        < 45 => $"class file {major}",
        45 => "Java 1.1",
        46 => "Java 1.2",
        47 => "Java 1.3",
        48 => "Java 1.4",
        _ => $"Java {major - 44}",
    };

    /// <summary>The package, as an internal name with slashes: <c>com/example</c>, or empty for the default package.</summary>
    public string PackageName => Name.LastIndexOf('/') is var slash and >= 0 ? Name[..slash] : string.Empty;

    /// <summary>
    /// Parses one class file. Throws <see cref="ClassFormatException"/> when the bytes are not a class file or
    /// its structure is broken; everything short of that is tolerated and reported in <see cref="Warnings"/>.
    /// </summary>
    public static ClassFile Parse(ReadOnlyMemory<byte> data)
    {
        var reader = new ClassReader(data.Span);
        if (reader.Length < 10 || reader.U4() != Magic)
        {
            throw new ClassFormatException("Not a class file: it does not start with 0xCAFEBABE.");
        }

        ushort minor = reader.U2();
        ushort major = reader.U2();
        var pool = ConstantPool.Read(ref reader);

        var access = (JvmAccess)reader.U2();
        string name = pool.RequireClass(reader.U2(), "class's own name");
        int superIndex = reader.U2();
        string? superName = superIndex == 0 ? null : pool.RequireClass(superIndex, "superclass");

        int interfaceCount = reader.Count(2, "interfaces");
        var interfaces = new string[interfaceCount];
        for (int i = 0; i < interfaceCount; i++)
        {
            interfaces[i] = pool.RequireClass(reader.U2(), "interface");
        }

        var warnings = new List<string>();
        var fields = ReadFields(ref reader, data, pool, warnings);
        var methods = ReadMethods(ref reader, data, pool, warnings);

        var file = new ClassFile
        {
            MinorVersion = minor,
            MajorVersion = major,
            Pool = pool,
            Access = access,
            Name = name,
            SuperName = superName,
            Interfaces = interfaces,
            Fields = fields,
            Methods = methods,
        };
        file._warnings.AddRange(warnings);
        file.ReadClassAttributes(ref reader, data);

        if (reader.Remaining > 0)
        {
            file._warnings.Add($"{reader.Remaining} byte(s) follow the class file's end.");
        }

        return file;
    }

    private static JvmField[] ReadFields(ref ClassReader reader, ReadOnlyMemory<byte> data, ConstantPool pool, List<string> warnings)
    {
        int count = reader.Count(8, "fields");
        var fields = new JvmField[count];
        for (int i = 0; i < count; i++)
        {
            var access = (JvmAccess)reader.U2();
            string name = pool.RequireUtf8(reader.U2(), "field's name");
            string descriptor = pool.RequireUtf8(reader.U2(), $"field {name}'s type");
            string? signature = null;
            int constant = 0;
            var names = new List<string>();
            ForEachAttribute(ref reader, data, pool, names, warnings, $"field {name}", (string attribute, ref ClassReader body, int _) =>
            {
                switch (attribute)
                {
                    case "Signature":
                        signature = pool.Utf8(body.U2());
                        break;
                    case "ConstantValue":
                        constant = body.U2();
                        break;
                }
            });

            fields[i] = new JvmField(access, name, descriptor) { Signature = signature, ConstantValue = constant, Attributes = names };
        }

        return fields;
    }

    private static JvmMethod[] ReadMethods(ref ClassReader reader, ReadOnlyMemory<byte> data, ConstantPool pool, List<string> warnings)
    {
        int count = reader.Count(8, "methods");
        var methods = new JvmMethod[count];
        for (int i = 0; i < count; i++)
        {
            var access = (JvmAccess)reader.U2();
            string name = pool.RequireUtf8(reader.U2(), "method's name");
            string descriptor = pool.RequireUtf8(reader.U2(), $"method {name}'s descriptor");
            string? signature = null;
            CodeAttribute? code = null;
            IReadOnlyList<string> exceptions = [];
            IReadOnlyList<string?> parameters = [];
            var names = new List<string>();
            ForEachAttribute(ref reader, data, pool, names, warnings, $"method {name}", (string attribute, ref ClassReader body, int start) =>
            {
                switch (attribute)
                {
                    case "Signature":
                        signature = pool.Utf8(body.U2());
                        break;
                    case "Code":
                        code = ReadCode(ref body, data, start, pool, warnings, name);
                        break;
                    case "Exceptions":
                        int n = body.Count(2, "exceptions");
                        var thrown = new string[n];
                        for (int e = 0; e < n; e++)
                        {
                            thrown[e] = pool.ClassName(body.U2()) ?? "?";
                        }

                        exceptions = thrown;
                        break;
                    case "MethodParameters":
                        int p = body.U1();
                        var parameterNames = new string?[p];
                        for (int e = 0; e < p; e++)
                        {
                            parameterNames[e] = pool.Utf8(body.U2());
                            body.U2();   // access flags
                        }

                        parameters = parameterNames;
                        break;
                }
            });

            methods[i] = new JvmMethod(access, name, descriptor)
            {
                Signature = signature,
                Code = code,
                Exceptions = exceptions,
                ParameterNames = parameters,
                Attributes = names,
            };
        }

        return methods;
    }

    private static CodeAttribute ReadCode(ref ClassReader body, ReadOnlyMemory<byte> data, int start, ConstantPool pool, List<string> warnings, string method)
    {
        int maxStack = body.U2();
        int maxLocals = body.U2();
        uint length = body.U4();
        if (length > (uint)body.Remaining)
        {
            throw new ClassFormatException($"code of {length} bytes cannot fit in the {body.Remaining} left");
        }

        var code = data.Slice(start + body.Position, (int)length);
        body.Skip(length);

        int handlerCount = body.Count(8, "exception handlers");
        var handlers = new ExceptionHandler[handlerCount];
        for (int i = 0; i < handlerCount; i++)
        {
            int startPc = body.U2();
            int endPc = body.U2();
            int handlerPc = body.U2();
            int type = body.U2();
            handlers[i] = new ExceptionHandler(startPc, endPc, handlerPc, type == 0 ? null : pool.ClassName(type) ?? "?");
        }

        var lines = new List<LineNumber>();
        var locals = new List<LocalVariable>();
        var types = new Dictionary<(int, int, int), string>();
        ForEachAttribute(ref body, data, start, pool, [], warnings, $"method {method}'s code", (string attribute, ref ClassReader inner, int _) =>
        {
            switch (attribute)
            {
                case "LineNumberTable":
                    int n = inner.Count(4, "line numbers");
                    for (int i = 0; i < n; i++)
                    {
                        lines.Add(new LineNumber(inner.U2(), inner.U2()));
                    }

                    break;
                case "LocalVariableTable":
                case "LocalVariableTypeTable":
                    int m = inner.Count(10, "local variables");
                    for (int i = 0; i < m; i++)
                    {
                        int from = inner.U2();
                        int span = inner.U2();
                        string localName = pool.Utf8(inner.U2()) ?? "?";
                        string type = pool.Utf8(inner.U2()) ?? "?";
                        int slot = inner.U2();
                        if (attribute == "LocalVariableTable")
                        {
                            locals.Add(new LocalVariable(from, span, localName, type, slot));
                        }
                        else
                        {
                            types[(from, span, slot)] = type;
                        }
                    }

                    break;
            }
        });

        // The type table only adds generic signatures to variables the plain table already has.
        if (types.Count > 0)
        {
            for (int i = 0; i < locals.Count; i++)
            {
                var local = locals[i];
                if (types.TryGetValue((local.StartPc, local.Length, local.Slot), out string? signature))
                {
                    locals[i] = local with { Signature = signature };
                }
            }
        }

        lines.Sort((a, b) => a.StartPc.CompareTo(b.StartPc));
        return new CodeAttribute(maxStack, maxLocals, code, handlers, lines, locals);
    }

    private void ReadClassAttributes(ref ClassReader reader, ReadOnlyMemory<byte> data)
    {
        var names = new List<string>();
        var pool = Pool;
        ForEachAttribute(ref reader, data, pool, names, _warnings, "class", (string attribute, ref ClassReader body, int _) =>
        {
            switch (attribute)
            {
                case "SourceFile":
                    SourceFile = pool.Utf8(body.U2());
                    break;
                case "Signature":
                    Signature = pool.Utf8(body.U2());
                    break;
                case "Deprecated":
                    IsDeprecated = true;
                    break;
                case "NestHost":
                    NestHost = pool.ClassName(body.U2());
                    break;
                case "EnclosingMethod":
                    EnclosingClass = pool.ClassName(body.U2());
                    int method = body.U2();
                    EnclosingMethod = method == 0 ? null : pool.NameAndType(method);
                    break;
                case "InnerClasses":
                    int n = body.Count(8, "inner classes");
                    var inner = new List<InnerClassEntry>(n);
                    for (int i = 0; i < n; i++)
                    {
                        int innerIndex = body.U2();
                        int outerIndex = body.U2();
                        int nameIndex = body.U2();
                        var flags = (JvmAccess)body.U2();
                        if (pool.ClassName(innerIndex) is { } innerName)
                        {
                            inner.Add(new InnerClassEntry(innerName, outerIndex == 0 ? null : pool.ClassName(outerIndex), nameIndex == 0 ? null : pool.Utf8(nameIndex), flags));
                        }
                    }

                    InnerClasses = inner;
                    break;
                case "PermittedSubclasses":
                    int p = body.Count(2, "permitted subclasses");
                    var permitted = new List<string>(p);
                    for (int i = 0; i < p; i++)
                    {
                        permitted.Add(pool.ClassName(body.U2()) ?? "?");
                    }

                    PermittedSubclasses = permitted;
                    break;
                case "Record":
                    int r = body.Count(6, "record components");
                    var components = new List<RecordComponent>(r);
                    for (int i = 0; i < r; i++)
                    {
                        string componentName = pool.Utf8(body.U2()) ?? "?";
                        string descriptor = pool.Utf8(body.U2()) ?? "?";
                        string? signature = null;
                        ForEachAttribute(ref body, data, 0, pool, [], _warnings, $"record component {componentName}", (string a, ref ClassReader b, int _) =>
                        {
                            if (a == "Signature")
                            {
                                signature = pool.Utf8(b.U2());
                            }
                        });
                        components.Add(new RecordComponent(componentName, descriptor, signature));
                    }

                    RecordComponents = components;
                    break;
                case "BootstrapMethods":
                    int b = body.Count(4, "bootstrap methods");
                    var methods = new List<BootstrapMethod>(b);
                    for (int i = 0; i < b; i++)
                    {
                        int handle = body.U2();
                        int argc = body.Count(2, "bootstrap arguments");
                        var args = new int[argc];
                        for (int a = 0; a < argc; a++)
                        {
                            args[a] = body.U2();
                        }

                        methods.Add(new BootstrapMethod(handle, args));
                    }

                    BootstrapMethods = methods;
                    break;
            }
        });

        Attributes = names;
    }

    private delegate void AttributeBody(string name, ref ClassReader body, int start);

    private static void ForEachAttribute(ref ClassReader reader, ReadOnlyMemory<byte> data, ConstantPool pool, List<string> names, List<string> warnings, string owner, AttributeBody read)
        => ForEachAttribute(ref reader, data, 0, pool, names, warnings, owner, read);

    /// <summary>
    /// Reads an attribute table, handing each body to <paramref name="read"/> on a reader of its own, bounded by
    /// the length the attribute declares. A body that does not parse costs that attribute and a warning, not the
    /// class. <paramref name="baseOffset"/> is where <paramref name="reader"/>'s span starts in <paramref name="data"/>,
    /// so a body can slice the class file's memory rather than copy.
    /// </summary>
    private static void ForEachAttribute(ref ClassReader reader, ReadOnlyMemory<byte> data, int baseOffset, ConstantPool pool, List<string> names, List<string> warnings, string owner, AttributeBody read)
    {
        int count = reader.Count(6, $"attributes of the {owner}");
        for (int i = 0; i < count; i++)
        {
            string name = pool.Utf8(reader.U2()) ?? "?";
            uint length = reader.U4();
            if (length > (uint)reader.Remaining)
            {
                throw new ClassFormatException($"The {owner}'s {name} attribute claims {length} bytes; {reader.Remaining} are left.");
            }

            int start = baseOffset + reader.Position;
            var body = new ClassReader(reader.Bytes(length));
            names.Add(name);
            try
            {
                read(name, ref body, start);
            }
            catch (ClassFormatException ex)
            {
                warnings.Add($"The {owner}'s {name} attribute could not be read: {ex.Message}");
            }
        }
    }
}
