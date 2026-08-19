using System.Buffers.Binary;
using System.Text;
using Spydate.Core.Pdb;

namespace Spydate.Tests;

/// <summary>
/// Builds a minimal MSF container: a superblock, a stream directory, and the three streams a PDB
/// reader cares about (info, DBI, symbol records). No native PDB with a linked DBI stream ships on
/// a machine without Visual Studio, so the record-parsing paths are exercised from here.
/// </summary>
internal static class SyntheticPdb
{
    private const int BlockSize = 4096;
    private const int SuperBlockBlock = 0;
    private const int BlockMapBlock = 3;
    private const int DirectoryBlock = 4;
    private const int FirstStreamBlock = 5;

    /// <summary>A symbol to place in the record stream.</summary>
    internal readonly record struct Public(string Name, ushort Segment, uint Offset, bool IsFunction);

    public static byte[] Build(Guid guid, uint age, IEnumerable<Public> publics, bool includeDbi = true)
    {
        var symbolRecords = BuildSymbolRecords(publics);
        var info = BuildInfoStream(guid, age);
        var dbi = includeDbi ? BuildDbiHeader(symbolRecordStream: 4) : Array.Empty<byte>();

        // Streams: 0 = old directory (empty), 1 = info, 2 = TPI (empty), 3 = DBI, 4 = records.
        var streams = new[] { Array.Empty<byte>(), info, Array.Empty<byte>(), dbi, symbolRecords };
        return Assemble(streams);
    }

    /// <summary>Raw record bytes, for tests that need a malformed run.</summary>
    public static byte[] BuildWithRecords(Guid guid, uint age, byte[] rawRecords)
        => Assemble(new[] { Array.Empty<byte>(), BuildInfoStream(guid, age), Array.Empty<byte>(), BuildDbiHeader(4), rawRecords });

    private static byte[] BuildInfoStream(Guid guid, uint age)
    {
        var info = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(info, 20000404);          // version
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(4), 0x1234);  // signature
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(8), age);
        guid.TryWriteBytes(info.AsSpan(12));
        return info;
    }

    private static byte[] BuildDbiHeader(ushort symbolRecordStream)
    {
        var dbi = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(dbi, -1);                          // version signature
        BinaryPrimitives.WriteUInt32LittleEndian(dbi.AsSpan(4), 19990903);         // version header
        BinaryPrimitives.WriteUInt32LittleEndian(dbi.AsSpan(8), 1);                // age
        BinaryPrimitives.WriteUInt16LittleEndian(dbi.AsSpan(12), 5);               // global stream
        BinaryPrimitives.WriteUInt16LittleEndian(dbi.AsSpan(16), 6);               // public stream
        BinaryPrimitives.WriteUInt16LittleEndian(dbi.AsSpan(20), symbolRecordStream);
        return dbi;
    }

    private static byte[] BuildSymbolRecords(IEnumerable<Public> publics)
    {
        var buffer = new List<byte>();
        foreach (var symbol in publics)
        {
            var name = Encoding.UTF8.GetBytes(symbol.Name);
            // S_PUB32: flags, offset, segment, NUL-terminated name, padded to 4 bytes.
            int payload = 2 + 4 + 4 + 2 + name.Length + 1;
            int padding = (4 - ((payload + 2) % 4)) % 4;
            int length = payload + padding;

            var record = new byte[length + 2];
            BinaryPrimitives.WriteUInt16LittleEndian(record, (ushort)length);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2), 0x110E);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), symbol.IsFunction ? 2u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), symbol.Offset);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(12), symbol.Segment);
            name.CopyTo(record.AsSpan(14));
            buffer.AddRange(record);
        }

        return buffer.ToArray();
    }

    /// <summary>Lays the streams out in blocks and writes the superblock, block map and directory.</summary>
    private static byte[] Assemble(byte[][] streams)
    {
        var streamBlocks = new List<uint[]>();
        int nextBlock = FirstStreamBlock;
        foreach (var stream in streams)
        {
            int count = (stream.Length + BlockSize - 1) / BlockSize;
            var blocks = new uint[count];
            for (int i = 0; i < count; i++)
            {
                blocks[i] = (uint)nextBlock++;
            }

            streamBlocks.Add(blocks);
        }

        // Directory: stream count, sizes, then each stream's block list.
        var directory = new List<byte>();
        void U32(int value) => directory.AddRange(BitConverter.GetBytes(value));
        U32(streams.Length);
        foreach (var stream in streams)
        {
            U32(stream.Length);
        }

        foreach (var blocks in streamBlocks)
        {
            foreach (uint block in blocks)
            {
                U32((int)block);
            }
        }

        var file = new byte[nextBlock * BlockSize];

        MsfFile.Signature.CopyTo(file);
        var super = file.AsSpan(MsfFile.Signature.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(super, BlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(super[4..], 2);                       // free block map
        BinaryPrimitives.WriteUInt32LittleEndian(super[8..], (uint)nextBlock);         // block count
        BinaryPrimitives.WriteUInt32LittleEndian(super[12..], (uint)directory.Count);  // directory bytes
        BinaryPrimitives.WriteUInt32LittleEndian(super[16..], 0);                      // unused
        BinaryPrimitives.WriteUInt32LittleEndian(super[20..], BlockMapBlock);

        // The block map holds the directory's own block list.
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(BlockMapBlock * BlockSize), DirectoryBlock);
        directory.ToArray().CopyTo(file.AsSpan(DirectoryBlock * BlockSize));

        for (int i = 0; i < streams.Length; i++)
        {
            if (streams[i].Length > 0)
            {
                streams[i].CopyTo(file.AsSpan((int)streamBlocks[i][0] * BlockSize));
            }
        }

        return file;
    }
}
