namespace Spydate.Core.Readings;

/// <summary>Which bytecode a reading is of. It decides how a body is listed, not how the reading is browsed.</summary>
public enum BytecodeKind
{
    /// <summary>.NET CIL, with metadata: an assembly inside a PE.</summary>
    DotNet,

    /// <summary>Java class files, as a JAR holds them.</summary>
    Jvm,

    /// <summary>Android's Dalvik bytecode, in the <c>classes*.dex</c> of an APK.</summary>
    Dalvik,
}

/// <summary>What a type is. Every bytecode format with types has these, under one name or another.</summary>
public enum BytecodeTypeKind
{
    Class,
    Interface,
    Struct,
    Enum,
    Delegate,

    /// <summary>A Java annotation type, a .NET module type, anything the others do not name.</summary>
    Other,
}

public enum BytecodeMemberKind
{
    Method,
    Constructor,
    Field,
    Property,
    Event,
}

/// <summary>A member of a type: a method, constructor, field, property or event.</summary>
public interface IBytecodeMember
{
    /// <summary>The name in the metadata: <c>.ctor</c>, <c>&lt;init&gt;</c>, <c>Load</c>.</summary>
    string Name { get; }

    /// <summary>How the listings print it: <c>Load(String) : PeImage</c>. What an agent copies back.</summary>
    string Signature { get; }

    BytecodeMemberKind Kind { get; }
}

/// <summary>A type, with the types nested in it and its members.</summary>
public interface IBytecodeType
{
    string Name { get; }

    /// <summary>The name the reading's own language writes: <c>Spydate.Core.PE.PeImage</c>, <c>java.util.List</c>.</summary>
    string FullName { get; }

    /// <summary>
    /// Other names the same type goes by, which resolving a name must also accept: the metadata's form with
    /// <c>+</c> for nesting and a backtick for arity, or a JVM internal name with slashes.
    /// </summary>
    IReadOnlyList<string> OtherNames { get; }

    BytecodeTypeKind Kind { get; }

    /// <summary>The kind in the reading's own words, lower-case, for display: <c>class</c>, <c>struct</c>.</summary>
    string KindName { get; }

    IReadOnlyList<IBytecodeType> NestedTypes { get; }

    IReadOnlyList<IBytecodeMember> Members { get; }
}

/// <summary>A namespace or package, and the top-level types in it.</summary>
public interface IBytecodeNamespace
{
    /// <summary>Empty for the global namespace or the default package.</summary>
    string Name { get; }

    string DisplayName { get; }

    IReadOnlyList<IBytecodeType> Types { get; }
}

/// <summary>
/// A second reading of a file: its bytecode, alongside (or instead of) its native code. A .NET assembly is a
/// PE whose program is CIL; a JAR is nothing but JVM bytecode; an APK is Dalvik bytecode with native libraries
/// beside it. Each is browsed the same way — namespaces, types, members — and read one member or one type at a
/// time in whichever views the reading offers.
///
/// This is deliberately what the MCP tools and the window's explorer ask of the .NET reading that a second one
/// can also answer, measured rather than guessed. What only .NET has — method bodies at file addresses the
/// debugger breaks on, IL patching, P/Invokes, resolving referenced assemblies — stays on the concrete reading,
/// reached by asking for it, the way a PE's load config stays on <c>PeImage</c>.
/// </summary>
public interface IBytecodeReading
{
    BytecodeKind Kind { get; }

    /// <summary>The short name: an assembly's simple name, a JAR's file name.</summary>
    string Name { get; }

    /// <summary>The full identity: an assembly's display name with version and key.</summary>
    string FullName { get; }

    /// <summary>What it targets: <c>.NETCoreApp,Version=v10.0</c>, <c>Java 17</c>.</summary>
    string Platform { get; }

    /// <summary>The version of the container's own format: CLR metadata <c>v4.0.30319</c>, class file <c>61.0</c>.</summary>
    string FormatVersion { get; }

    /// <summary>What a whole reading is called in messages: "assembly", "archive".</summary>
    string Noun { get; }

    IReadOnlyList<IBytecodeNamespace> Namespaces { get; }

    /// <summary>What it needs to run: referenced assemblies, required libraries. Display names.</summary>
    IReadOnlyList<string> Requires { get; }

    /// <summary>Where execution starts, when the reading declares it: <c>Main</c>, a manifest's Main-Class.</summary>
    IBytecodeMember? EntryPoint { get; }

    /// <summary>The views a type or member can be read in, preferred first: <c>csharp</c>, <c>il</c>; <c>bytecode</c>.</summary>
    IReadOnlyList<string> Views { get; }

    /// <summary>
    /// A type, or one member of it, in one of <see cref="Views"/>, as plain text. The member must be one of the
    /// type's own. Throws <see cref="ArgumentException"/> for a view the reading does not offer.
    /// </summary>
    string Render(IBytecodeType type, IBytecodeMember? member, string view, CancellationToken cancellationToken = default);

    /// <summary>
    /// The key a name or comment on this type or member is stored under, when the reading keys annotations by
    /// member — a JAR has no addresses to key them by. It must stay the same across builds of the same code and
    /// tell overloads apart. Null when the reading's annotations live at addresses instead, as .NET's do: its
    /// methods are annotated where their IL sits.
    /// </summary>
    string? AnnotationKey(IBytecodeType type, IBytecodeMember? member);
}
