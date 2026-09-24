namespace Spydate.Core.Dex;

/// <summary>
/// Access flags of a DEX class, field or method: the Java bits, plus the two DEX adds for methods — a
/// constructor, and <c>synchronized</c> declared in source (the Java bit means the VM locks for the method).
/// </summary>
[Flags]
public enum DexAccess : uint
{
    None = 0,
    Public = 0x0001,
    Private = 0x0002,
    Protected = 0x0004,
    Static = 0x0008,
    Final = 0x0010,
    Synchronized = 0x0020,
    VolatileOrBridge = 0x0040,
    TransientOrVarargs = 0x0080,
    Native = 0x0100,
    Interface = 0x0200,
    Abstract = 0x0400,
    Strict = 0x0800,
    Synthetic = 0x1000,
    Annotation = 0x2000,
    Enum = 0x4000,
    Constructor = 0x10000,
    DeclaredSynchronized = 0x20000,
}

/// <summary>A method prototype: its shorty, return type and parameter types, as type descriptors.</summary>
public sealed record DexProto(string Shorty, string ReturnType, IReadOnlyList<string> Parameters)
{
    /// <summary>The JVM-style method descriptor: <c>(ILjava/lang/String;)V</c>.</summary>
    public string Descriptor => $"({string.Concat(Parameters)}){ReturnType}";
}

/// <summary>A field as instructions name it: the class that declares it, its type and name.</summary>
public sealed record DexFieldRef(string Owner, string Type, string Name);

/// <summary>A method as instructions name it: the class, the name, the prototype.</summary>
public sealed record DexMethodRef(string Owner, string Name, DexProto Proto);

/// <summary>What a method handle does (<c>invoke-static</c>, <c>instance-get</c>…), with the member it names.</summary>
public sealed record DexMethodHandle(int Kind, DexFieldRef? Field, DexMethodRef? Method)
{
    public bool IsField => Kind <= 3;
}

/// <summary>A call site for <c>invoke-custom</c>: its bootstrap handle, name, type and extra arguments.</summary>
public sealed record DexCallSite(DexMethodHandle? Bootstrap, string Name, DexProto? Type, IReadOnlyList<DexValue> Arguments);

public enum DexValueKind
{
    Byte,
    Short,
    Char,
    Int,
    Long,
    Float,
    Double,
    MethodType,
    MethodHandle,
    String,
    Type,
    Field,
    Method,
    Enum,
    Array,
    Annotation,
    Null,
    Boolean,
}

/// <summary>
/// An encoded value from an annotation or a class's static values: a number (as <see cref="long"/>, or a
/// <see cref="float"/>/<see cref="double"/>), a string, a type descriptor, a member, an array, an annotation,
/// null or a boolean.
/// </summary>
public sealed record DexValue(DexValueKind Kind, object? Value);

public enum DexVisibility : byte
{
    Build = 0,
    Runtime = 1,
    System = 2,
}

/// <summary>An annotation: its type descriptor and its elements. System ones carry what javac keeps in attributes.</summary>
public sealed record DexAnnotation(DexVisibility Visibility, string Type, IReadOnlyList<(string Name, DexValue Value)> Elements)
{
    public DexValue? this[string name] => Elements.FirstOrDefault(e => e.Name == name).Value;
}

/// <summary>One try range: its start and length in code units, the handlers by caught type, and the catch-all.</summary>
public sealed record DexTry(int Start, int Length, IReadOnlyList<(string Type, int Address)> Handlers, int? CatchAll);

/// <summary>A local variable a debug table names: its register, the addresses it is live over, name, type and generic signature.</summary>
public sealed record DexLocal(int Register, int Start, int End, string? Name, string? Type, string? Signature);

/// <summary>A method's debug information: its first line, parameter names, lines by address and locals.</summary>
public sealed record DexDebugInfo(int LineStart, IReadOnlyList<string?> ParameterNames, IReadOnlyList<(int Address, int Line)> Lines, IReadOnlyList<DexLocal> Locals);

/// <summary>
/// A method's code: its register count (the last <see cref="Ins"/> registers hold the arguments), its
/// instructions in 16-bit code units, its try ranges and its debug information.
/// </summary>
public sealed record DexCode(int Registers, int Ins, int Outs, ReadOnlyMemory<ushort> Insns, IReadOnlyList<DexTry> Tries, DexDebugInfo? Debug)
{
    /// <summary>The file whose tables the instructions' indices refer to.</summary>
    public DexFile? File { get; init; }
}

public sealed record DexField(DexAccess Access, DexFieldRef Ref)
{
    public IReadOnlyList<DexAnnotation> Annotations { get; init; } = [];

    /// <summary>A static field's initial value from the class's static values, or null.</summary>
    public DexValue? StaticValue { get; init; }
}

public sealed record DexMethod(DexAccess Access, DexMethodRef Ref)
{
    /// <summary>Null for an abstract or native method.</summary>
    public DexCode? Code { get; init; }

    public IReadOnlyList<DexAnnotation> Annotations { get; init; } = [];

    /// <summary>Each parameter's annotations, in order; empty when none has any.</summary>
    public IReadOnlyList<IReadOnlyList<DexAnnotation>> ParameterAnnotations { get; init; } = [];
}

/// <summary>One class definition: its type descriptor, flags, supertypes, source file, members and annotations.</summary>
public sealed record DexClass(string Descriptor, DexAccess Access, string? Super, IReadOnlyList<string> Interfaces, string? SourceFile)
{
    public IReadOnlyList<DexField> StaticFields { get; init; } = [];

    public IReadOnlyList<DexField> InstanceFields { get; init; } = [];

    /// <summary>Static, private and constructor methods.</summary>
    public IReadOnlyList<DexMethod> DirectMethods { get; init; } = [];

    public IReadOnlyList<DexMethod> VirtualMethods { get; init; } = [];

    public IReadOnlyList<DexAnnotation> Annotations { get; init; } = [];

    /// <summary>The internal name, as a class file writes it: <c>com/example/Greeter</c>.</summary>
    public string Name => DexFile.InternalName(Descriptor);

    public IEnumerable<DexField> Fields => StaticFields.Concat(InstanceFields);

    public IEnumerable<DexMethod> Methods => DirectMethods.Concat(VirtualMethods);
}
