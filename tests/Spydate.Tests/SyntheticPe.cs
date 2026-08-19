using System.Buffers.Binary;
using Spydate.Core.PE;

namespace Spydate.Tests;

/// <summary>
/// Builds minimal, hand-assembled PE32+ images so parser edge cases can be exercised without
/// depending on the shape of a real binary.
/// </summary>
internal static class SyntheticPe
{
    private const int LfaNew = 0x80;
    private const int SectionDataOffset = 0x400;
    private const int SectionDataSize = 0x200;
    private const uint SectionRva = 0x1000;
    private const int FileSize = SectionDataOffset + SectionDataSize;

    /// <summary>
    /// An image whose base relocation directory points at a single block with the given (possibly
    /// nonsensical) size, followed by three Dir64 fix-ups and zero padding.
    /// </summary>
    public static PeImage WithRelocationBlock(uint pageRva, uint blockSize, uint directorySize = 0x1000)
    {
        var file = NewImage(DataDirectoryIndex.BaseRelocation, SectionRva, directorySize);

        var block = file.AsSpan(SectionDataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(block, pageRva);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], blockSize);
        ushort Dir64(uint offset) => (ushort)(((uint)RelocationType.Dir64 << 12) | (offset & 0x0FFF));
        BinaryPrimitives.WriteUInt16LittleEndian(block[8..], Dir64(0x10));
        BinaryPrimitives.WriteUInt16LittleEndian(block[10..], Dir64(0x20));
        BinaryPrimitives.WriteUInt16LittleEndian(block[12..], 0); // IMAGE_REL_BASED_ABSOLUTE padding
        BinaryPrimitives.WriteUInt16LittleEndian(block[14..], Dir64(0x30));

        return PeImage.Parse(file);
    }

    /// <summary>A PE32+ image with one read-only section and one populated data directory.</summary>
    private static byte[] NewImage(DataDirectoryIndex directory, uint directoryRva, uint directorySize)
    {
        var file = new byte[FileSize];
        var span = file.AsSpan();

        // --- DOS header -------------------------------------------------
        span[0] = (byte)'M';
        span[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x3C..], LfaNew);

        // --- NT headers -------------------------------------------------
        var nt = span[LfaNew..];
        BinaryPrimitives.WriteUInt32LittleEndian(nt, 0x0000_4550); // "PE\0\0"

        var coff = nt[4..];
        BinaryPrimitives.WriteUInt16LittleEndian(coff, (ushort)MachineType.Amd64);
        BinaryPrimitives.WriteUInt16LittleEndian(coff[2..], 1);      // NumberOfSections
        BinaryPrimitives.WriteUInt16LittleEndian(coff[16..], 0xF0);  // SizeOfOptionalHeader
        BinaryPrimitives.WriteUInt16LittleEndian(coff[18..], 0x2022); // Characteristics: executable, DLL, large address aware

        var opt = coff[20..];
        BinaryPrimitives.WriteUInt16LittleEndian(opt, 0x020B);        // PE32+
        BinaryPrimitives.WriteUInt32LittleEndian(opt[16..], 0);       // AddressOfEntryPoint
        BinaryPrimitives.WriteUInt64LittleEndian(opt[24..], 0x1_4000_0000); // ImageBase
        BinaryPrimitives.WriteUInt32LittleEndian(opt[32..], 0x1000);  // SectionAlignment
        BinaryPrimitives.WriteUInt32LittleEndian(opt[36..], 0x200);   // FileAlignment
        BinaryPrimitives.WriteUInt32LittleEndian(opt[56..], 0x2000);  // SizeOfImage
        BinaryPrimitives.WriteUInt32LittleEndian(opt[60..], 0x400);   // SizeOfHeaders
        BinaryPrimitives.WriteUInt16LittleEndian(opt[68..], 3);       // Subsystem: console
        BinaryPrimitives.WriteUInt32LittleEndian(opt[108..], 16);     // NumberOfRvaAndSizes

        var directories = opt[112..];
        BinaryPrimitives.WriteUInt32LittleEndian(directories[((int)directory * 8)..], directoryRva);
        BinaryPrimitives.WriteUInt32LittleEndian(directories[(((int)directory * 8) + 4)..], directorySize);

        // --- Section table ----------------------------------------------
        var section = opt[0xF0..];
        ".rdata"u8.CopyTo(section);
        BinaryPrimitives.WriteUInt32LittleEndian(section[8..], SectionDataSize);      // VirtualSize
        BinaryPrimitives.WriteUInt32LittleEndian(section[12..], SectionRva);          // VirtualAddress
        BinaryPrimitives.WriteUInt32LittleEndian(section[16..], SectionDataSize);     // SizeOfRawData
        BinaryPrimitives.WriteUInt32LittleEndian(section[20..], SectionDataOffset);   // PointerToRawData
        BinaryPrimitives.WriteUInt32LittleEndian(section[36..], 0x4000_0040);         // initialized data, read

        return file;
    }
}
