using Spydate.Core.Strings;

namespace Spydate.Disassembly;

/// <summary>
/// Address lookup over scanned strings. References routinely point into the middle of a literal
/// (<c>lea rcx, [str+4]</c>), so lookups match the whole range a string occupies, not just its start.
/// </summary>
public sealed class StringIndex
{
    private readonly ulong[] _starts;
    private readonly ulong[] _ends;
    private readonly FoundString[] _strings;
    private readonly int[] _sourceIndex;

    private StringIndex(ulong[] starts, ulong[] ends, FoundString[] strings, int[] sourceIndex)
    {
        _starts = starts;
        _ends = ends;
        _strings = strings;
        _sourceIndex = sourceIndex;
    }

    public static StringIndex Empty { get; } =
        new(Array.Empty<ulong>(), Array.Empty<ulong>(), Array.Empty<FoundString>(), Array.Empty<int>());

    public int Count => _strings.Length;

    /// <summary>Indexes the mapped strings; ones with no virtual address are skipped.</summary>
    public static StringIndex Build(IReadOnlyList<FoundString> strings)
    {
        ArgumentNullException.ThrowIfNull(strings);

        var mapped = strings
            .Select((s, i) => (String: s, Source: i))
            .Where(e => e.String.Va is not null)
            .OrderBy(e => e.String.Va!.Value)
            .ToArray();

        var starts = new ulong[mapped.Length];
        var ends = new ulong[mapped.Length];
        var entries = new FoundString[mapped.Length];
        var sources = new int[mapped.Length];
        for (int i = 0; i < mapped.Length; i++)
        {
            starts[i] = mapped[i].String.Va!.Value;
            ends[i] = starts[i] + (ulong)ByteLength(mapped[i].String);
            entries[i] = mapped[i].String;
            sources[i] = mapped[i].Source;
        }

        return new StringIndex(starts, ends, entries, sources);
    }

    /// <summary>Bytes the text occupies: UTF-16 characters are two bytes wide.</summary>
    public static int ByteLength(FoundString s) => s.Text.Length * (s.Encoding == StringEncodingKind.Utf16 ? 2 : 1);

    /// <summary>The string covering <paramref name="va"/>, or null.</summary>
    public FoundString? Find(ulong va) => IndexOf(va) is { } i ? _strings[i] : null;

    /// <summary>Position of the covering string in the sorted index, or null.</summary>
    public int? IndexOf(ulong va)
    {
        if (_starts.Length == 0)
        {
            return null;
        }

        int slot = Array.BinarySearch(_starts, va);
        if (slot < 0)
        {
            slot = ~slot - 1; // last string starting at or before va
        }

        if (slot < 0)
        {
            return null;
        }

        // Several strings can share a start address (an ASCII hit and a UTF-16 hit overlap), so
        // check every candidate with that start before giving up.
        for (int i = slot; i >= 0 && _starts[i] == _starts[slot]; i--)
        {
            if (va < _ends[i])
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>The indexed strings, ordered by address.</summary>
    public IReadOnlyList<FoundString> Strings => _strings;

    /// <summary>Position of the entry at <paramref name="slot"/> in the list it was built from.</summary>
    public int SourceIndexAt(int slot) => _sourceIndex[slot];
}
