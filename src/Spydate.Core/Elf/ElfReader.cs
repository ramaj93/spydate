using System.Buffers.Binary;
using System.Text;
using Spydate.Core.Binary;

namespace Spydate.Core.Elf;

/// <summary>Thrown when a file cannot be interpreted as an ELF image (fatal structural error).</summary>
public sealed class ElfParseException : BinaryParseException
{
    public ElfParseException(string message) : base(message)
    {
    }

    public ElfParseException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Bounds-checked cursor over an ELF file. ELF states its own byte order and word size in its first bytes, so
/// unlike the PE reader this one carries both: every multi-byte read honours the order, and the "address" and
/// "offset" reads are four bytes wide in a 32-bit file and eight in a 64-bit one. Every overrun throws
/// <see cref="ElfParseException"/>, so hostile input never surfaces as an <see cref="IndexOutOfRangeException"/>.
/// </summary>
public ref struct ElfReader
{
    private readonly ReadOnlySpan<byte> _data;

    public ElfReader(ReadOnlySpan<byte> data, bool is64Bit, bool bigEndian, long position = 0)
    {
        _data = data;
        Is64Bit = is64Bit;
        BigEndian = bigEndian;
        Seek(position);
    }

    public bool Is64Bit { get; }

    public bool BigEndian { get; }

    public int Position { get; private set; }

    public int Length => _data.Length;

    public bool CanRead(long count) => count >= 0 && Position + count <= _data.Length;

    private void Ensure(int count)
    {
        if (!CanRead(count))
        {
            throw new ElfParseException($"Read of {count} byte(s) at offset 0x{Position:X} exceeds the file's {_data.Length} bytes.");
        }
    }

    public void Seek(long position)
    {
        if (position < 0 || position > _data.Length)
        {
            throw new ElfParseException($"Offset 0x{position:X} is outside the file's {_data.Length} bytes.");
        }

        Position = (int)position;
    }

    public void Skip(int count)
    {
        Ensure(count);
        Position += count;
    }

    public byte U8()
    {
        Ensure(1);
        return _data[Position++];
    }

    public ushort U16()
    {
        Ensure(2);
        var s = _data.Slice(Position, 2);
        Position += 2;
        return BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
    }

    public uint U32()
    {
        Ensure(4);
        var s = _data.Slice(Position, 4);
        Position += 4;
        return BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
    }

    public ulong U64()
    {
        Ensure(8);
        var s = _data.Slice(Position, 8);
        Position += 8;
        return BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s);
    }

    /// <summary>An address, offset or size field: <c>Elf32_Addr</c>/<c>Elf32_Off</c> or their 64-bit forms.</summary>
    public ulong Word() => Is64Bit ? U64() : U32();

    /// <summary>A signed word, such as a dynamic entry's tag or a relocation's addend.</summary>
    public long SWord() => Is64Bit ? (long)U64() : (int)U32();

    public ReadOnlySpan<byte> Bytes(int count)
    {
        Ensure(count);
        var s = _data.Slice(Position, count);
        Position += count;
        return s;
    }

    public ulong ULeb128()
    {
        ulong result = 0;
        for (int shift = 0; ; shift += 7)
        {
            byte b = U8();
            if (shift < 64)
            {
                result |= (ulong)(b & 0x7F) << shift;
            }

            if ((b & 0x80) == 0)
            {
                return result;
            }

            if (shift > 70)
            {
                throw new ElfParseException($"An LEB128 number at offset 0x{Position:X} never ends.");
            }
        }
    }

    public long SLeb128()
    {
        long result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = U8();
            if (shift < 64)
            {
                result |= (long)(b & 0x7F) << shift;
            }

            shift += 7;
            if (shift > 77)
            {
                throw new ElfParseException($"An LEB128 number at offset 0x{Position:X} never ends.");
            }
        }
        while ((b & 0x80) != 0);

        if (shift < 64 && (b & 0x40) != 0)
        {
            result |= -1L << shift;
        }

        return result;
    }

    /// <summary>A NUL-terminated string from the cursor, at most <paramref name="max"/> bytes.</summary>
    public string Z(int max = 4096)
    {
        var rest = _data[Position..];
        int limit = Math.Min(max, rest.Length);
        int end = rest[..limit].IndexOf((byte)0);
        if (end < 0)
        {
            end = limit;
        }

        string s = Encoding.UTF8.GetString(rest[..end]);
        Position += Math.Min(end + 1, rest.Length);
        return s;
    }

    /// <summary>A NUL-terminated string at an offset, or empty when the offset is outside the data.</summary>
    public static string StringAt(ReadOnlySpan<byte> data, long offset, int max = 4096)
    {
        if (offset < 0 || offset >= data.Length)
        {
            return string.Empty;
        }

        var rest = data[(int)offset..];
        int limit = Math.Min(max, rest.Length);
        int end = rest[..limit].IndexOf((byte)0);
        return Encoding.UTF8.GetString(rest[..(end < 0 ? limit : end)]);
    }
}
