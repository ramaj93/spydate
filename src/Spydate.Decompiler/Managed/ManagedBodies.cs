using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace Spydate.Decompiler.Managed;

/// <summary>
/// One method's IL, and where in the file it actually sits.
///
/// The RVA is the point of this type. Metadata addresses a method body, and a listing addresses an
/// instruction inside it by an offset from the body's first IL byte — but a patch is a change to
/// bytes of a file, and the only thing that can name those is an RVA. Carrying both is what lets an
/// IL instruction be patched by exactly the machinery that patches an x86 one.
/// </summary>
public sealed record ManagedBody(MethodDefinitionHandle Method, uint BodyRva, uint IlRva, ImmutableArray<byte> Il)
{
    /// <summary>Past the last IL byte. The exception handler table, when there is one, starts here.</summary>
    public uint IlEndRva => IlRva + (uint)Il.Length;

    public bool Covers(uint rva) => rva >= IlRva && rva < IlEndRva;

    /// <summary>The offset a listing would print for this RVA.</summary>
    public int OffsetOf(uint rva) => (int)(rva - IlRva);

    public uint RvaOf(int offset) => IlRva + (uint)offset;
}

/// <summary>
/// Every method body in an assembly, by handle and by address.
///
/// Built once and held, because the address lookup is what every IL patch starts with: a target
/// arrives as a number read off a listing, and the first question is which method's code it is in.
/// </summary>
public sealed class ManagedBodies
{
    /// <summary>A fat header, per ECMA-335: two bytes of flags whose top nibble is its size in dwords.</summary>
    private const int Fat = 3;

    /// <summary>A tiny header is one byte, holding the code size in its top six bits.</summary>
    private const int Tiny = 2;

    private readonly List<ManagedBody> _byRva = new();
    private readonly Dictionary<MethodDefinitionHandle, ManagedBody> _byMethod = new();

    private ManagedBodies()
    {
    }

    /// <summary>Every body that could be read, in address order.</summary>
    public IReadOnlyList<ManagedBody> All => _byRva;

    /// <summary>Bodies whose header or content could not be read, which is a fact about the file.</summary>
    public int Unreadable { get; private set; }

    public static ManagedBodies Build(ManagedAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var bodies = new ManagedBodies();
        var reader = assembly.Metadata;
        var pe = assembly.Module.Reader;

        foreach (var handle in reader.MethodDefinitions)
        {
            int rva;
            try
            {
                rva = reader.GetMethodDefinition(handle).RelativeVirtualAddress;
            }
            catch (BadImageFormatException)
            {
                bodies.Unreadable++;
                continue;
            }

            if (rva == 0)
            {
                continue;   // abstract, extern or a P/Invoke: no body, and that is not a fault
            }

            try
            {
                if (Header(pe, rva) is not { } header)
                {
                    bodies.Unreadable++;
                    continue;
                }

                var il = pe.GetMethodBody(rva).GetILContent();
                var body = new ManagedBody(handle, (uint)rva, (uint)(rva + header), il);
                bodies._byRva.Add(body);
                bodies._byMethod[handle] = body;
            }
            catch (Exception ex) when (ex is BadImageFormatException or ArgumentOutOfRangeException or InvalidOperationException)
            {
                bodies.Unreadable++;
            }
        }

        bodies._byRva.Sort((a, b) => a.IlRva.CompareTo(b.IlRva));
        return bodies;
    }

    /// <summary>The body whose IL covers an RVA, or null when no method's code is there.</summary>
    public ManagedBody? At(uint rva)
    {
        int low = 0;
        int high = _byRva.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            var body = _byRva[middle];
            if (body.Covers(rva))
            {
                return body;
            }

            if (rva < body.IlRva)
            {
                high = middle - 1;
            }
            else
            {
                low = middle + 1;
            }
        }

        return null;
    }

    /// <summary>One method's body, or null when it has none.</summary>
    public ManagedBody? Of(EntityHandle method)
        => method.Kind == HandleKind.MethodDefinition && _byMethod.TryGetValue((MethodDefinitionHandle)method, out var body)
            ? body
            : null;

    /// <summary>
    /// How many bytes of header sit in front of a body's IL, or null when it is not a body at all.
    ///
    /// Read here rather than taken from <c>MethodBodyBlock</c>, which hands back the IL without
    /// saying where it began. The distance is the whole question: it is what turns a listing offset
    /// into a file address, and being one byte out would place every patch one byte out.
    /// </summary>
    private static int? Header(System.Reflection.PortableExecutable.PEReader pe, int rva)
    {
        var section = pe.GetSectionData(rva);
        if (section.Length < 2)
        {
            return null;
        }

        var head = section.GetContent(0, 2);
        int kind = head[0] & 3;

        if (kind == Tiny)
        {
            return 1;
        }

        if (kind != Fat)
        {
            return null;
        }

        // The size is in the top nibble of the two-byte flags, counted in four-byte words. Three is
        // the only value anything emits, but it is read rather than assumed: a file is free to say
        // something else, and believing three would put the IL start inside the header.
        int words = head[1] >> 4;
        return words < 3 ? null : words * 4;
    }
}
