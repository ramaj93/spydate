using System.Buffers.Binary;
using System.Text;
using Spydate.Core.PE;

namespace Spydate.Core.Binary;

/// <summary>
/// Bounds-checked little-endian cursor over a span of bytes.
/// Every overrun throws <see cref="PeParseException"/> so callers never observe
/// <see cref="IndexOutOfRangeException"/> on hostile input.
/// </summary>
public ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _data;

    public SpanReader(ReadOnlySpan<byte> data, int position = 0)
    {
        _data = data;
        Position = position;
    }

    public int Position { get; set; }

    public int Length => _data.Length;

    public int Remaining => _data.Length - Position;

    public bool CanRead(int count) => count >= 0 && Position >= 0 && Position + count <= _data.Length;

    private void Ensure(int count)
    {
        if (!CanRead(count))
        {
            throw new PeParseException($"Read of {count} byte(s) at offset 0x{Position:X} exceeds buffer of {_data.Length} bytes.");
        }
    }

    public byte ReadU8()
    {
        Ensure(1);
        return _data[Position++];
    }

    public ushort ReadU16()
    {
        Ensure(2);
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(Position, 2));
        Position += 2;
        return v;
    }

    public uint ReadU32()
    {
        Ensure(4);
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(Position, 4));
        Position += 4;
        return v;
    }

    public ulong ReadU64()
    {
        Ensure(8);
        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(Position, 8));
        Position += 8;
        return v;
    }

    /// <summary>Reads a 4-byte (PE32) or 8-byte (PE32+) unsigned value.</summary>
    public ulong ReadPointer(bool is64Bit) => is64Bit ? ReadU64() : ReadU32();

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        Ensure(count);
        var s = _data.Slice(Position, count);
        Position += count;
        return s;
    }

    public Guid ReadGuid()
    {
        Ensure(16);
        var g = new Guid(_data.Slice(Position, 16));
        Position += 16;
        return g;
    }

    /// <summary>Reads a NUL-terminated ASCII string, at most <paramref name="max"/> bytes.</summary>
    public string ReadAsciiZ(int max = 4096)
    {
        if (Position < 0 || Position > _data.Length)
        {
            throw new PeParseException($"String read at offset 0x{Position:X} is outside the buffer.");
        }

        var slice = _data[Position..];
        int limit = Math.Min(max, slice.Length);
        int end = slice[..limit].IndexOf((byte)0);
        if (end < 0)
        {
            end = limit;
        }

        string s = Encoding.ASCII.GetString(slice[..end]);
        Position += Math.Min(end + 1, slice.Length);
        return s;
    }

    /// <summary>Reads a fixed-size ASCII field, trimming trailing NULs (e.g. section names).</summary>
    public string ReadFixedAscii(int size)
    {
        var bytes = ReadBytes(size);
        int end = bytes.IndexOf((byte)0);
        if (end < 0)
        {
            end = bytes.Length;
        }

        return Encoding.ASCII.GetString(bytes[..end]);
    }

    public void Skip(int count)
    {
        Ensure(count);
        Position += count;
    }

    public void Seek(int position)
    {
        if (position < 0 || position > _data.Length)
        {
            throw new PeParseException($"Seek to offset 0x{position:X} is outside the buffer of {_data.Length} bytes.");
        }

        Position = position;
    }

    /// <summary>Reads a NUL-terminated ASCII string at an absolute offset without moving the cursor.</summary>
    public static string ReadAsciiZAt(ReadOnlySpan<byte> data, int offset, int max = 4096)
    {
        var r = new SpanReader(data, offset);
        return r.ReadAsciiZ(max);
    }
}
