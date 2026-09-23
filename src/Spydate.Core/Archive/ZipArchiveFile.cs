using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Spydate.Core.Binary;

namespace Spydate.Core.Archive;

/// <summary>An archive could not be read, or one entry in it could not. The message says which, and why.</summary>
public sealed class ArchiveException : BinaryParseException
{
    public ArchiveException(string message) : base(message)
    {
    }

    public ArchiveException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// One file in an archive, as its directory describes it. The sizes are what the directory claims, which an
/// archive built to mislead can get wrong; <see cref="IArchive.Read"/> checks them against what it inflates.
/// </summary>
public sealed record ArchiveEntry(
    int Index,
    string Name,
    long Size,
    long CompressedSize,
    uint Crc32,
    ushort Method,
    DateTime? Modified,
    bool IsEncrypted,
    long LocalHeaderOffset)
{
    public bool IsDirectory => Name.EndsWith('/');

    /// <summary>The compression method in words: <c>stored</c>, <c>deflated</c>, or its number.</summary>
    public string MethodName => Method switch
    {
        0 => "stored",
        8 => "deflated",
        9 => "deflate64",
        12 => "bzip2",
        14 => "lzma",
        93 => "zstd",
        _ => $"method {Method}",
    };

    /// <summary>Whether <see cref="IArchive.Read"/> can produce its bytes: stored or deflated, and not encrypted.</summary>
    public bool CanRead => !IsEncrypted && Method is 0 or 8;
}

/// <summary>
/// A container of named files — a JAR, an APK. What the readers of those formats ask of one: its entries in
/// directory order, one found by name, and one entry's bytes.
/// </summary>
public interface IArchive
{
    IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>The entry with this exact name, or null. When a name appears twice, the first, as a JVM would take it.</summary>
    ArchiveEntry? Find(string name);

    /// <summary>
    /// An entry's bytes, inflated. Throws <see cref="ArchiveException"/> when it is larger than
    /// <paramref name="maxLength"/>, uses a method or encryption this cannot undo, is cut short, or fails its CRC.
    /// </summary>
    byte[] Read(ArchiveEntry entry, int maxLength);
}

/// <summary>
/// A zip archive read from memory. The central directory is parsed here rather than through
/// <see cref="ZipArchive"/>, because an analyst wants what that class hides — each entry's method, a name that
/// appears twice, the directory's own bytes to fingerprint the build by — and because every length in a hostile
/// zip is a lie until checked. Only inflating is delegated, to <see cref="DeflateStream"/>.
/// </summary>
public sealed class ZipArchiveFile : IArchive
{
    private const uint EndOfDirectorySignature = 0x06054B50;
    private const uint Zip64LocatorSignature = 0x07064B50;
    private const uint Zip64EndSignature = 0x06064B50;
    private const uint DirectoryEntrySignature = 0x02014B50;
    private const uint LocalHeaderSignature = 0x04034B50;

    /// <summary>The end record is 22 bytes plus a comment of at most 65535.</summary>
    private const int MaxEndSearch = 22 + 0xFFFF;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly Dictionary<string, ArchiveEntry> _byName;

    private ZipArchiveFile(ReadOnlyMemory<byte> data, List<ArchiveEntry> entries, long directoryOffset, long directorySize, List<string> warnings)
    {
        _data = data;
        Entries = entries;
        DirectoryOffset = directoryOffset;
        DirectorySize = directorySize;
        Warnings = warnings;
        _byName = new Dictionary<string, ArchiveEntry>(entries.Count, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!_byName.TryAdd(entry.Name, entry))
            {
                Duplicates.Add(entry.Name);
            }
        }

        if (Duplicates.Count > 0)
        {
            warnings.Add($"{Duplicates.Count} name(s) appear more than once; the first of each is the one read: {string.Join(", ", Duplicates.Take(5))}{(Duplicates.Count > 5 ? ", ..." : string.Empty)}");
        }
    }

    public IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>Where the central directory starts in the file, and how long it is.</summary>
    public long DirectoryOffset { get; }

    public long DirectorySize { get; }

    /// <summary>Names that appear more than once. A JVM reads the first; a tool that reads the last sees another program.</summary>
    public HashSet<string> Duplicates { get; } = new(StringComparer.Ordinal);

    /// <summary>What was tolerated rather than refused: a duplicate name, a directory entry that was skipped.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// A SHA-256 of the central directory, as 32 hex characters. The directory holds every entry's name, sizes and
    /// CRC, so any change to any entry changes it, while reading it costs nothing beyond what opening already did.
    /// </summary>
    public string DirectoryHash()
    {
        var span = _data.Span.Slice((int)DirectoryOffset, (int)DirectorySize);
        return Convert.ToHexString(SHA256.HashData(span))[..32];
    }

    public ArchiveEntry? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Parses a zip held in memory. Throws <see cref="ArchiveException"/> when it has no readable directory.</summary>
    public static ZipArchiveFile Open(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        long end = FindEndOfDirectory(span);
        if (end < 0)
        {
            throw new ArchiveException("No zip end-of-directory record was found: this is not a zip archive, or it has been cut short.");
        }

        long count = BinaryPrimitives.ReadUInt16LittleEndian(span[(int)(end + 10)..]);
        long size = BinaryPrimitives.ReadUInt32LittleEndian(span[(int)(end + 12)..]);
        long offset = BinaryPrimitives.ReadUInt32LittleEndian(span[(int)(end + 16)..]);

        // A zip64 archive says so by saturating a field, and keeps the real values in a record before this one.
        if ((count == 0xFFFF || size == 0xFFFFFFFF || offset == 0xFFFFFFFF) && end >= 20
            && BinaryPrimitives.ReadUInt32LittleEndian(span[(int)(end - 20)..]) == Zip64LocatorSignature)
        {
            ulong record = BinaryPrimitives.ReadUInt64LittleEndian(span[(int)(end - 12)..]);
            if (span.Length >= 56 && record <= (ulong)(span.Length - 56) &&BinaryPrimitives.ReadUInt32LittleEndian(span[(int)record..]) == Zip64EndSignature)
            {
                count = Clamp(BinaryPrimitives.ReadUInt64LittleEndian(span[(int)(record + 32)..]));
                size = Clamp(BinaryPrimitives.ReadUInt64LittleEndian(span[(int)(record + 40)..]));
                offset = Clamp(BinaryPrimitives.ReadUInt64LittleEndian(span[(int)(record + 48)..]));
            }
        }

        if (offset > span.Length || size > span.Length - offset)
        {
            // A self-extractor or a zip with junk prepended: the directory really does sit just before the end record.
            long shifted = end - size;
            if (shifted < 0 || size > span.Length)
            {
                throw new ArchiveException($"The zip directory claims {size} bytes at offset 0x{offset:X}, beyond the file's {span.Length} bytes.");
            }

            offset = shifted;
        }

        var warnings = new List<string>();
        var entries = ReadDirectory(span.Slice((int)offset, (int)size), (int)Math.Min(count, int.MaxValue), warnings);
        return new ZipArchiveFile(data, entries, offset, size, warnings);
    }

    public byte[] Read(ArchiveEntry entry, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsEncrypted)
        {
            throw new ArchiveException($"{entry.Name} is encrypted.");
        }

        if (entry.Method is not (0 or 8))
        {
            throw new ArchiveException($"{entry.Name} is compressed with {entry.MethodName}, which Spydate cannot inflate.");
        }

        if (entry.Size > maxLength)
        {
            throw new ArchiveException($"{entry.Name} is {entry.Size:N0} bytes, more than the {maxLength:N0} read at once.");
        }

        var span = _data.Span;
        long local = entry.LocalHeaderOffset;
        if (local < 0 || local > span.Length - 30 || BinaryPrimitives.ReadUInt32LittleEndian(span[(int)local..]) != LocalHeaderSignature)
        {
            throw new ArchiveException($"{entry.Name}'s local header at 0x{local:X} is missing.");
        }

        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(span[(int)(local + 26)..]);
        int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(span[(int)(local + 28)..]);
        long start = local + 30 + nameLength + extraLength;
        if (start > span.Length || entry.CompressedSize > span.Length - start)
        {
            throw new ArchiveException($"{entry.Name}'s {entry.CompressedSize:N0} packed bytes run past the end of the file.");
        }

        var packed = _data.Slice((int)start, (int)entry.CompressedSize);
        byte[] bytes = new byte[entry.Size];
        int read;
        if (entry.Method == 0)
        {
            if (entry.CompressedSize != entry.Size)
            {
                throw new ArchiveException($"{entry.Name} is stored, yet its sizes differ ({entry.CompressedSize:N0} packed, {entry.Size:N0} plain).");
            }

            packed.Span.CopyTo(bytes);
            read = bytes.Length;
        }
        else
        {
            try
            {
                using var stream = new DeflateStream(new ReadOnlyMemoryStream(packed), CompressionMode.Decompress);
                read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            }
            catch (InvalidDataException ex)
            {
                throw new ArchiveException($"{entry.Name} does not inflate: {ex.Message}", ex);
            }
        }

        if (read != bytes.Length)
        {
            throw new ArchiveException($"{entry.Name} inflates to {read:N0} bytes, not the {entry.Size:N0} its directory entry says.");
        }

        if (Crc32.Compute(bytes) != entry.Crc32)
        {
            throw new ArchiveException($"{entry.Name} fails its CRC check: its bytes are not the ones the directory describes.");
        }

        return bytes;
    }

    private static long Clamp(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    /// <summary>The end-of-directory record, searched for backwards from the end, where a trailing comment may push it.</summary>
    private static long FindEndOfDirectory(ReadOnlySpan<byte> span)
    {
        long lowest = Math.Max(0, span.Length - MaxEndSearch);
        for (long at = span.Length - 22; at >= lowest; at--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(span[(int)at..]) == EndOfDirectorySignature)
            {
                return at;
            }
        }

        return -1;
    }

    private static List<ArchiveEntry> ReadDirectory(ReadOnlySpan<byte> directory, int declared, List<string> warnings)
    {
        // Never trust the declared count for an allocation: each entry takes at least 46 bytes.
        var entries = new List<ArchiveEntry>(Math.Min(declared, directory.Length / 46));
        int at = 0;
        while (at <= directory.Length - 46)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(directory[at..]) != DirectoryEntrySignature)
            {
                warnings.Add($"The zip directory stops making sense at byte {at} of {directory.Length}; {entries.Count} entries were read.");
                break;
            }

            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(directory[(at + 8)..]);
            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(directory[(at + 10)..]);
            ushort time = BinaryPrimitives.ReadUInt16LittleEndian(directory[(at + 12)..]);
            ushort date = BinaryPrimitives.ReadUInt16LittleEndian(directory[(at + 14)..]);
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(directory[(at + 16)..]);
            long compressed = BinaryPrimitives.ReadUInt32LittleEndian(directory[(at + 20)..]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(directory[(at + 24)..]);
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(directory[(at + 28)..]);
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(directory[(at + 30)..]);
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(directory[(at + 32)..]);
            long local = BinaryPrimitives.ReadUInt32LittleEndian(directory[(at + 42)..]);

            int next = at + 46 + nameLength + extraLength + commentLength;
            if (next > directory.Length)
            {
                warnings.Add($"The zip directory's entry at byte {at} runs past its end; {entries.Count} entries were read.");
                break;
            }

            var nameBytes = directory.Slice(at + 46, nameLength);
            // Bit 11 says UTF-8. Without it the name is officially code page 437, but JAR tools write UTF-8
            // regardless, and a name that is not valid UTF-8 comes out with replacement characters, not an error.
            string name = Encoding.UTF8.GetString(nameBytes);

            // Zip64 keeps whichever of the sizes and the offset overflowed in an extra field, in that order.
            var extra = directory.Slice(at + 46 + nameLength, extraLength);
            ReadZip64(extra, ref size, ref compressed, ref local);

            entries.Add(new ArchiveEntry(entries.Count, name, size, compressed, crc, method, DosTime(date, time), (flags & 1) != 0, local));
            at = next;
        }

        return entries;
    }

    private static void ReadZip64(ReadOnlySpan<byte> extra, ref long size, ref long compressed, ref long local)
    {
        int at = 0;
        while (at <= extra.Length - 4)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(extra[at..]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(extra[(at + 2)..]);
            if (at + 4 + length > extra.Length)
            {
                return;
            }

            if (id == 0x0001)
            {
                var field = extra.Slice(at + 4, length);
                int f = 0;
                if (size == 0xFFFFFFFF && f + 8 <= field.Length)
                {
                    size = Clamp(BinaryPrimitives.ReadUInt64LittleEndian(field[f..]));
                    f += 8;
                }

                if (compressed == 0xFFFFFFFF && f + 8 <= field.Length)
                {
                    compressed = Clamp(BinaryPrimitives.ReadUInt64LittleEndian(field[f..]));
                    f += 8;
                }

                if (local == 0xFFFFFFFF && f + 8 <= field.Length)
                {
                    local = Clamp(BinaryPrimitives.ReadUInt64LittleEndian(field[f..]));
                }

                return;
            }

            at += 4 + length;
        }
    }

    /// <summary>An MS-DOS date and time, as zips store them, or null when the fields are not a date.</summary>
    private static DateTime? DosTime(ushort date, ushort time)
    {
        int year = 1980 + (date >> 9);
        int month = (date >> 5) & 0xF;
        int day = date & 0x1F;
        int hour = time >> 11;
        int minute = (time >> 5) & 0x3F;
        int second = (time & 0x1F) * 2;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
    }

    /// <summary>A read-only stream over memory the archive already holds, so inflating copies nothing first.</summary>
    private sealed class ReadOnlyMemoryStream(ReadOnlyMemory<byte> memory) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => memory.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = Math.Min(buffer.Length, memory.Length - _position);
            memory.Span.Slice(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>The zip CRC-32 (the IEEE polynomial, reflected), to check an inflated entry against its directory.</summary>
public static class Crc32
{
    private static readonly uint[] Table = Build();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
