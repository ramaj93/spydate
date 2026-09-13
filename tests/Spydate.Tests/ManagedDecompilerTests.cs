using Spydate.Core.PE;
using Spydate.Decompiler.Managed;

namespace Spydate.Tests;

public class ManagedDecompilerTests
{
    private static string CoreAssemblyPath => typeof(PeImage).Assembly.Location;

    [Fact]
    public void LoadsNamespacesAndTypes()
    {
        using var asm = ManagedAssembly.Load(CoreAssemblyPath);

        Assert.Contains(asm.Namespaces, ns => ns.Name == "Spydate.Core.PE");
        var ns = asm.Namespaces.First(n => n.Name == "Spydate.Core.PE");
        var peImage = ns.Types.FirstOrDefault(t => t.Name == "PeImage");
        Assert.NotNull(peImage);
        Assert.Contains(peImage!.Members, m => m.Name == "Load" && m.Kind == ManagedMemberKind.Method);
        Assert.NotEmpty(asm.AssemblyReferences);
        Assert.Contains("v10.0", asm.TargetFramework, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReferencesResolveToAssembliesWeCanWalkInto()
    {
        using var asm = ManagedAssembly.Load(CoreAssemblyPath);
        Assert.NotEmpty(asm.References);

        // The runtime this test runs on is on disk, so at least one reference — a core one like
        // System.Private.CoreLib — resolves to an assembly whose own types can be read. That is what
        // lets the explorer append a reference's namespaces and members.
        var walkable = asm.References
            .Select(r => asm.Resolve(r))
            .FirstOrDefault(a => a is { Namespaces.Count: > 0 });

        Assert.NotNull(walkable);
        Assert.NotNull(walkable!.Path);
        Assert.Contains(walkable.Namespaces, n => n.Types.Count > 0);
    }

    [Fact]
    public void ResolvingAReferenceIsCachedAndOwned()
    {
        using var asm = ManagedAssembly.Load(CoreAssemblyPath);
        var reference = asm.References[0];

        var first = asm.Resolve(reference);
        var second = asm.Resolve(reference);

        // The same instance, so a reference is loaded once and disposed with the assembly that owns
        // it rather than reloaded on every expansion.
        Assert.Same(first, second);
    }

    [Fact]
    public void ATypeInAResolvedReferenceDecompilesThroughThatReference()
    {
        using var asm = ManagedAssembly.Load(CoreAssemblyPath);
        var resolved = asm.References
            .Select(r => asm.Resolve(r))
            .FirstOrDefault(a => a is { Namespaces.Count: > 0 });
        Assert.NotNull(resolved);

        // A real type in the reference — decompiled through the reference's own assembly, which is
        // exactly what opening it from the explorer (go-to-definition) does. Through the opened
        // assembly instead it would find nothing.
        var type = resolved!.Namespaces.SelectMany(n => n.Types).First(t => t.Members.Count > 0);
        string cs = resolved.Decompiler.DecompileType(type);

        Assert.NotEmpty(cs);
        Assert.Contains(type.Name, cs, StringComparison.Ordinal);
    }

    [Fact]
    public void DecompilesTypeToCSharp()
    {
        using var asm = ManagedAssembly.Load(CoreAssemblyPath);
        var type = asm.Namespaces.First(n => n.Name == "Spydate.Core.PE").Types.First(t => t.Name == "PeParseException");

        string cs = asm.Decompiler.DecompileType(type);

        Assert.Contains("class PeParseException", cs);
        Assert.Contains("Exception", cs);
    }

    [Fact]
    public void DecompilesMethodToCSharpAndIl()
    {
        using var asm = ManagedAssembly.Load(CoreAssemblyPath);
        var type = asm.Namespaces.First(n => n.Name == "Spydate.Core.PE").Types.First(t => t.Name == "PeImage");
        var method = type.Members.First(m => m.Name == "LooksLikePe");

        string cs = asm.Decompiler.DecompileMember(method);
        string il = asm.Decompiler.DisassembleMember(method);

        Assert.Contains("LooksLikePe", cs);
        Assert.Contains(".method", il);
        Assert.Contains("IL_0000", il);
    }

    [Fact]
    public void ModuleHeaderIl()
    {
        using var asm = ManagedAssembly.Load(CoreAssemblyPath);

        string header = asm.Decompiler.DisassembleModuleHeader();

        Assert.Contains(".assembly", header);
        Assert.Contains(".module", header);
    }

    /// <summary>
    /// Two documents decompiling the same assembly at once do not spoil each other's work.
    ///
    /// The window opens each document's text on a thread of its own and they all share one
    /// decompiler, so a type and a member opened together are two threads inside one ILSpy object.
    /// It is not thread-safe, and the failure is a NullReferenceException from somewhere inside it,
    /// which the document shows as "Decompilation failed" - on a method that is perfectly fine and
    /// decompiles on the next try. That is what browsing a real application looked like: every few
    /// tabs, a method that had no source.
    ///
    /// Every path that touches the shared decompiler is in here, because they all took it in turns
    /// to be the one that did not take its turn.
    /// </summary>
    [Fact]
    public async Task ManyThreadsDecompilingAtOnceAllGetTheirText()
    {
        using var asm = ManagedAssembly.Load(CoreAssemblyPath);
        var type = asm.Namespaces.First(n => n.Name == "Spydate.Core.PE").Types.First(t => t.Name == "PeImage");
        var method = type.Members.First(m => m.Name == "LooksLikePe");
        var handle = (System.Reflection.Metadata.MethodDefinitionHandle)method.Handle;

        var work = new List<Func<object>>
        {
            () => asm.Decompiler.DecompileType(type),
            () => asm.Decompiler.DecompileMember(method),
            () => asm.Decompiler.SourceForType(type),
            () => asm.Decompiler.SourceForMember(method),
            () => asm.Decompiler.StatementsFor(handle),
        };

        // Four rounds of all five at once. One round passes often enough to be no evidence at all.
        var running = Enumerable.Range(0, 4)
            .SelectMany(_ => work)
            .Select(one => Task.Run(one))
            .ToArray();

        object[] produced = await Task.WhenAll(running);

        Assert.All(produced, Assert.NotNull);
        Assert.Equal(20, produced.Length);
    }
}
