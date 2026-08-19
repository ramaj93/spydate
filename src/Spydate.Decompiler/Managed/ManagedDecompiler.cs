using System.Reflection.Metadata;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.TypeSystem;

namespace Spydate.Decompiler.Managed;

/// <summary>C# and IL output for a <see cref="ManagedAssembly"/> (ILSpy engine).</summary>
public sealed class ManagedDecompiler
{
    private readonly ManagedAssembly _assembly;

    internal ManagedDecompiler(ManagedAssembly assembly) => _assembly = assembly;

    // ---------------- C# ----------------

    public string DecompileAssembly(CancellationToken cancellationToken = default)
    {
        var d = _assembly.CSharpDecompiler;
        d.CancellationToken = cancellationToken;
        return d.DecompileWholeModuleAsString();
    }

    public string DecompileType(ManagedType type, CancellationToken cancellationToken = default)
    {
        var d = _assembly.CSharpDecompiler;
        d.CancellationToken = cancellationToken;
        return d.DecompileTypeAsString(new FullTypeName(type.Definition.ReflectionName));
    }

    public string DecompileMember(ManagedMember member, CancellationToken cancellationToken = default)
    {
        var d = _assembly.CSharpDecompiler;
        d.CancellationToken = cancellationToken;
        return d.DecompileAsString(member.Handle);
    }

    // ---------------- IL ----------------

    public string DisassembleModuleHeader(CancellationToken cancellationToken = default)
    {
        var output = new PlainTextOutput();
        var dis = new ReflectionDisassembler(output, cancellationToken);
        dis.WriteAssemblyReferences(_assembly.Metadata);
        dis.WriteAssemblyHeader(_assembly.Module);
        output.WriteLine();
        dis.WriteModuleHeader(_assembly.Module);
        return output.ToString();
    }

    public string DisassembleType(ManagedType type, CancellationToken cancellationToken = default)
    {
        var output = new PlainTextOutput();
        var dis = new ReflectionDisassembler(output, cancellationToken);
        dis.DisassembleType(_assembly.Module, (TypeDefinitionHandle)type.Handle);
        return output.ToString();
    }

    public string DisassembleMember(ManagedMember member, CancellationToken cancellationToken = default)
    {
        var output = new PlainTextOutput();
        var dis = new ReflectionDisassembler(output, cancellationToken);
        switch (member.Handle.Kind)
        {
            case HandleKind.MethodDefinition:
                dis.DisassembleMethod(_assembly.Module, (MethodDefinitionHandle)member.Handle);
                break;
            case HandleKind.FieldDefinition:
                dis.DisassembleField(_assembly.Module, (FieldDefinitionHandle)member.Handle);
                break;
            case HandleKind.PropertyDefinition:
                dis.DisassembleProperty(_assembly.Module, (PropertyDefinitionHandle)member.Handle);
                break;
            case HandleKind.EventDefinition:
                dis.DisassembleEvent(_assembly.Module, (EventDefinitionHandle)member.Handle);
                break;
            default:
                output.WriteLine($"// unsupported handle kind {member.Handle.Kind}");
                break;
        }

        return output.ToString();
    }
}
