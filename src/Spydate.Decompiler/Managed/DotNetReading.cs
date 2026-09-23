using Spydate.Core.Readings;

namespace Spydate.Decompiler.Managed;

/// <summary>
/// The .NET reading of a PE, as an <see cref="IBytecodeReading"/>: the <see cref="ManagedAssembly"/> it
/// wraps, unchanged, answering the questions every bytecode reading answers. Its types and members are the
/// assembly's own <see cref="ManagedType"/> and <see cref="ManagedMember"/> records, which implement the seam's
/// interfaces directly — so a caller that needs the .NET specifics (a metadata handle, a method body's address)
/// casts what the seam handed it back, and nothing is copied or wrapped twice.
///
/// Everything only .NET has — method bodies at file addresses, IL patching, P/Invokes, resolving referenced
/// assemblies, the C# decompiler's statement map — stays on <see cref="Assembly"/>.
/// </summary>
public sealed class DotNetReading : IBytecodeReading
{
    public const string CSharpView = "csharp";

    public const string IlView = "il";

    public DotNetReading(ManagedAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        Assembly = assembly;
    }

    public ManagedAssembly Assembly { get; }

    public BytecodeKind Kind => BytecodeKind.DotNet;

    public string Name => Assembly.SimpleName;

    public string FullName => Assembly.FullName;

    public string Platform => Assembly.TargetFramework;

    public string FormatVersion => Assembly.RuntimeVersion;

    public string Noun => "assembly";

    public IReadOnlyList<IBytecodeNamespace> Namespaces => Assembly.Namespaces;

    public IReadOnlyList<string> Requires => Assembly.AssemblyReferences;

    public IBytecodeMember? EntryPoint => Assembly.EntryPoint;

    public IReadOnlyList<string> Views { get; } = [CSharpView, IlView];

    public string Render(IBytecodeType type, IBytecodeMember? member, string view, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type is not ManagedType managedType || (member is not null and not ManagedMember))
        {
            throw new ArgumentException("the type and member must come from this assembly", nameof(type));
        }

        var decompiler = Assembly.Decompiler;
        return (view, member as ManagedMember) switch
        {
            (CSharpView, { } m) => decompiler.DecompileMember(m, cancellationToken),
            (CSharpView, null) => decompiler.DecompileType(managedType, cancellationToken),
            (IlView, { } m) => decompiler.DisassembleMember(m, cancellationToken),
            (IlView, null) => decompiler.DisassembleType(managedType, cancellationToken),
            _ => throw new ArgumentException($"a .NET assembly is read as {string.Join(" or ", Views)}, not {view}", nameof(view)),
        };
    }

    /// <summary>Always null: a .NET method is annotated at the address its IL begins, like any other code.</summary>
    public string? AnnotationKey(IBytecodeType type, IBytecodeMember? member) => null;

    public override string ToString() => $"{FullName} (.NET)";
}
