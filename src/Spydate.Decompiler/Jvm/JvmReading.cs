using System.Globalization;
using Spydate.Core.Android;
using Spydate.Core.Jvm;
using Spydate.Core.Project;
using Spydate.Core.Readings;

namespace Spydate.Decompiler.Jvm;

/// <summary>A class in a JAR, seen through the reading seam, with the classes nested in it.</summary>
public sealed class JvmType : IBytecodeType
{
    private readonly List<IBytecodeType> _nested = [];

    internal JvmType(JarClass jarClass)
    {
        Class = jarClass;
        Name = SimpleName(File.Name);
        FullName = Descriptors.ClassName(File.Name);
        Members = [.. File.Fields.Select(f => (IBytecodeMember)new JvmMember(this, null, f)), .. File.Methods.Select(m => new JvmMember(this, m, null))];
    }

    public JarClass Class { get; }

    public ClassFile File => Class.File;

    /// <summary>The class this one is declared in, or null for a top-level class.</summary>
    public JvmType? Outer { get; private set; }

    /// <summary>The <c>InnerClasses</c> row that describes this class, when it is nested; it carries the access a nested class really has.</summary>
    public InnerClassEntry? Nesting { get; private set; }

    public string Name { get; private set; }

    public string FullName { get; private set; }

    /// <summary>The internal name with slashes, and the binary name with dots and dollars, both of which an agent may paste.</summary>
    public IReadOnlyList<string> OtherNames => FullName == Descriptors.ClassName(File.Name)
        ? [File.Name]
        : [File.Name, Descriptors.ClassName(File.Name)];

    public BytecodeTypeKind Kind => (File.Access & JvmAccess.Annotation) != 0 ? BytecodeTypeKind.Other
        : File.IsInterface ? BytecodeTypeKind.Interface
        : (File.Access & JvmAccess.Enum) != 0 ? BytecodeTypeKind.Enum
        : BytecodeTypeKind.Class;

    public string KindName => JvmModifiers.ClassKeyword(File.Access, File.SuperName) switch
    {
        "@interface" => "annotation",
        var keyword => keyword,
    };

    public IReadOnlyList<IBytecodeType> NestedTypes => _nested;

    public IReadOnlyList<IBytecodeMember> Members { get; }

    /// <summary>The access the source declared: a nested class's real one is in its InnerClasses row, not its own flags.</summary>
    public JvmAccess DeclaredAccess => Nesting?.Access ?? File.Access;

    internal void NestIn(JvmType outer, InnerClassEntry? row)
    {
        Outer = outer;
        Nesting = row;
        outer._nested.Add(this);
    }

    /// <summary>
    /// Names the classes nested in this one, then theirs: top-down, once every link is made, so a class's full
    /// name is built on its outer class's final one whatever order the archive listed them in.
    /// </summary>
    internal void NameNested()
    {
        foreach (var child in _nested.Cast<JvmType>())
        {
            child.NameFrom(this);
            child.NameNested();
        }

        _nested.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
    }

    private void NameFrom(JvmType outer)
    {
        var row = Nesting;

        // A member class reads Outer.Inner, as Java writes it; a local or anonymous one keeps its binary name's
        // tail — Greeter$1, Greeter$1Local — since Java gives it no other.
        string tail = File.Name.StartsWith(outer.File.Name + "$", StringComparison.Ordinal)
            ? File.Name[(outer.File.Name.Length + 1)..]
            : SimpleName(File.Name);
        if (row is { Outer: not null, SimpleName: { Length: > 0 } simple })
        {
            Name = simple;
            FullName = $"{outer.FullName}.{simple}";
        }
        else
        {
            Name = tail;
            FullName = $"{outer.FullName}${tail}";
        }
    }

    private static string SimpleName(string internalName)
    {
        int slash = internalName.LastIndexOf('/');
        return slash < 0 ? internalName : internalName[(slash + 1)..];
    }

    public override string ToString() => FullName;
}

/// <summary>A field or method of a <see cref="JvmType"/>.</summary>
public sealed class JvmMember : IBytecodeMember
{
    internal JvmMember(JvmType owner, JvmMethod? method, JvmField? field)
    {
        Owner = owner;
        Method = method;
        Field = field;
        Name = method?.Name ?? field!.Name;
        Kind = method is null ? BytecodeMemberKind.Field
            : method.IsConstructor || method.IsStaticInitializer ? BytecodeMemberKind.Constructor
            : BytecodeMemberKind.Method;
        Signature = method is null ? $"{field!.Name} : {Descriptors.TypeName(field.Descriptor, simple: true)}" : MethodSignature(owner, method);
    }

    public JvmType Owner { get; }

    /// <summary>Set for a method or constructor.</summary>
    public JvmMethod? Method { get; }

    /// <summary>Set for a field.</summary>
    public JvmField? Field { get; }

    public string Name { get; }

    public string Signature { get; }

    public BytecodeMemberKind Kind { get; }

    /// <summary>The JVM descriptor, which with the name tells overloads apart.</summary>
    public string Descriptor => Method?.Descriptor ?? Field!.Descriptor;

    /// <summary>
    /// <c>greet(String) : String</c>; a constructor is written with its class's name and no return, as Java
    /// writes one; a static initializer is <c>&lt;clinit&gt;()</c>. Short type names, the way the .NET reading
    /// prints them, so an agent can type one back.
    /// </summary>
    private static string MethodSignature(JvmType owner, JvmMethod method)
    {
        if (Descriptors.Method(method.Descriptor, simple: true) is not { } parsed)
        {
            return $"{method.Name}{method.Descriptor}";
        }

        string parameters = string.Join(", ", parsed.Parameters);
        return method.IsConstructor ? $"{owner.Name}({parameters})"
            : method.IsStaticInitializer ? "<clinit>()"
            : $"{method.Name}({parameters}) : {parsed.Return}";
    }

    public override string ToString() => $"{Owner.FullName}::{Signature}";
}

/// <summary>A Java package: the top-level classes whose internal names share a directory.</summary>
public sealed class JvmPackage : IBytecodeNamespace
{
    internal JvmPackage(string name, IReadOnlyList<IBytecodeType> types)
    {
        Name = name;
        Types = types;
    }

    public string Name { get; }

    public string DisplayName => Name.Length == 0 ? "(default package)" : Name;

    public IReadOnlyList<IBytecodeType> Types { get; }
}

/// <summary>
/// The JVM reading of a JAR: its classes by package, with nesting recovered from each class's own
/// <c>InnerClasses</c> and <c>EnclosingMethod</c> attributes, and each member read as bytecode.
///
/// Annotations are keyed by member (<see cref="AnnotationKey"/>), since nothing in a JAR has an address; the store
/// is handed in so a listing shows what has been recorded, the way pseudo-C shows a renamed function.
/// </summary>
public sealed class JvmReading : IBytecodeReading
{
    /// <summary>The in-house decompiler's Java-shaped pseudo-code: the default view.</summary>
    public const string JavaView = "java";

    public const string BytecodeView = "bytecode";

    private readonly Dictionary<string, JvmType> _byInternalName = new(StringComparer.Ordinal);

    public JvmReading(JarImage jar, MemberAnnotationStore? annotations = null)
        : this(jar.Classes, annotations)
    {
        Jar = jar;
        int newest = jar.Classes.Count == 0 ? 0 : jar.Classes.Max(c => c.File.MajorVersion);
        var sample = jar.Classes.FirstOrDefault(c => c.File.MajorVersion == newest)?.File;
        Platform = newest == 0 ? "no classes" : ClassFile.JavaRelease(newest);
        FormatVersion = sample is null ? "-" : string.Create(CultureInfo.InvariantCulture, $"{sample.MajorVersion}.{sample.MinorVersion}");

        if (jar.Manifest?.MainClass is { } main && _byInternalName.TryGetValue(main, out var mainType))
        {
            EntryPoint = mainType.Members.OfType<JvmMember>()
                .FirstOrDefault(m => m.Method is { Name: "main" } method
                                     && (method.Access & JvmAccess.Static) != 0
                                     && method.Descriptor == "([Ljava/lang/String;)V")
                         ?? mainType.Members.OfType<JvmMember>().FirstOrDefault(m => m.Name == "main");
            MainType = mainType;
        }
    }

    /// <summary>
    /// An Android package's classes: its DEX classes as class files (<see cref="DexClasses"/>). What launches is the
    /// manifest's main activity, from its <c>onCreate</c>.
    /// </summary>
    public JvmReading(ApkImage apk, MemberAnnotationStore? annotations = null)
        : this(apk.ClassFiles, annotations)
    {
        Apk = apk;
        Kind = BytecodeKind.Dalvik;
        Noun = "package";
        string? minSdk = apk.Manifest?.MinSdk;
        Platform = minSdk is null ? (apk.DexFiles.Count == 0 ? "no code" : apk.DexFiles[0].File.AndroidVersion) : $"Android API {minSdk}+";
        FormatVersion = apk.DexVersion == 0 ? "-" : string.Create(CultureInfo.InvariantCulture, $"DEX {apk.DexVersion:D3}");

        if (apk.Manifest?.MainActivity is { } main && _byInternalName.TryGetValue(main.Replace('.', '/'), out var mainType))
        {
            EntryPoint = mainType.Members.OfType<JvmMember>().FirstOrDefault(m => m.Name == "onCreate")
                         ?? mainType.Members.OfType<JvmMember>().FirstOrDefault(m => m.Method?.IsConstructor == true);
            MainType = mainType;
        }
    }

    private JvmReading(IReadOnlyList<JarClass> classes, MemberAnnotationStore? annotations)
    {
        Annotations = annotations;
        _references = new Lazy<JvmReferences>(() => JvmReferences.Build(this), LazyThreadSafetyMode.ExecutionAndPublication);

        foreach (var jarClass in classes)
        {
            _byInternalName[jarClass.File.Name] = new JvmType(jarClass);
        }

        var topLevel = new List<JvmType>();
        foreach (var type in _byInternalName.Values)
        {
            if (OuterOf(type) is { } nesting)
            {
                type.NestIn(nesting.Outer, nesting.Row);
            }
            else
            {
                topLevel.Add(type);
            }
        }

        foreach (var type in topLevel)
        {
            type.NameNested();
        }

        Namespaces = topLevel
            .GroupBy(t => t.File.PackageName.Replace('/', '.'), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (IBytecodeNamespace)new JvmPackage(g.Key, g.OrderBy(t => t.Name, StringComparer.Ordinal).ToList<IBytecodeType>()))
            .ToList();
    }

    /// <summary>The Java archive read; null for an Android package.</summary>
    public JarImage? Jar { get; }

    /// <summary>The Android package read; null for a Java archive.</summary>
    public ApkImage? Apk { get; }

    public MemberAnnotationStore? Annotations { get; }

    public BytecodeKind Kind { get; } = BytecodeKind.Jvm;

    public string Name => Jar?.FileName ?? Apk!.FileName;

    /// <summary>What the manifest calls it, with its version, falling back to the module name and then the file name.</summary>
    public string FullName
    {
        get
        {
            if (Apk is { } apk)
            {
                return apk.Manifest?.Package is { } package ? apk.Manifest.VersionName is { } versionName ? $"{package} {versionName}" : package : apk.FileName;
            }

            var manifest = Jar!.Manifest;
            string? title = manifest?["Implementation-Title"] ?? manifest?["Bundle-Name"] ?? manifest?["Automatic-Module-Name"] ?? Jar.ModuleName;
            string? version = manifest?["Implementation-Version"] ?? manifest?["Bundle-Version"];
            return title is null ? Jar.FileName : version is null ? title : $"{title} {version}";
        }
    }

    /// <summary>The newest Java release any class needs: the archive runs on nothing older.</summary>
    public string Platform { get; } = "no classes";

    public string FormatVersion { get; } = "-";

    public string Noun { get; } = "archive";

    public IReadOnlyList<IBytecodeNamespace> Namespaces { get; }

    public IReadOnlyList<string> Requires => Jar?.Manifest?.ClassPath ?? [];

    public IBytecodeMember? EntryPoint { get; }

    /// <summary>The manifest's Main-Class, when it is in the archive.</summary>
    public JvmType? MainType { get; }

    public IReadOnlyList<string> Views { get; } = [JavaView, BytecodeView];

    /// <summary>The class with this internal name (<c>com/example/Greeter</c>), or null.</summary>
    public JvmType? FindType(string internalName) => _byInternalName.GetValueOrDefault(internalName);

    /// <summary>
    /// Every reference and string literal in the archive's code. Built on first use — it decodes every method,
    /// which a session that only reads one class never needs.
    /// </summary>
    public JvmReferences References => _references.Value;

    private readonly Lazy<JvmReferences> _references;

    /// <summary>
    /// The class or member a JVM name in any of its spellings names: <c>java.lang.Runtime</c>,
    /// <c>java/lang/Runtime</c>, <c>java.util.Map.Entry</c> (tried as <c>java/util/Map$Entry</c>), or any of
    /// those followed by <c>::member</c> or <c>.member</c>. Only classes some instruction here names are found,
    /// since those are the only ones a question about references can be about.
    /// </summary>
    public (string Owner, string? Member)? ReferencedName(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string trimmed = text.Trim();
        int paren = trimmed.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
        {
            trimmed = trimmed[..paren];
        }

        var owners = References.Owners;
        int mark = trimmed.IndexOf("::", StringComparison.Ordinal);
        if (mark > 0)
        {
            return Owner(trimmed[..mark], owners) is { } owner ? (owner, trimmed[(mark + 2)..].Trim()) : null;
        }

        if (Owner(trimmed, owners) is { } whole)
        {
            return (whole, null);
        }

        int dot = trimmed.LastIndexOfAny(['.', '/']);
        return dot > 0 && Owner(trimmed[..dot], owners) is { } split ? (split, trimmed[(dot + 1)..]) : null;
    }

    /// <summary>A class name written with dots or slashes, tried with each later dot as a nesting <c>$</c>.</summary>
    private static string? Owner(string name, IReadOnlyCollection<string> owners)
    {
        string slashed = name.Replace('.', '/');
        var known = owners as ICollection<string> ?? owners.ToList();
        if (known.Contains(slashed))
        {
            return slashed;
        }

        var chars = slashed.ToCharArray();
        for (int i = chars.Length - 1; i >= 0; i--)
        {
            if (chars[i] == '/')
            {
                chars[i] = '$';
                if (known.Contains(new string(chars)))
                {
                    return new string(chars);
                }
            }
        }

        return null;
    }

    public int TypeCount => _byInternalName.Count;

    public string Render(IBytecodeType type, IBytecodeMember? member, string view, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type is not JvmType jvmType || (member is not null and not JvmMember))
        {
            throw new ArgumentException("the type and member must come from this archive", nameof(type));
        }

        if (view == JavaView)
        {
            return member is JvmMember javaMember
                ? Java.JavaDecompiler.Member(this, jvmType, javaMember, cancellationToken)
                : Java.JavaDecompiler.Type(this, jvmType, cancellationToken);
        }

        if (view != BytecodeView)
        {
            throw new ArgumentException($"a JAR is read as {JavaView} or {BytecodeView}, not {view}", nameof(view));
        }

        var listing = new BytecodeListing(this);
        return member is JvmMember jvmMember
            ? listing.Member(jvmType, jvmMember, cancellationToken)
            : listing.Type(jvmType, cancellationToken);
    }

    /// <summary>
    /// <c>com/example/Greeter</c> for a class; <c>com/example/Greeter.greet(Ljava/lang/String;)V</c> for a method;
    /// <c>com/example/Greeter.name:Ljava/lang/String;</c> for a field. Internal names and descriptors, because they
    /// are what the class file itself says, survive a rebuild of the same code, and tell overloads apart.
    /// </summary>
    public string? AnnotationKey(IBytecodeType type, IBytecodeMember? member) => (type, member) switch
    {
        (JvmType t, null) => t.File.Name,
        (JvmType t, JvmMember { Method: { } m }) => $"{t.File.Name}.{m.Name}{m.Descriptor}",
        (JvmType t, JvmMember { Field: { } f }) => $"{t.File.Name}.{f.Name}:{f.Descriptor}",
        _ => null,
    };

    /// <summary>
    /// The class a class is declared in, from what it says about itself: its own <c>InnerClasses</c> row names its
    /// outer class when it is a member, and a local or anonymous class's <c>EnclosingMethod</c> names the class
    /// whose code declares it. A name that is not in the archive, or a chain that loops back — which only a crafted
    /// file has — leaves it top-level.
    /// </summary>
    private (JvmType Outer, InnerClassEntry? Row)? OuterOf(JvmType type)
    {
        var row = type.File.InnerClasses.FirstOrDefault(r => r.Inner == type.File.Name);
        string? outerName = row?.Outer ?? (row is not null ? type.File.EnclosingClass : null);
        if (outerName is null || outerName == type.File.Name || !_byInternalName.TryGetValue(outerName, out var outer))
        {
            return null;
        }

        // Walk up: a loop would make the tree infinite.
        var seen = new HashSet<string>(StringComparer.Ordinal) { type.File.Name };
        for (var at = outer; at is not null;)
        {
            if (!seen.Add(at.File.Name))
            {
                return null;
            }

            var up = at.File.InnerClasses.FirstOrDefault(r => r.Inner == at.File.Name);
            string? next = up?.Outer ?? (up is not null ? at.File.EnclosingClass : null);
            at = next is not null && _byInternalName.TryGetValue(next, out var parent) ? parent : null;
        }

        return (outer, row);
    }

    public override string ToString() => $"{FullName} (JVM)";
}
