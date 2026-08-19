using System.Buffers.Binary;
using System.Text;
using Spydate.Core.PE;

namespace Spydate.Core.Pdb;

/// <summary>A public symbol from a PDB: a name and the section-relative address it lives at.</summary>
public readonly record struct PdbPublicSymbol(string Name, ushort Segment, uint Offset, bool IsFunction)
{
    public override string ToString() => $"{Name} @ {Segment}:{Offset:X}";
}

/// <summary>
/// A native (MSF) PDB, read far enough to answer the question that matters for disassembly: what is
/// this address called? That means the info stream, for identity, and the public symbol records.
/// Types, line numbers and per-module symbols are not read.
/// </summary>
public sealed class PdbFile
{
    private const int InfoStream = 1;
    private const int DbiStream = 3;
    private const int DbiHeaderSize = 64;
    private const ushort SPub32 = 0x110E;
    private const uint PublicSymbolIsFunction = 0x00000002;

    private PdbFile(string path, Guid guid, uint age, uint signature, IReadOnlyList<PdbPublicSymbol> symbols)
    {
        Path = path;
        Guid = guid;
        Age = age;
        Signature = signature;
        PublicSymbols = symbols;
    }

    public string Path { get; }

    /// <summary>Identity shared with the image's CodeView record.</summary>
    public Guid Guid { get; }

    public uint Age { get; }

    public uint Signature { get; }

    public IReadOnlyList<PdbPublicSymbol> PublicSymbols { get; }

    /// <summary>Whether this PDB is the one built alongside <paramref name="codeView"/>'s image.</summary>
    public bool Matches(CodeViewInfo codeView) => codeView.Guid == Guid && codeView.Age == Age;

    /// <summary>Loads a PDB from disk. Throws <see cref="PdbParseException"/> for anything unusable.</summary>
    public static PdbFile Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PdbParseException($"Cannot read '{path}': {ex.Message}");
        }

        return Parse(bytes, path);
    }

    /// <summary>Loads a PDB, returning null (with a reason) instead of throwing.</summary>
    public static PdbFile? TryLoad(string path, out string? error)
    {
        try
        {
            error = null;
            return Load(path);
        }
        catch (PdbParseException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static PdbFile Parse(ReadOnlyMemory<byte> data, string path = "<memory>")
    {
        var msf = MsfFile.Parse(data);

        // Stream 1: version, signature, age, then the GUID that ties the PDB to its image.
        var info = msf.ReadStream(InfoStream).Span;
        if (info.Length < 28)
        {
            throw new PdbParseException("PDB info stream is missing or truncated.");
        }

        uint signature = BinaryPrimitives.ReadUInt32LittleEndian(info[4..]);
        uint age = BinaryPrimitives.ReadUInt32LittleEndian(info[8..]);
        var guid = new Guid(info.Slice(12, 16));

        return new PdbFile(path, guid, age, signature, ReadPublicSymbols(msf));
    }

    /// <summary>
    /// Public symbols live in the record stream the DBI header points at, as a run of
    /// length-prefixed records; S_PUB32 is the one carrying names for linked addresses.
    /// </summary>
    private static IReadOnlyList<PdbPublicSymbol> ReadPublicSymbols(MsfFile msf)
    {
        var dbi = msf.ReadStream(DbiStream).Span;
        if (dbi.Length < DbiHeaderSize)
        {
            return Array.Empty<PdbPublicSymbol>();
        }

        int signature = BinaryPrimitives.ReadInt32LittleEndian(dbi);
        if (signature != -1)
        {
            throw new PdbParseException("DBI stream has an unexpected signature (PDB 2.0 is not supported).");
        }

        ushort symbolRecordStream = BinaryPrimitives.ReadUInt16LittleEndian(dbi[20..]);
        var records = msf.ReadStream(symbolRecordStream).Span;
        var symbols = new List<PdbPublicSymbol>();

        int offset = 0;
        while (offset + 4 <= records.Length)
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(records[offset..]);
            if (length < 2)
            {
                break; // a zero-length record would loop forever
            }

            ushort kind = BinaryPrimitives.ReadUInt16LittleEndian(records[(offset + 2)..]);
            int next = offset + 2 + length; // the length field itself is not counted

            if (kind == SPub32 && offset + 14 <= records.Length)
            {
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(records[(offset + 4)..]);
                uint symbolOffset = BinaryPrimitives.ReadUInt32LittleEndian(records[(offset + 8)..]);
                ushort segment = BinaryPrimitives.ReadUInt16LittleEndian(records[(offset + 12)..]);
                string name = ReadNulTerminated(records, offset + 14, Math.Min(next, records.Length));
                if (name.Length > 0 && segment > 0)
                {
                    symbols.Add(new PdbPublicSymbol(name, segment, symbolOffset, (flags & PublicSymbolIsFunction) != 0));
                }
            }

            if (next <= offset)
            {
                break;
            }

            offset = next;
        }

        return symbols;
    }

    private static string ReadNulTerminated(ReadOnlySpan<byte> data, int start, int end)
    {
        if (start >= end || start >= data.Length)
        {
            return string.Empty;
        }

        end = Math.Min(end, data.Length);
        int length = data[start..end].IndexOf((byte)0);
        return Encoding.UTF8.GetString(data.Slice(start, length < 0 ? end - start : length));
    }

    /// <summary>
    /// Where the image would keep its PDB: the path recorded at build time, then the same file name
    /// next to the image, which is where it ends up after being copied around.
    /// </summary>
    public static IEnumerable<string> ProbePaths(PeImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var codeView = image.Debug.Select(d => d.CodeView).FirstOrDefault(cv => cv is not null);
        string? recorded = codeView?.PdbPath;

        if (!string.IsNullOrWhiteSpace(recorded))
        {
            yield return recorded;

            string? directory = image.Path is null ? null : System.IO.Path.GetDirectoryName(image.Path);
            if (directory is not null)
            {
                yield return System.IO.Path.Combine(directory, System.IO.Path.GetFileName(recorded));
            }
        }

        if (image.Path is { } imagePath)
        {
            yield return System.IO.Path.ChangeExtension(imagePath, ".pdb");
        }
    }
}
