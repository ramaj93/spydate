using System.Globalization;
using System.Reflection.Metadata;
using System.Text;
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

    /// <summary>
    /// ILSpy's listing with a file address against every instruction.
    ///
    /// The offsets ILSpy prints — <c>IL_0007</c> — are distances into a method body, and nothing
    /// outside that method can use one. A patch names bytes of a file, the breakpoint gutter reads
    /// an address off the start of a line, and <c>Targets.Resolve</c> takes hex: all three want the
    /// same thing and none of them can work out where <c>IL_0007</c> is. Prefixing the address
    /// rather than replacing the label keeps the listing exactly as readable as it was, with the
    /// tokens still resolved by ILSpy, and makes every line of it addressable.
    ///
    /// One member at a time, deliberately. A whole type's listing restarts its offsets at every
    /// method, and deciding which body a line belongs to means parsing ILSpy's own output for
    /// <c>.method</c> headers — a guess about a format that is not a contract, in the one place
    /// where being one method out would place a patch in someone else's code.
    /// </summary>
    public static string Addressed(string listing, ManagedBody body, ulong imageBase, bool wide)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(body);

        var sb = new StringBuilder();
        string blank = new(' ', wide ? 18 : 10);

        foreach (string line in listing.Split('\n'))
        {
            string text = line.TrimEnd('\r');
            if (Label(text) is { } offset && offset < body.Il.Length)
            {
                ulong va = imageBase + body.RvaOf(offset);
                sb.Append(wide ? $"{va:X16}  " : $"{va:X8}  ").Append(text.TrimStart()).Append('\n');
            }
            else
            {
                // Everything else keeps its shape, indented past the address column so the
                // instructions still line up under the declaration they belong to.
                sb.Append(blank).Append(text).Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>The IL offset a line is labelled with, or null when it is not an instruction line.</summary>
    private static int? Label(string line)
    {
        string text = line.TrimStart();
        if (!text.StartsWith("IL_", StringComparison.Ordinal) || text.Length < 8 || text[7] != ':')
        {
            return null;
        }

        return int.TryParse(text.AsSpan(3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int offset)
            ? offset
            : null;
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
