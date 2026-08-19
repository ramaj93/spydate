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
}
