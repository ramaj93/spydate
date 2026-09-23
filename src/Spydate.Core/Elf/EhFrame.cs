namespace Spydate.Core.Elf;

/// <summary>
/// Reads function extents out of <c>.eh_frame</c>, the unwind table every modern Linux toolchain emits even for
/// C (it is what makes a backtrace through a stripped binary work). Each FDE declares one function's first
/// address and length — the ELF counterpart of a PE's <c>.pdata</c>, and like it, exact where the compiler
/// wrote it, which beats anything discovery can infer.
///
/// Only the parts needed for the extent are read: the CIE's augmentation (for how the FDE encodes its
/// addresses) and the FDE's initial location and range. The call-frame instructions are skipped.
/// </summary>
internal static class EhFrame
{
    private const int MaxEntries = 1_000_000;

    private const byte OmitEncoding = 0xFF;

    public static IReadOnlyList<(uint BeginRva, uint EndRva)> Read(ElfImage image)
    {
        if (image.Header.Type == ElfType.Relocatable)
        {
            // An object file's FDEs point at code through relocations that have not been applied: every start is zero.
            return Array.Empty<(uint, uint)>();
        }

        if (Locate(image) is not { } frame)
        {
            return Array.Empty<(uint, uint)>();
        }

        var (offset, size, va) = frame;
        var data = image.Data.Span;
        long end = Math.Min(offset + size, data.Length);
        var ranges = new List<(uint, uint)>();
        var cies = new Dictionary<long, byte>();
        var r = new ElfReader(data, image.Is64Bit, image.Header.IsBigEndian, offset);

        for (int n = 0; n < MaxEntries && r.Position + 4 <= end; n++)
        {
            long start = r.Position;
            ulong length = r.U32();
            if (length == 0)
            {
                break;   // the terminator
            }

            bool wide = length == 0xFFFFFFFF;
            if (wide)
            {
                length = r.U64();
            }

            long idAt = r.Position;
            long next = idAt + (long)Math.Min(length, (ulong)(end - idAt));
            if (length > (ulong)(end - idAt))
            {
                break;
            }

            ulong id = wide ? r.U64() : r.U32();
            if (id == 0)
            {
                cies[start] = FdeEncoding(ref r);
            }
            else
            {
                // The CIE pointer counts back from the field it is in.
                long cie = idAt - (long)id;
                if (!cies.TryGetValue(cie, out byte encoding))
                {
                    long saved = r.Position;
                    try
                    {
                        r.Seek(cie);
                        ulong cieLength = r.U32();
                        if (cieLength == 0xFFFFFFFF)
                        {
                            r.U64();
                            r.U64();
                        }
                        else
                        {
                            r.U32();
                        }

                        encoding = FdeEncoding(ref r);
                    }
                    catch (ElfParseException)
                    {
                        encoding = OmitEncoding;
                    }

                    cies[cie] = encoding;
                    r.Seek(saved);
                }

                if (encoding != OmitEncoding)
                {
                    ulong fieldVa = va + (ulong)(r.Position - offset);
                    ulong begin = ReadEncoded(ref r, encoding, fieldVa, image.Is64Bit);
                    ulong range = ReadEncoded(ref r, (byte)(encoding & 0x0F), 0, image.Is64Bit);
                    // The linker gives the whole PLT one FDE. It is a table of stubs, not a function, and bounding
                    // it as one would swallow every stub into a single "function" the size of the section.
                    if (begin != 0 && range is > 0 and < 0x1000_0000
                        && image.VaToRva(begin) is { } rva
                        && image.SectionFromRva(rva) is { IsExecutable: true } section
                        && !section.Name.StartsWith(".plt", StringComparison.Ordinal))
                    {
                        ranges.Add((rva, (uint)Math.Min(rva + range, uint.MaxValue)));
                    }
                }
            }

            r.Seek(next);
        }

        return ranges;
    }

    /// <summary>Where <c>.eh_frame</c> is: its section, or failing that, the pointer in <c>.eh_frame_hdr</c>.</summary>
    private static (long Offset, long Size, ulong Va)? Locate(ElfImage image)
    {
        if (image.SectionHeader(".eh_frame") is { HasNoBits: false, Size: > 0 } section)
        {
            return ((long)Math.Min(section.Offset, (ulong)image.Length), (long)Math.Min(section.Size, (ulong)image.Length), image.AddressOf(section));
        }

        var header = image.Segments.FirstOrDefault(s => s.Type == (uint)SegmentType.GnuEhFrame);
        if (header is null || header.Offset + 8 > (ulong)image.Length)
        {
            return null;
        }

        var r = new ElfReader(image.Data.Span, image.Is64Bit, image.Header.IsBigEndian, (long)header.Offset);
        if (r.U8() != 1)
        {
            return null;   // eh_frame_hdr version
        }

        byte pointerEncoding = r.U8();
        r.U8();
        r.U8();
        ulong frameVa = ReadEncoded(ref r, pointerEncoding, header.VirtualAddress + 4, image.Is64Bit);
        if (image.VaToOffset(frameVa) is not { } offset)
        {
            return null;
        }

        // The header does not say how long the table is; it runs to its terminator, within the file.
        return (offset, image.Length - offset, frameVa);
    }

    /// <summary>Reads a CIE from just after its id and returns the encoding its FDEs use for addresses.</summary>
    private static byte FdeEncoding(ref ElfReader r)
    {
        byte version = r.U8();
        string augmentation = r.Z(64);
        if (augmentation.Contains("eh", StringComparison.Ordinal))
        {
            r.Word();
        }

        if (version >= 4)
        {
            r.U8();   // address size
            r.U8();   // segment selector size
        }

        r.ULeb128();   // code alignment
        r.SLeb128();   // data alignment
        if (version == 1)
        {
            r.U8();
        }
        else
        {
            r.ULeb128();   // return address register
        }

        byte encoding = 0;   // absolute, pointer-sized, when the CIE does not say
        if (!augmentation.StartsWith('z'))
        {
            return augmentation.Length == 0 ? encoding : OmitEncoding;
        }

        r.ULeb128();   // augmentation data length
        foreach (char c in augmentation.AsSpan(1))
        {
            switch (c)
            {
                case 'R':
                    encoding = r.U8();
                    break;
                case 'L':
                    r.U8();
                    break;
                case 'P':
                    byte personality = r.U8();
                    ReadEncoded(ref r, (byte)(personality & 0x7F), 0, r.Is64Bit);
                    break;
                case 'S' or 'B' or 'G':
                    break;
                default:
                    return OmitEncoding;   // an augmentation we cannot size: stop trusting this CIE
            }
        }

        return encoding;
    }

    /// <summary>A DWARF exception-handling pointer: a format (low bits) applied relative to something (high bits).</summary>
    private static ulong ReadEncoded(ref ElfReader r, byte encoding, ulong fieldVa, bool is64)
    {
        if (encoding == OmitEncoding)
        {
            return 0;
        }

        ulong value = (encoding & 0x0F) switch
        {
            0x00 => is64 ? r.U64() : r.U32(),
            0x01 => r.ULeb128(),
            0x02 => r.U16(),
            0x03 => r.U32(),
            0x04 => r.U64(),
            0x09 => (ulong)r.SLeb128(),
            0x0A => (ulong)(short)r.U16(),
            0x0B => (ulong)(int)r.U32(),
            0x0C => r.U64(),
            _ => throw new ElfParseException($"pointer encoding 0x{encoding:X2} is not one the unwind table may use."),
        };

        return (encoding & 0x70) switch
        {
            0x00 => value,
            0x10 => fieldVa + value,   // relative to the field itself
            _ => value,                // data- or text-relative; only the header uses them, and only for its own table
        };
    }
}
