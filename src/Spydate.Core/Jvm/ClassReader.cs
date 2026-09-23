using System.Buffers.Binary;
using Spydate.Core.Binary;

namespace Spydate.Core.Jvm;

/// <summary>Thrown when bytes cannot be interpreted as a Java class file (a fatal structural error).</summary>
public sealed class ClassFormatException : BinaryParseException
{
    public ClassFormatException(string message) : base(message)
    {
    }

    public ClassFormatException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Bounds-checked big-endian cursor over a class file. Every overrun throws <see cref="ClassFormatException"/>,
/// so hostile input never surfaces as an <see cref="IndexOutOfRangeException"/>.
/// </summary>
public ref struct ClassReader
{
    private readonly ReadOnlySpan<byte> _data;

    public ClassReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        Position = 0;
    }

    public int Position { get; private set; }

    public int Length => _data.Length;

    public int Remaining => _data.Length - Position;

    private void Ensure(long count)
    {
        if (count < 0 || Position + count > _data.Length)
        {
            throw new ClassFormatException($"Read of {count} byte(s) at offset 0x{Position:X} exceeds the {_data.Length} bytes there are.");
        }
    }

    public void Skip(long count)
    {
        Ensure(count);
        Position += (int)count;
    }

    public byte U1()
    {
        Ensure(1);
        return _data[Position++];
    }

    public ushort U2()
    {
        Ensure(2);
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(_data[Position..]);
        Position += 2;
        return value;
    }

    public uint U4()
    {
        Ensure(4);
        uint value = BinaryPrimitives.ReadUInt32BigEndian(_data[Position..]);
        Position += 4;
        return value;
    }

    public ulong U8()
    {
        Ensure(8);
        ulong value = BinaryPrimitives.ReadUInt64BigEndian(_data[Position..]);
        Position += 8;
        return value;
    }

    public ReadOnlySpan<byte> Bytes(long count)
    {
        Ensure(count);
        var slice = _data.Slice(Position, (int)count);
        Position += (int)count;
        return slice;
    }

    /// <summary>
    /// A count of items each at least <paramref name="minItemSize"/> bytes long, refused when the bytes left could
    /// not hold that many. It is what keeps a forged count from sizing an allocation.
    /// </summary>
    public int Count(int minItemSize, string what)
    {
        int count = U2();
        if ((long)count * minItemSize > Remaining)
        {
            throw new ClassFormatException($"{count} {what} cannot fit in the {Remaining} bytes left at offset 0x{Position:X}.");
        }

        return count;
    }
}
