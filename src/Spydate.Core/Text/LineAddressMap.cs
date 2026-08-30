namespace Spydate.Core.Text;

/// <summary>
/// Which line of a listing is about which address, both ways round. This is what lets two views of the
/// same function follow each other: a disassembly line states its address at the start, a pseudo-C line
/// states it in the trailing comment, and <see cref="AddressText.FromLine"/> reads either.
///
/// The reverse lookup is deliberately not exact. Most instructions leave no line of their own in
/// decompiled output - the passes fold them away - so asking for one lands on the last line at or before
/// it, which is the statement that instruction ended up inside.
/// </summary>
public sealed class LineAddressMap
{
    private readonly ulong[] _addresses;   // sorted, one entry per line that states an address
    private readonly int[] _lines;         // 1-based line numbers, parallel to _addresses
    private readonly Dictionary<int, ulong> _byLine;

    private LineAddressMap(ulong[] addresses, int[] lines, Dictionary<int, ulong> byLine)
    {
        _addresses = addresses;
        _lines = lines;
        _byLine = byLine;
    }

    public static LineAddressMap Empty { get; } = new(Array.Empty<ulong>(), Array.Empty<int>(), new Dictionary<int, ulong>());

    /// <summary>Number of lines that state an address.</summary>
    public int Count => _addresses.Length;

    public static LineAddressMap Build(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Empty;
        }

        var byLine = new Dictionary<int, ulong>();
        var pairs = new List<(ulong Address, int Line)>();
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (AddressText.FromLine(lines[i].TrimEnd('\r')) is not { } address)
            {
                continue;
            }

            int lineNumber = i + 1;
            byLine[lineNumber] = address;
            pairs.Add((address, lineNumber));
        }

        // Sorted by address for the nearest-preceding search; ties keep the first line, which is the
        // one the reader would call "where that address is".
        pairs.Sort((a, b) => a.Address != b.Address ? a.Address.CompareTo(b.Address) : a.Line.CompareTo(b.Line));

        var addresses = new List<ulong>(pairs.Count);
        var lineNumbers = new List<int>(pairs.Count);
        foreach (var (address, line) in pairs)
        {
            if (addresses.Count > 0 && addresses[^1] == address)
            {
                continue;   // one line per address; the first one wins
            }

            addresses.Add(address);
            lineNumbers.Add(line);
        }

        return new LineAddressMap(addresses.ToArray(), lineNumbers.ToArray(), byLine);
    }

    /// <summary>The address a line is about, if it states one.</summary>
    public ulong? AddressAt(int line) => _byLine.TryGetValue(line, out ulong address) ? address : null;

    /// <summary>
    /// The line for an address: the one that states it, or the last line before it when it has none of
    /// its own. Null when the address is before anything in this text.
    /// </summary>
    public int? LineFor(ulong address)
    {
        if (_addresses.Length == 0)
        {
            return null;
        }

        int slot = Array.BinarySearch(_addresses, address);
        if (slot >= 0)
        {
            return _lines[slot];
        }

        slot = ~slot - 1;   // the last entry at or before the address
        return slot < 0 ? null : _lines[slot];
    }

    /// <summary>True when the address falls inside the span this text covers.</summary>
    public bool Covers(ulong address) => _addresses.Length > 0 && address >= _addresses[0] && address <= _addresses[^1];
}
