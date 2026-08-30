using System.Text;

namespace Spydate.Core.PE;

/// <summary>
/// The table that says which real DLL is behind an <c>api-ms-win-*</c> import.
///
/// Since Windows 7 most system imports name an "API set" — <c>api-ms-win-core-synch-l1-1-0.dll</c> —
/// rather than the DLL that implements them. No such file exists; the loader redirects the name using a
/// schema built into the operating system. Without reading it, nearly every import of a modern binary
/// resolves to nothing, which is most of what there is to resolve.
///
/// The schema lives in the <c>.apiset</c> section of <c>apisetschema.dll</c>, in the version 6 layout
/// used by Windows 10 and 11. Nothing here is looked up in a table of our own: the mapping is read out of
/// a file on the machine, and if the file is absent, older, or laid out differently, nothing is claimed.
/// </summary>
public sealed class ApiSetSchema
{
    /// <summary>Only version 6 is parsed. Earlier layouts (Windows 7 and 8) differ and are not guessed at.</summary>
    private const uint SupportedVersion = 6;

    private const int NamespaceHeaderSize = 28;
    private const int EntrySize = 24;
    private const int ValueSize = 20;

    /// <summary>A schema far larger than any real one is a sign the section is not what it claims.</summary>
    private const int MaxEntries = 20_000;

    private readonly Dictionary<string, string> _hosts;

    private ApiSetSchema(Dictionary<string, string> hosts) => _hosts = hosts;

    /// <summary>An empty schema: every lookup misses, and callers behave as if there were no schema.</summary>
    public static ApiSetSchema Empty { get; } = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public int Count => _hosts.Count;

    /// <summary>
    /// Every redirect the schema declares, keyed the way the loader keys them: the api set name up to
    /// but not including its last version component (see <see cref="Resolve"/>).
    /// </summary>
    public IReadOnlyDictionary<string, string> Entries => _hosts;

    /// <summary>True for a name the loader would redirect, whether or not this schema knows it.</summary>
    public static bool IsApiSetName(string module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return module.StartsWith("api-", StringComparison.OrdinalIgnoreCase)
               || module.StartsWith("ext-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The DLL that implements <paramref name="module"/>, or null when this is not a redirected name or
    /// the schema does not contain it. The <c>.dll</c> suffix is optional on the way in.
    ///
    /// The last version component of the name is not part of the key. A binary importing
    /// <c>api-ms-win-core-synch-l1-1-0</c> is matched by the schema's <c>...-l1-1-1</c> entry, because
    /// the loader hashes only the part the schema marks as hashed and that part stops one component
    /// short. Looking for the whole name instead misses most of the imports of a real binary.
    /// </summary>
    public string? Resolve(string module)
    {
        ArgumentNullException.ThrowIfNull(module);
        string key = module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? module[..^4] : module;
        if (_hosts.TryGetValue(key, out string? host))
        {
            return host;
        }

        int last = key.LastIndexOf('-');
        return last > 0 && _hosts.TryGetValue(key[..last], out host) ? host : null;
    }

    /// <summary>
    /// Reads the schema out of an <c>apisetschema.dll</c> that has already been parsed. Returns
    /// <see cref="Empty"/> for anything that is not one, rather than throwing: the file is only ever
    /// opportunistically present, and a malformed one must not stop analysis of the binary being read.
    /// </summary>
    public static ApiSetSchema From(PeImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var section = image.Sections.FirstOrDefault(s => s.Name.Equals(".apiset", StringComparison.Ordinal));
        if (section is null || section.SizeOfRawData < NamespaceHeaderSize)
        {
            return Empty;
        }

        var data = image.Data.Span;
        long start = section.PointerToRawData;
        long length = Math.Min(section.SizeOfRawData, data.Length - start);
        if (start < 0 || start >= data.Length || length < NamespaceHeaderSize)
        {
            return Empty;
        }

        var schema = data.Slice((int)start, (int)length);
        if (ReadU32(schema, 0) != SupportedVersion)
        {
            return Empty;
        }

        uint count = ReadU32(schema, 12);
        uint entryOffset = ReadU32(schema, 16);
        if (count == 0 || count > MaxEntries)
        {
            return Empty;
        }

        var hosts = new Dictionary<string, string>((int)count, StringComparer.OrdinalIgnoreCase);
        for (uint i = 0; i < count; i++)
        {
            long entry = entryOffset + ((long)i * EntrySize);
            if (entry < 0 || entry + EntrySize > schema.Length)
            {
                break;
            }

            // NameLength covers the whole name; HashedLength covers the part the loader keys on, which
            // stops before the last version component. Storing the hashed part is what lets an import of
            // "...-l1-1-0" find the schema's "...-l1-1-1" entry, as it does at run time.
            uint nameOffset = ReadU32(schema, entry + 4);
            uint nameLength = ReadU32(schema, entry + 8);
            uint hashedLength = ReadU32(schema, entry + 12);
            string? name = ReadUtf16(schema, nameOffset, hashedLength is > 0 and var h && h <= nameLength ? h : nameLength);
            if (name is null)
            {
                continue;
            }

            if (ResolveHost(schema, ReadU32(schema, entry + 16), ReadU32(schema, entry + 20)) is { } host)
            {
                hosts[name] = host;
            }
        }

        return hosts.Count == 0 ? Empty : new ApiSetSchema(hosts);
    }

    /// <summary>
    /// An api set can name several hosts: one default, and further ones that apply only when a
    /// particular module is the importer. Only the default is used — a per-importer override is about
    /// which copy of the code runs, and both copies answer to the same declaration.
    /// </summary>
    private static string? ResolveHost(ReadOnlySpan<byte> schema, uint valueOffset, uint valueCount)
    {
        string? fallback = null;
        for (uint v = 0; v < valueCount && v < MaxEntries; v++)
        {
            long value = valueOffset + ((long)v * ValueSize);
            if (value < 0 || value + ValueSize > schema.Length)
            {
                break;
            }

            if (ReadUtf16(schema, ReadU32(schema, value + 12), ReadU32(schema, value + 16)) is not { Length: > 0 } host)
            {
                continue;
            }

            // A zero-length importer name is the entry that applies to everyone.
            if (ReadU32(schema, value + 8) == 0)
            {
                return host;
            }

            fallback ??= host;
        }

        return fallback;
    }

    private static uint ReadU32(ReadOnlySpan<byte> span, long offset)
        => offset < 0 || offset + 4 > span.Length ? 0 : BitConverter.ToUInt32(span.Slice((int)offset, 4));

    /// <summary>Names are UTF-16 and not terminated; the length is in bytes.</summary>
    private static string? ReadUtf16(ReadOnlySpan<byte> span, uint offset, uint byteLength)
    {
        if (byteLength == 0 || byteLength % 2 != 0 || offset >= span.Length || offset + byteLength > span.Length)
        {
            return null;
        }

        return Encoding.Unicode.GetString(span.Slice((int)offset, (int)byteLength));
    }
}
