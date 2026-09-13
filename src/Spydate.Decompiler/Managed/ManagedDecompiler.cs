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

    /// <summary>
    /// One decompilation at a time, because there is one decompiler.
    ///
    /// ILSpy's <c>CSharpDecompiler</c> is not thread-safe and this one is shared by every open
    /// document — and each document produces its text on a thread of its own, so opening a type and
    /// a member together is two threads inside the same object. The cancellation token alone is
    /// shared mutable state, before anything the decompiler does internally. It fails as a
    /// <c>NullReferenceException</c> from somewhere inside ILSpy, which the document catches and
    /// shows as "Decompilation failed" on a method that decompiles perfectly well when opened on
    /// its own — so it looks like a bad binary rather than a race, and it comes and goes.
    ///
    /// A lock rather than a decompiler each: building one means building a type system for the
    /// whole assembly, which for a real application is most of a second and several tens of
    /// megabytes. Waiting for the tab next door is cheaper than that, and it happens off the
    /// window's thread.
    /// </summary>
    private readonly Lock _oneAtATime = new();

    internal ManagedDecompiler(ManagedAssembly assembly) => _assembly = assembly;

    /// <summary>Runs something on the shared decompiler, alone, with the caller's cancellation on it.</summary>
    private T Decompiling<T>(CancellationToken cancellationToken, Func<ICSharpCode.Decompiler.CSharp.CSharpDecompiler, T> work)
    {
        lock (_oneAtATime)
        {
            var d = _assembly.CSharpDecompiler;
            d.CancellationToken = cancellationToken;
            return work(d);
        }
    }

    // ---------------- C# ----------------

    public string DecompileAssembly(CancellationToken cancellationToken = default)
        => Decompiling(cancellationToken, d => d.DecompileWholeModuleAsString());

    public string DecompileType(ManagedType type, CancellationToken cancellationToken = default)
        => Decompiling(cancellationToken, d => d.DecompileTypeAsString(new FullTypeName(type.Definition.ReflectionName)));

    public string DecompileMember(ManagedMember member, CancellationToken cancellationToken = default)
        => Decompiling(cancellationToken, d => d.DecompileAsString(member.Handle));

    /// <summary>
    /// C# for one member, with the IL offset behind each line of it.
    ///
    /// The pairing is what makes decompiled C# debuggable. A breakpoint is a method and an offset
    /// into its IL; a line of this text is something the decompiler invented out of that IL a moment
    /// ago and corresponds to no file anywhere. Only the decompiler can say which is which, and
    /// <see cref="ICSharpCode.Decompiler.CSharp.CSharpDecompiler.CreateSequencePoints"/> is it.
    /// </summary>
    public ManagedSource SourceForMember(ManagedMember member, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        return Decompiling(cancellationToken, d => SequencePoints.Of(d, d.Decompile(member.Handle), _assembly.Settings));
    }

    /// <summary>
    /// Where each statement of one method begins and ends, for a debugger that has only a token.
    ///
    /// The stop reports a method and an offset and nothing else, so this takes the same. It also
    /// works for a state machine's <c>MoveNext</c>, which is what an async method's stops are in:
    /// the decompiler is given that method and the ranges come back for the body it really has.
    /// </summary>
    public IReadOnlyList<SourceStatement> StatementsFor(MethodDefinitionHandle method, CancellationToken cancellationToken = default)
        => Decompiling(cancellationToken, d => SequencePoints.Statements(d, d.Decompile(method)));

    /// <summary>
    /// The names the decompiler gave a method's locals, by IL slot.
    ///
    /// Without symbols a local has no name anywhere in the binary — <c>V_0</c> is all the runtime can
    /// say — but the C# view beside the Locals pane calls it <c>flag</c> or <c>currentProcess</c>, and
    /// a pane that disagrees with the code makes the reader match the two up by type. The decompiler
    /// chose the names on screen, so it is the one to ask. A slot it removed or merged has no entry.
    /// </summary>
    public IReadOnlyDictionary<int, string> LocalNamesFor(MethodDefinitionHandle method, CancellationToken cancellationToken = default)
        => Decompiling(cancellationToken, d => SequencePoints.LocalNames(d, d.Decompile(method), method));

    /// <summary>C# for a whole type, with the IL offset behind each line — every method in it.</summary>
    public ManagedSource SourceForType(ManagedType type, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);

        return Decompiling(
            cancellationToken,
            d => SequencePoints.Of(d, d.DecompileType(new FullTypeName(type.Definition.ReflectionName)), _assembly.Settings));
    }

    /// <summary>
    /// The same C#, with each line's file address in a trailing comment.
    ///
    /// Written into the text rather than carried beside it because that is how every other view
    /// here works: the breakpoint margin, the caret's address and the execution arrow all read the
    /// address back off the line. Pseudo-C has done it this way since before there was a debugger.
    /// </summary>
    public static string Addressed(ManagedSource source, ManagedBodies bodies, ulong imageBase)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(bodies);

        return SequencePoints.Addressed(source, bodies, imageBase);
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
