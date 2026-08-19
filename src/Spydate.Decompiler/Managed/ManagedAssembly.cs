using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace Spydate.Decompiler.Managed;

public enum ManagedMemberKind
{
    Method,
    Constructor,
    Field,
    Property,
    Event,
}

/// <summary>A member of a managed type (method, field, property, event).</summary>
public sealed record ManagedMember(string Name, string Signature, ManagedMemberKind Kind, EntityHandle Handle, IEntity Entity)
{
    public override string ToString() => Signature;
}

/// <summary>A managed type with its nested types and members.</summary>
public sealed record ManagedType(
    string Name,
    string FullName,
    TypeKind Kind,
    EntityHandle Handle,
    IReadOnlyList<ManagedType> NestedTypes,
    IReadOnlyList<ManagedMember> Members,
    ITypeDefinition Definition)
{
    public override string ToString() => FullName;
}

/// <summary>A namespace and its top-level types.</summary>
public sealed record ManagedNamespace(string Name, IReadOnlyList<ManagedType> Types)
{
    public string DisplayName => Name.Length == 0 ? "-" : Name;
    public override string ToString() => DisplayName;
}

/// <summary>
/// A loaded .NET assembly: metadata browsing (namespaces → types → members), plus a shared
/// <see cref="ManagedDecompiler"/> for C# and IL output. Wraps the ILSpy engine.
/// </summary>
public sealed class ManagedAssembly : IDisposable
{
    private readonly UniversalAssemblyResolver _resolver;
    private readonly Lazy<CSharpDecompiler> _csharp;
    private readonly Lazy<IReadOnlyList<ManagedNamespace>> _namespaces;

    private ManagedAssembly(PEFile module, string? path)
    {
        Module = module;
        Path = path;
        Metadata = module.Metadata;
        Name = module.Name;

        string? tfm = null;
        string? runtimePack = null;
        try
        {
            tfm = module.DetectTargetFrameworkId();
            runtimePack = module.DetectRuntimePack();
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
        {
            // Metadata without a TargetFramework attribute — resolver falls back to probing.
        }

        TargetFramework = string.IsNullOrEmpty(tfm) ? "(unknown)" : tfm;
        RuntimeVersion = Metadata.MetadataVersion;
        _resolver = new UniversalAssemblyResolver(path, throwOnError: false, targetFramework: tfm, runtimePack: runtimePack, PEStreamOptions.PrefetchMetadata);

        Settings = new DecompilerSettings(LanguageVersion.Latest)
        {
            ThrowOnAssemblyResolveErrors = false,
            ShowXmlDocumentation = false,
            UseDebugSymbols = true,
        };

        _csharp = new Lazy<CSharpDecompiler>(() => new CSharpDecompiler(Module, _resolver, Settings), LazyThreadSafetyMode.ExecutionAndPublication);
        _namespaces = new Lazy<IReadOnlyList<ManagedNamespace>>(BuildNamespaces, LazyThreadSafetyMode.ExecutionAndPublication);
        Decompiler = new ManagedDecompiler(this);
    }

    /// <summary>Loads a managed PE from disk.</summary>
    public static ManagedAssembly Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var module = new PEFile(path, PEStreamOptions.PrefetchEntireImage);
        return new ManagedAssembly(module, path);
    }

    /// <summary>Loads a managed PE from bytes (assembly resolution is limited to the runtime directory).</summary>
    public static ManagedAssembly FromBytes(ReadOnlyMemory<byte> data, string name)
    {
        var stream = new MemoryStream(data.ToArray(), writable: false);
        var module = new PEFile(name, stream, PEStreamOptions.PrefetchEntireImage);
        return new ManagedAssembly(module, null);
    }

    public PEFile Module { get; }
    public string? Path { get; }
    public MetadataReader Metadata { get; }
    public string Name { get; }
    public string TargetFramework { get; }
    public string RuntimeVersion { get; }
    public DecompilerSettings Settings { get; }
    public ManagedDecompiler Decompiler { get; }

    internal CSharpDecompiler CSharpDecompiler => _csharp.Value;

    /// <summary>Namespaces sorted by name, each with its top-level types sorted by name.</summary>
    public IReadOnlyList<ManagedNamespace> Namespaces => _namespaces.Value;

    /// <summary>Referenced assemblies (display names).</summary>
    public IReadOnlyList<string> AssemblyReferences =>
        Metadata.AssemblyReferences
            .Select(h => Metadata.GetAssemblyReference(h))
            .Select(r => $"{Metadata.GetString(r.Name)}, Version={r.Version}")
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Full name of the assembly including version and public key token.</summary>
    public string FullName
    {
        get
        {
            if (!Metadata.IsAssembly)
            {
                return Name;
            }

            var def = Metadata.GetAssemblyDefinition();
            return $"{Metadata.GetString(def.Name)}, Version={def.Version}";
        }
    }

    /// <summary>Entry point method, if any.</summary>
    public ManagedMember? EntryPoint
    {
        get
        {
            var ep = Module.Reader.PEHeaders.CorHeader?.EntryPointTokenOrRelativeVirtualAddress ?? 0;
            if (ep == 0)
            {
                return null;
            }

            var handle = MetadataTokens.EntityHandle(ep);
            if (handle.Kind != HandleKind.MethodDefinition)
            {
                return null;
            }

            var ts = CSharpDecompiler.TypeSystem;
            var method = ts.MainModule.GetDefinition((MethodDefinitionHandle)handle);
            return method is null ? null : ToMember(method);
        }
    }

    private IReadOnlyList<ManagedNamespace> BuildNamespaces()
    {
        var ts = CSharpDecompiler.TypeSystem;
        var groups = ts.MainModule.TopLevelTypeDefinitions
            .GroupBy(t => t.Namespace, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        var list = new List<ManagedNamespace>();
        foreach (var g in groups)
        {
            var types = g.OrderBy(t => t.Name, StringComparer.Ordinal).Select(ToType).ToList();
            list.Add(new ManagedNamespace(g.Key, types));
        }

        return list;
    }

    private static ManagedType ToType(ITypeDefinition t)
    {
        var nested = t.NestedTypes.OrderBy(n => n.Name, StringComparer.Ordinal).Select(ToType).ToList();
        var members = new List<ManagedMember>();
        members.AddRange(t.Fields.Select(ToMember));
        members.AddRange(t.Properties.Select(ToMember));
        members.AddRange(t.Events.Select(ToMember));
        members.AddRange(t.Methods.Where(m => !m.IsAccessor).Select(ToMember));
        return new ManagedType(t.Name, t.FullName, t.Kind, t.MetadataToken, nested, members, t);
    }

    private static ManagedMember ToMember(IEntity e)
    {
        return e switch
        {
            IMethod m => new ManagedMember(m.Name, MethodSignature(m), m.IsConstructor ? ManagedMemberKind.Constructor : ManagedMemberKind.Method, m.MetadataToken, m),
            IField f => new ManagedMember(f.Name, $"{f.Name} : {f.ReturnType.Name}", ManagedMemberKind.Field, f.MetadataToken, f),
            IProperty p => new ManagedMember(p.Name, $"{p.Name} : {p.ReturnType.Name}", ManagedMemberKind.Property, p.MetadataToken, p),
            IEvent ev => new ManagedMember(ev.Name, $"{ev.Name} : {ev.ReturnType.Name}", ManagedMemberKind.Event, ev.MetadataToken, ev),
            _ => new ManagedMember(e.Name, e.Name, ManagedMemberKind.Field, e.MetadataToken, e),
        };
    }

    private static string MethodSignature(IMethod m)
    {
        string name = m.IsConstructor ? m.DeclaringTypeDefinition?.Name ?? m.Name : m.Name;
        string args = string.Join(", ", m.Parameters.Select(p => p.Type.Name));
        return m.IsConstructor ? $"{name}({args})" : $"{name}({args}) : {m.ReturnType.Name}";
    }

    public void Dispose()
    {
        Module.Dispose();
    }
}
