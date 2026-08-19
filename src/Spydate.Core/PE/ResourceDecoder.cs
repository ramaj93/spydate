using System.Text;
using Spydate.Core.Binary;

namespace Spydate.Core.PE;

/// <summary>One localised block of name/value pairs from VS_VERSIONINFO.</summary>
public sealed record VersionStringTable(string LanguageCodePage, IReadOnlyList<KeyValuePair<string, string>> Strings)
{
    public string? this[string name] => Strings.FirstOrDefault(s => string.Equals(s.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>Decoded RT_VERSION resource: the fixed block plus the localised string tables.</summary>
public sealed record VersionInfo
{
    public Version? FileVersion { get; init; }
    public Version? ProductVersion { get; init; }
    public uint FileFlags { get; init; }
    public uint FileOs { get; init; }
    public uint FileType { get; init; }
    public required IReadOnlyList<VersionStringTable> StringTables { get; init; }

    /// <summary>Value from the first string table that defines it (usually the only one).</summary>
    public string? Get(string name) => StringTables.Select(t => t[name]).FirstOrDefault(v => !string.IsNullOrEmpty(v));

    public string? FileDescription => Get("FileDescription");
    public string? CompanyName => Get("CompanyName");
    public string? ProductName => Get("ProductName");
    public string? OriginalFilename => Get("OriginalFilename");

    public override string ToString() => $"{ProductName ?? FileDescription ?? "(unnamed)"} {FileVersion}";
}

/// <summary>One entry of an RT_STRING block.</summary>
public readonly record struct ResourceString(uint Id, string Text);

/// <summary>
/// Turns resource bytes into something readable: version blocks, manifests and string tables.
/// Every reader is bounds-checked and returns null rather than throwing — resource data is as
/// untrusted as the rest of the file.
/// </summary>
public static class ResourceDecoder
{
    private const uint FixedFileInfoSignature = 0xFEEF04BD;

    /// <summary>Bytes of a resource data entry, or empty when the RVA is not backed by file data.</summary>
    public static ReadOnlyMemory<byte> ReadData(PeImage image, ResourceNode leaf)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(leaf);
        return leaf.IsDirectory ? ReadOnlyMemory<byte>.Empty : image.ReadAtRva(leaf.DataRva, (int)leaf.DataSize);
    }

    /// <summary>Decodes an RT_VERSION resource. Returns null when the block is not a version block.</summary>
    public static VersionInfo? ReadVersionInfo(ReadOnlySpan<byte> data)
    {
        // VS_VERSIONINFO: wLength, wValueLength, wType, "VS_VERSION_INFO", pad, VS_FIXEDFILEINFO, children
        if (!TryReadNodeHeader(data, 0, out int valueLength, out string key, out int valueOffset) || key != "VS_VERSION_INFO")
        {
            return null;
        }

        Version? fileVersion = null;
        Version? productVersion = null;
        uint flags = 0, os = 0, type = 0;

        if (valueLength >= 52 && valueOffset + 52 <= data.Length)
        {
            var fixedInfo = new SpanReader(data, valueOffset);
            if (fixedInfo.ReadU32() == FixedFileInfoSignature)
            {
                fixedInfo.Skip(4); // struct version
                fileVersion = ReadVersion(ref fixedInfo);
                productVersion = ReadVersion(ref fixedInfo);
                fixedInfo.Skip(4); // flags mask
                flags = fixedInfo.ReadU32();
                os = fixedInfo.ReadU32();
                type = fixedInfo.ReadU32();
            }
        }

        var tables = new List<VersionStringTable>();
        int childrenStart = Align(valueOffset + valueLength);
        int end = Math.Min(ReadU16(data, 0), data.Length);

        foreach (int child in Children(data, childrenStart, end))
        {
            if (TryReadNodeHeader(data, child, out _, out string childKey, out int childValue) && childKey == "StringFileInfo")
            {
                ReadStringFileInfo(data, Align(childValue), child + ReadU16(data, child), tables);
            }
        }

        return new VersionInfo
        {
            FileVersion = fileVersion,
            ProductVersion = productVersion,
            FileFlags = flags,
            FileOs = os,
            FileType = type,
            StringTables = tables,
        };
    }

    /// <summary>
    /// Decodes an RT_STRING block. Each block holds 16 slots for ids
    /// <c>(blockId - 1) * 16 .. + 15</c>; empty slots are skipped.
    /// </summary>
    public static IReadOnlyList<ResourceString> ReadStringTable(ReadOnlySpan<byte> data, uint blockId)
    {
        var strings = new List<ResourceString>(16);
        uint firstId = blockId == 0 ? 0 : (blockId - 1) * 16;
        int offset = 0;

        for (int i = 0; i < 16 && offset + 2 <= data.Length; i++)
        {
            int length = ReadU16(data, offset);
            offset += 2;
            if (length == 0)
            {
                continue;
            }

            int bytes = Math.Min(length * 2, data.Length - offset);
            if (bytes <= 0)
            {
                break;
            }

            strings.Add(new ResourceString(firstId + (uint)i, Encoding.Unicode.GetString(data.Slice(offset, bytes))));
            offset += bytes;
        }

        return strings;
    }

    /// <summary>Decodes an RT_MANIFEST resource, which is UTF-8 XML (with or without a BOM).</summary>
    public static string ReadManifest(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            data = data[3..];
        }

        return Encoding.UTF8.GetString(data).TrimEnd('\0');
    }

    // ------------------------------------------------------------------
    // VS_VERSIONINFO plumbing: a tree of length-prefixed, 4-byte aligned nodes
    // ------------------------------------------------------------------

    private static void ReadStringFileInfo(ReadOnlySpan<byte> data, int start, int end, List<VersionStringTable> tables)
    {
        foreach (int tableOffset in Children(data, start, end))
        {
            if (!TryReadNodeHeader(data, tableOffset, out _, out string language, out int firstString))
            {
                continue;
            }

            var strings = new List<KeyValuePair<string, string>>();
            int tableEnd = Math.Min(tableOffset + ReadU16(data, tableOffset), data.Length);

            foreach (int stringOffset in Children(data, Align(firstString), tableEnd))
            {
                if (!TryReadNodeHeader(data, stringOffset, out int valueChars, out string name, out int valueOffset))
                {
                    continue;
                }

                valueOffset = Align(valueOffset);
                int bytes = Math.Min(valueChars * 2, data.Length - valueOffset);
                string value = bytes > 0 ? Encoding.Unicode.GetString(data.Slice(valueOffset, bytes)).TrimEnd('\0') : string.Empty;
                strings.Add(new KeyValuePair<string, string>(name, value));
            }

            tables.Add(new VersionStringTable(language, strings));
        }
    }

    /// <summary>Offsets of the length-prefixed children between <paramref name="start"/> and <paramref name="end"/>.</summary>
    private static IEnumerable<int> Children(ReadOnlySpan<byte> data, int start, int end)
    {
        // Collected eagerly: a span cannot cross an iterator boundary.
        var offsets = new List<int>();
        int offset = Align(start);
        end = Math.Min(end, data.Length);

        while (offset + 6 <= end && offsets.Count < 256)
        {
            int length = ReadU16(data, offset);
            if (length < 6)
            {
                break; // a zero-length node would loop forever
            }

            offsets.Add(offset);
            offset = Align(offset + length);
        }

        return offsets;
    }

    /// <summary>Reads wLength/wValueLength/wType/szKey; <paramref name="valueOffset"/> is just past the key.</summary>
    private static bool TryReadNodeHeader(ReadOnlySpan<byte> data, int offset, out int valueLength, out string key, out int valueOffset)
    {
        valueLength = 0;
        key = string.Empty;
        valueOffset = 0;

        if (offset < 0 || offset + 6 > data.Length)
        {
            return false;
        }

        int length = ReadU16(data, offset);
        valueLength = ReadU16(data, offset + 2);
        int type = ReadU16(data, offset + 4);
        if (length < 6 || offset + length > data.Length + 3)
        {
            return false;
        }

        // wType 1 means the value is text, and then wValueLength counts characters, not bytes.
        if (type != 1)
        {
            // Binary values (VS_FIXEDFILEINFO) count bytes; nothing to convert.
        }

        int cursor = offset + 6;
        var sb = new StringBuilder(32);
        while (cursor + 2 <= data.Length && sb.Length < 256)
        {
            char c = (char)ReadU16(data, cursor);
            cursor += 2;
            if (c == '\0')
            {
                break;
            }

            sb.Append(c);
        }

        key = sb.ToString();
        valueOffset = Align(cursor);
        return true;
    }

    private static Version ReadVersion(ref SpanReader r)
    {
        uint most = r.ReadU32();
        uint least = r.ReadU32();
        return new Version((int)(most >> 16), (int)(most & 0xFFFF), (int)(least >> 16), (int)(least & 0xFFFF));
    }

    private static int Align(int offset) => (offset + 3) & ~3;

    private static int ReadU16(ReadOnlySpan<byte> data, int offset)
        => offset + 2 <= data.Length ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) : 0;
}
