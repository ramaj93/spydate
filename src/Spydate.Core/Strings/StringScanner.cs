using System.Text;
using Spydate.Core.PE;

namespace Spydate.Core.Strings;

/// <summary>How a string was encoded in the file.</summary>
public enum StringEncodingKind
{
    /// <summary>Single-byte printable characters.</summary>
    Ascii,
    /// <summary>UTF-16 little endian (the Windows W APIs).</summary>
    Utf16,
}

/// <summary>A printable run found in the file, with both its file and virtual location.</summary>
public sealed record FoundString
{
    public required long Offset { get; init; }
    /// <summary>RVA when the offset is inside a mapped section, otherwise null (overlay, headers gap).</summary>
    public required uint? Rva { get; init; }
    /// <summary>VA when mapped, otherwise null.</summary>
    public required ulong? Va { get; init; }
    public required string Text { get; init; }
    public required StringEncodingKind Encoding { get; init; }
    /// <summary>Owning section name, or "(overlay)" / "(headers)".</summary>
    public required string Section { get; init; }
    /// <summary>Whether the run ended with a NUL, i.e. it is probably a real C string.</summary>
    public required bool NullTerminated { get; init; }

    public int Length => Text.Length;

    public override string ToString() => $"{Offset:X8} [{Encoding}] {Text}";
}

/// <summary>Tunables for <see cref="StringScanner"/>.</summary>
public sealed record StringScanOptions
{
    /// <summary>Shortest run to report. Below 4 the output is mostly noise.</summary>
    public int MinLength { get; init; } = 5;

    /// <summary>Longest run reported; longer runs are truncated and still reported.</summary>
    public int MaxLength { get; init; } = 1024;

    /// <summary>Upper bound on results, so a huge file cannot exhaust memory.</summary>
    public int MaxResults { get; init; } = 200_000;

    public bool ScanAscii { get; init; } = true;

    public bool ScanUtf16 { get; init; } = true;

    public static StringScanOptions Default { get; } = new();
}

/// <summary>
/// Finds printable ASCII and UTF-16LE runs in a PE file. Works on raw file bytes (so overlay data
/// is covered too) and maps each hit back to its RVA/VA when the offset lies in a section.
/// </summary>
public static class StringScanner
{
    public static IReadOnlyList<FoundString> Scan(PeImage image, StringScanOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var opts = options ?? StringScanOptions.Default;
        var data = image.Data.Span;
        var results = new List<FoundString>();

        if (opts.ScanAscii)
        {
            ScanAsciiRuns(image, data, opts, results, cancellationToken);
        }

        if (opts.ScanUtf16)
        {
            // Both parities: packed and obfuscated binaries do put wide strings at odd offsets.
            for (int parity = 0; parity < 2 && results.Count < opts.MaxResults; parity++)
            {
                ScanUtf16Runs(image, data, opts, results, cancellationToken, parity);
            }
        }

        results.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
        return results;
    }

    private static void ScanAsciiRuns(PeImage image, ReadOnlySpan<byte> data, StringScanOptions opts, List<FoundString> results, CancellationToken token)
    {
        int start = -1;
        for (int i = 0; i <= data.Length; i++)
        {
            bool printable = i < data.Length && IsPrintable(data[i]);
            if (printable)
            {
                if (start < 0)
                {
                    start = i;
                }

                continue;
            }

            if (start >= 0)
            {
                int length = i - start;
                if (length >= opts.MinLength)
                {
                    string text = Encoding.ASCII.GetString(data.Slice(start, Math.Min(length, opts.MaxLength)));
                    results.Add(Create(image, start, text, StringEncodingKind.Ascii, i < data.Length && data[i] == 0));
                    if (results.Count >= opts.MaxResults)
                    {
                        return;
                    }

                    if ((results.Count & 0x3FF) == 0)
                    {
                        token.ThrowIfCancellationRequested();
                    }
                }

                start = -1;
            }
        }
    }

    private static void ScanUtf16Runs(PeImage image, ReadOnlySpan<byte> data, StringScanOptions opts, List<FoundString> results, CancellationToken token, int startOffset)
    {
        // Only the common case: printable ASCII characters widened to 16 bits, little endian.
        int start = -1;
        for (int i = startOffset; i + 1 <= data.Length; i += 2)
        {
            bool printable = i + 1 < data.Length && IsPrintable(data[i]) && data[i + 1] == 0;
            if (printable)
            {
                if (start < 0)
                {
                    start = i;
                }

                continue;
            }

            if (start >= 0)
            {
                int chars = (i - start) / 2;
                if (chars >= opts.MinLength)
                {
                    int byteCount = Math.Min(chars, opts.MaxLength) * 2;
                    string text = Encoding.Unicode.GetString(data.Slice(start, byteCount));
                    bool terminated = i + 1 < data.Length && data[i] == 0 && data[i + 1] == 0;
                    results.Add(Create(image, start, text, StringEncodingKind.Utf16, terminated));
                    if (results.Count >= opts.MaxResults)
                    {
                        return;
                    }

                    if ((results.Count & 0x3FF) == 0)
                    {
                        token.ThrowIfCancellationRequested();
                    }
                }

                start = -1;
            }
        }
    }

    private static FoundString Create(PeImage image, int offset, string text, StringEncodingKind encoding, bool nullTerminated)
    {
        uint? rva = image.OffsetToRva((uint)offset);
        string section = rva is { } r
            ? image.SectionFromRva(r)?.Name ?? "(headers)"
            : offset >= image.Overlay.Offset && image.Overlay.Length > 0 ? "(overlay)" : "(unmapped)";

        return new FoundString
        {
            Offset = offset,
            Rva = rva,
            Va = rva is { } v ? image.RvaToVa(v) : null,
            Text = text,
            Encoding = encoding,
            Section = section,
            NullTerminated = nullTerminated,
        };
    }

    private static bool IsPrintable(byte b) => b is >= 0x20 and <= 0x7E;
}
