using System.Buffers.Binary;
using System.Text;

namespace Spydate.Core.Pdb;

/// <summary>Raised when a file is not a usable MSF container.</summary>
public sealed class PdbParseException : Exception
{
    public PdbParseException(string message) : base(message)
    {
    }
}

/// <summary>
/// The Multi-Stream Format container a native PDB is stored in: a block device with a directory of
/// numbered streams, each a list of block indices. Everything above this layer reads streams.
/// </summary>
public sealed class MsfFile
{
    /// <summary>"Microsoft C/C++ MSF 7.00\r\n\x1aDS\0\0\0".</summary>
    private static readonly byte[] Magic7 = "Microsoft C/C++ MSF 7.00\r\nDS\0\0\0"u8.ToArray();

    private const int MaxStreams = 65536;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly uint[][] _streamBlocks;
    private readonly uint[] _streamSizes;

    private MsfFile(ReadOnlyMemory<byte> data, uint blockSize, uint[] streamSizes, uint[][] streamBlocks)
    {
        _data = data;
        BlockSize = blockSize;
        _streamSizes = streamSizes;
        _streamBlocks = streamBlocks;
    }

    public uint BlockSize { get; }

    public int StreamCount => _streamSizes.Length;

    /// <summary>The 32-byte MSF 7.00 signature, including the 0x1A end-of-file byte.</summary>
    public static ReadOnlySpan<byte> Signature => Magic7;

    /// <summary>True when the buffer starts with the MSF 7.00 signature.</summary>
    public static bool LooksLikeMsf(ReadOnlySpan<byte> data)
        => data.Length >= Magic7.Length && data[..Magic7.Length].SequenceEqual(Magic7);

    public static MsfFile Parse(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        if (!LooksLikeMsf(span))
        {
            throw new PdbParseException("Not an MSF 7.00 container (a portable PDB or an unsupported version).");
        }

        // Superblock: magic, then block size, free block map, block count, directory size, unused,
        // and the block holding the directory's own block list.
        int offset = Magic7.Length;
        uint blockSize = ReadU32(span, offset);
        uint numBlocks = ReadU32(span, offset + 8);
        uint directoryBytes = ReadU32(span, offset + 12);
        uint blockMapAddr = ReadU32(span, offset + 20);

        if (blockSize is not (512 or 1024 or 2048 or 4096) || blockSize == 0)
        {
            throw new PdbParseException($"Unsupported MSF block size {blockSize}.");
        }

        if ((long)numBlocks * blockSize > data.Length + blockSize)
        {
            throw new PdbParseException($"MSF claims {numBlocks} blocks but the file holds {data.Length / blockSize}.");
        }

        // The directory is itself a stream: its block list lives in the block map block.
        uint directoryBlockCount = Ceil(directoryBytes, blockSize);
        var directoryBlocks = ReadBlockList(span, blockMapAddr * blockSize, directoryBlockCount, blockSize, data.Length);
        var directory = Gather(data, directoryBlocks, blockSize, directoryBytes);

        var reader = directory.Span;
        uint streamCount = ReadU32(reader, 0);
        if (streamCount > MaxStreams)
        {
            throw new PdbParseException($"MSF declares {streamCount} streams.");
        }

        var sizes = new uint[streamCount];
        int cursor = 4;
        for (int i = 0; i < streamCount; i++, cursor += 4)
        {
            uint size = ReadU32(reader, cursor);
            // 0xFFFFFFFF marks a stream that was deleted; treat it as empty.
            sizes[i] = size == uint.MaxValue ? 0 : size;
        }

        var blocks = new uint[streamCount][];
        for (int i = 0; i < streamCount; i++)
        {
            uint count = Ceil(sizes[i], blockSize);
            blocks[i] = new uint[count];
            for (int b = 0; b < count; b++, cursor += 4)
            {
                blocks[i][b] = ReadU32(reader, cursor);
            }
        }

        return new MsfFile(data, blockSize, sizes, blocks);
    }

    /// <summary>Contents of a stream, or empty when the index is out of range or the stream is empty.</summary>
    public ReadOnlyMemory<byte> ReadStream(int index)
        => index < 0 || index >= _streamSizes.Length || _streamSizes[index] == 0
            ? ReadOnlyMemory<byte>.Empty
            : Gather(_data, _streamBlocks[index], BlockSize, _streamSizes[index]);

    public uint StreamSize(int index) => index < 0 || index >= _streamSizes.Length ? 0 : _streamSizes[index];

    /// <summary>Copies the blocks of a stream into one contiguous buffer.</summary>
    private static ReadOnlyMemory<byte> Gather(ReadOnlyMemory<byte> data, uint[] blocks, uint blockSize, uint size)
    {
        var buffer = new byte[size];
        int written = 0;
        foreach (uint block in blocks)
        {
            long start = (long)block * blockSize;
            if (start < 0 || start >= data.Length)
            {
                break; // truncated file: return what was readable
            }

            int take = (int)Math.Min(blockSize, Math.Min(size - written, data.Length - start));
            if (take <= 0)
            {
                break;
            }

            data.Slice((int)start, take).CopyTo(buffer.AsMemory(written));
            written += take;
        }

        return buffer.AsMemory(0, written);
    }

    private static uint[] ReadBlockList(ReadOnlySpan<byte> span, long offset, uint count, uint blockSize, int fileLength)
    {
        var list = new uint[count];
        for (int i = 0; i < count; i++)
        {
            long at = offset + (i * 4);
            if (at + 4 > span.Length)
            {
                throw new PdbParseException("MSF stream directory is truncated.");
            }

            uint block = ReadU32(span, (int)at);
            if ((long)block * blockSize >= fileLength + blockSize)
            {
                throw new PdbParseException($"MSF block {block} is outside the file.");
            }

            list[i] = block;
        }

        return list;
    }

    private static uint Ceil(uint value, uint unit) => unit == 0 ? 0 : (value + unit - 1) / unit;

    private static uint ReadU32(ReadOnlySpan<byte> span, int offset)
        => offset + 4 <= span.Length ? BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]) : 0;
}
