using Spydate.Core.Jvm;
using Spydate.Decompiler.Native.IR;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// What the printer asks of the class around the method it prints: how to write a class the compiler made up —
/// an anonymous class where it is created, a lambda where its method is referenced, a local class where it is
/// declared — what the compiler's own fields mean (<c>this$0</c> is the enclosing instance, <c>val$x</c> the
/// captured <c>x</c>), and which constructor arguments the compiler added. Null answers mean "print it as it is".
/// </summary>
internal interface IJavaScope
{
    /// <summary><c>new Iface() { … }</c> for an anonymous class, its members at <paramref name="level"/> + 1.</summary>
    string? Anonymous(JNew created, Func<IrArguments, string> arguments, int level);

    /// <summary>The source's arguments for <c>new T(…)</c> of a class whose constructor takes the compiler's own too, and the outer instance, if any.</summary>
    (JExpr? Outer, IReadOnlyList<JExpr> Args, string Descriptor)? Constructed(JNew created);

    /// <summary>A lambda written out in place of the synthetic method that holds its body.</summary>
    string? Lambda(JDynamic dynamic, int level);

    /// <summary>What a field the compiler added reads as: <c>Outer.this</c>, or the captured local's name.</summary>
    string? SyntheticField(JField field);

    /// <summary>For a switch on an enum — on <c>e.ordinal()</c> or a switch map — the switched value and each case's constant.</summary>
    (JExpr Value, IReadOnlyDictionary<int, string> Names)? EnumSwitch(IrExpr value);

    /// <summary>A local class declared here: its whole declaration at <paramref name="level"/>.</summary>
    string? LocalClass(string internalName, int level);

    /// <summary>Whether a class is a local class, declared in a method body.</summary>
    bool IsLocalClass(string internalName);

    /// <summary>For an anonymous class, the type it extends or implements, as a variable of it is declared; null otherwise.</summary>
    string? AnonymousType(string internalName);

    /// <summary>What a call to a synthetic accessor (<c>Outer.access$000(x)</c>) does, written out; null for any other call.</summary>
    JExpr? Accessor(JCall call);
}

/// <summary>Arguments to print, with their descriptor, for <see cref="IJavaScope.Anonymous"/>.</summary>
internal sealed record IrArguments(IReadOnlyList<JExpr> Args, string Descriptor, string Owner);

/// <summary>
/// How the Java printer names things: the class being printed (for its package and to call its own members
/// without qualification), the names the project has given classes and members, by annotation key, and the
/// class around it (<see cref="Scope"/>). Class names print as tokens that <see cref="JavaImports"/> resolves
/// once the whole class is written, so imports can be chosen from what was really used.
/// </summary>
internal sealed class JavaNaming
{
    private readonly Func<string, string?> _given;
    private readonly Func<string, ClassFile?> _find;

    public JavaNaming(ClassFile file, Func<string, string?> given, Func<string, ClassFile?>? find = null, IJavaScope? scope = null)
    {
        Class = file;
        _given = given;
        _find = find ?? (_ => null);
        Scope = scope;
        Package = file.PackageName;
    }

    /// <summary>A class of the same JAR by internal name, or null for one it does not hold — the JDK's, a library's.</summary>
    public ClassFile? FindClass(string internalName) => internalName == Class.Name ? Class : _find(internalName);

    public ClassFile Class { get; }

    public string Package { get; }

    public IJavaScope? Scope { get; }

    /// <summary>
    /// While a static field's initialiser is written, the class's static fields declared from it on: Java forbids
    /// their simple names there — even in a lambda — so they are written <c>Owner.field</c>, as the source must have.
    /// </summary>
    public IReadOnlySet<string>? ForwardStatics { get; set; }

    public string? GivenClassName(string internalName) => _given(internalName);

    public string MethodName(string owner, string name, string descriptor) => _given($"{owner}.{name}{descriptor}") ?? name;

    public string FieldName(string owner, string name, string descriptor) => _given($"{owner}.{name}:{descriptor}") ?? name;

    /// <summary>A descriptor as Java writes the type: <c>int</c>, <c>byte[][]</c>, a class as its name.</summary>
    public string Type(string? descriptor)
    {
        if (descriptor is null or "")
        {
            return JavaImports.Token("java/lang/Object");
        }

        int dims = 0;
        while (dims < descriptor.Length && descriptor[dims] == '[')
        {
            dims++;
        }

        string element = descriptor[dims..] switch
        {
            "Z" => "boolean",
            "B" => "byte",
            "C" => "char",
            "S" => "short",
            "I" => "int",
            "J" => "long",
            "F" => "float",
            "D" => "double",
            "V" => "void",
            ['L', .. var name, ';'] => ClassName(name),
            var other => other,
        };

        return dims == 0 ? element : element + string.Concat(Enumerable.Repeat("[]", dims));
    }

    /// <summary>
    /// An internal class name as printed: the project's name for it if it has one, otherwise a token that becomes
    /// the simple or qualified name when the class's imports are settled.
    /// </summary>
    public string ClassName(string internalName)
    {
        if (internalName.StartsWith('['))
        {
            return Type(internalName);
        }

        return _given(internalName) ?? JavaImports.Token(internalName);
    }
}
