using Spydate.Core.Strings;

namespace Spydate.Disassembly;

/// <summary>A found string together with every code site that refers to it.</summary>
public sealed record StringXrefs(FoundString String, IReadOnlyList<Xref> References)
{
    public int Count => References.Count;

    /// <summary>First byte of the string in virtual memory. 0 when the string is not mapped.</summary>
    public ulong StartVa => String.Va ?? 0;

    /// <summary>One past the last byte, so interior references can be attributed to the string.</summary>
    public ulong EndVa => StartVa + (ulong)StringIndex.ByteLength(String);
}

/// <summary>
/// Joins scanned strings to the cross-reference index: which code loads this text?
/// Attribution is by range (see <see cref="StringIndex"/>), because compilers routinely point at
/// the middle of a literal.
/// </summary>
public static class StringReferences
{
    /// <summary>
    /// Returns one entry per input string, in the same order, each carrying the references that
    /// land inside it. Strings that are not mapped into memory never have references.
    /// </summary>
    public static IReadOnlyList<StringXrefs> Resolve(IReadOnlyList<FoundString> strings, XrefTable xrefs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(xrefs);

        var index = StringIndex.Build(strings);
        var buckets = new List<Xref>?[strings.Count];

        foreach (var xref in xrefs.All())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index.IndexOf(xref.ToVa) is { } slot)
            {
                (buckets[index.SourceIndexAt(slot)] ??= new List<Xref>(1)).Add(xref);
            }
        }

        var result = new StringXrefs[strings.Count];
        for (int i = 0; i < strings.Count; i++)
        {
            var list = buckets[i];
            list?.Sort(static (a, b) => a.FromVa.CompareTo(b.FromVa));
            result[i] = new StringXrefs(strings[i], (IReadOnlyList<Xref>?)list ?? Array.Empty<Xref>());
        }

        return result;
    }
}
