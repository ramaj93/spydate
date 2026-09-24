using System.Buffers.Binary;
using Spydate.Core.Binary;

namespace Spydate.Core.Dex;

/// <summary>Thrown when bytes cannot be interpreted as a DEX file (a fatal structural error).</summary>
public sealed class DexFormatException : BinaryParseException
{
    public DexFormatException(string message) : base(message)
    {
    }
}

/// <summary>
/// Bounds-checked little-endian cursor over a DEX file, with the LEB128 forms DEX uses. Every overrun throws
/// <see cref="DexFormatException"/>, so hostile input never surfaces as an <see cref="IndexOutOfRangeException"/>.
/// </summary>
public ref struct DexReader
{
    private readonly ReadOnlySpan<byte> _data;

    public DexReader(ReadOnlySpan<byte> data, int position = 0)
    {
        _data = data;
        if (position < 0 || position > data.Length)
        {
            throw new DexFormatException($"Offset 0x{position:X} is outside the {data.Length} bytes of the file.");
        }

        Position = position;
    }

    public int Position { get; private set; }

    public int Length => _data.Length;

    private void Ensure(long count)
    {
        if (count < 0 || Position + count > _data.Length)
        {
            throw new DexFormatException($"Read of {count} byte(s) at offset 0x{Position:X} exceeds the {_data.Length} bytes there are.");
        }
    }

    public void Seek(long position)
    {
        if (position < 0 || position > _data.Length)
        {
            throw new DexFormatException($"Offset 0x{position:X} is outside the {_data.Length} bytes of the file.");
        }

        Position = (int)position;
    }

    public void Skip(long count)
    {
        Ensure(count);
        Position += (int)count;
    }

    /// <summary>Moves to the next multiple of four, as item lists are aligned.</summary>
    public void Align4() => Skip((4 - (Position % 4)) % 4);

    public byte U1()
    {
        Ensure(1);
        return _data[Position++];
    }

    public ushort U2()
    {
        Ensure(2);
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_data[Position..]);
        Position += 2;
        return value;
    }

    public uint U4()
    {
        Ensure(4);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data[Position..]);
        Position += 4;
        return value;
    }

    public ReadOnlySpan<byte> Bytes(int count)
    {
        Ensure(count);
        var bytes = _data.Slice(Position, count);
        Position += count;
        return bytes;
    }

    /// <summary>An unsigned LEB128 of at most five bytes, as DEX allows for a 32-bit value.</summary>
    public uint Uleb128()
    {
        uint result = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            byte b = U1();
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw new DexFormatException($"A LEB128 value at offset 0x{Position:X} runs past five bytes.");
    }

    /// <summary>A signed LEB128 of at most five bytes.</summary>
    public int Sleb128()
    {
        int result = 0;
        int shift = 0;
        byte b;
        do
        {
            if (shift >= 35)
            {
                throw new DexFormatException($"A LEB128 value at offset 0x{Position:X} runs past five bytes.");
            }

            b = U1();
            result |= (b & 0x7F) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0);

        return shift < 32 && (b & 0x40) != 0 ? result | (-1 << shift) : result;
    }

    /// <summary><c>uleb128p1</c>: the value plus one, so that -1 (none) is stored as 0.</summary>
    public int Uleb128p1() => (int)Uleb128() - 1;

    /// <summary>A count about to be read, checked against what is left: every item takes at least a byte.</summary>
    public int Count(uint value, int minimumItemSize = 1)
    {
        if (value > int.MaxValue || (long)value * minimumItemSize > _data.Length - Position)
        {
            throw new DexFormatException($"A count of {value} at offset 0x{Position:X} is more than the file could hold.");
        }

        return (int)value;
    }
}
