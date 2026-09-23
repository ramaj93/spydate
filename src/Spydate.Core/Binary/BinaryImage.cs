using System.IO.Compression;
using Spydate.Core.Elf;
using Spydate.Core.PE;

namespace Spydate.Core.Binary;

/// <summary>A file could not be opened as a binary. The message says why, in words a person can act on.</summary>
public abstract class BinaryParseException : Exception
{
    protected BinaryParseException(string message)
        : base(message)
    {
    }

    protected BinaryParseException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The file is a binary of a kind Spydate recognises but does not open yet. Said as such, rather than as
/// "not a valid PE" — a person holding an ELF deserves to hear it is an ELF.
/// </summary>
public sealed class UnsupportedFormatException : BinaryParseException
{
    public UnsupportedFormatException(string path, BinaryFormat format)
        : base($"{System.IO.Path.GetFileName(path)} is {Describe(format)}, which Spydate cannot open yet.")
    {
        Format = format;
    }

    public BinaryFormat Format { get; }

    private static string Describe(BinaryFormat format) => format switch
    {
        BinaryFormat.Elf => "an ELF binary (a Linux, BSD or Android native program or library)",
        BinaryFormat.Jar => "a Java archive (JAR)",
        BinaryFormat.Apk => "an Android package (APK)",
        _ => "not a format Spydate recognises",
    };
}

/// <summary>
/// The one way a binary is opened: recognise the container from its first bytes, then hand it to that format's
/// parser. Everything downstream works on the <see cref="IBinaryImage"/> it returns.
/// </summary>
public static class BinaryImage
{
    /// <summary>How much of the file recognising it reads.</summary>
    private const int HeadLength = 4096;

    /// <summary>
    /// The format the first bytes announce. A zip is reported as <see cref="BinaryFormat.Jar"/> here, since the
    /// head alone cannot tell a JAR from an APK — or from a .docx; <see cref="Detect"/> looks inside to decide.
    /// </summary>
    public static BinaryFormat Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 2 && head[0] == (byte)'M' && head[1] == (byte)'Z')
        {
            return BinaryFormat.Pe;
        }

        if (head.Length >= 4 && head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F')
        {
            return BinaryFormat.Elf;
        }

        if (head.Length >= 4 && head[0] == (byte)'P' && head[1] == (byte)'K' && head[2] == 3 && head[3] == 4)
        {
            return BinaryFormat.Jar;
        }

        return BinaryFormat.Unknown;
    }

    /// <summary>
    /// The format of a file on disk. A zip is only called a JAR or an APK when its contents say so — a Word
    /// document is a zip too, and naming it a Java archive would be a confident wrong answer. Anything that cannot
    /// be read or recognised is <see cref="BinaryFormat.Unknown"/>.
    /// </summary>
    public static BinaryFormat Detect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        byte[] head = new byte[HeadLength];
        int read;
        try
        {
            using var stream = File.OpenRead(path);
            read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BinaryFormat.Unknown;   // the parser that follows reports the read failure in its own words
        }

        var format = Sniff(head.AsSpan(0, read));
        return format == BinaryFormat.Jar ? ZipKind(path) : format;
    }

    /// <summary>
    /// Opens a binary. A PE or an ELF is parsed; a recognised format Spydate does not open yet is refused by name.
    /// A file nothing recognises still goes to the PE parser, whose error explains what it found where a header
    /// should be — the same answer opening an unknown file has always given.
    /// </summary>
    public static IBinaryImage Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var format = Detect(path);
        if (format == BinaryFormat.Elf)
        {
            return ElfImage.Load(path);
        }

        if (format is BinaryFormat.Jar or BinaryFormat.Apk)
        {
            throw new UnsupportedFormatException(path, format);
        }

        return PeImage.Load(path);
    }

    /// <summary>The processor, in the format's own words: <c>Amd64</c> for a PE, <c>X86_64</c> for an ELF.</summary>
    public static string MachineName(IBinaryImage image) => image switch
    {
        PeImage pe => pe.Machine.ToString(),
        ElfImage elf => elf.Header.MachineName,
        _ => image.Architecture.ToString(),
    };

    /// <summary>The container and its width: <c>PE32+</c>, <c>ELF32</c>.</summary>
    public static string ContainerName(IBinaryImage image) => image switch
    {
        PeImage pe => pe.Is64Bit ? "PE32+" : "PE32",
        ElfImage elf => elf.Header.ClassName,
        _ => image.Format.ToString(),
    };

    /// <summary>An APK has a manifest and Dalvik code; a JAR has a Java manifest or classes; anything else is neither.</summary>
    private static BinaryFormat ZipKind(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            bool manifest = false;
            bool dex = false;
            bool java = false;
            foreach (var entry in zip.Entries)
            {
                string name = entry.FullName;
                manifest |= name.Equals("AndroidManifest.xml", StringComparison.OrdinalIgnoreCase);
                dex |= name.EndsWith(".dex", StringComparison.OrdinalIgnoreCase);
                java |= name.Equals("META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".class", StringComparison.OrdinalIgnoreCase);
            }

            return manifest && dex ? BinaryFormat.Apk : java ? BinaryFormat.Jar : BinaryFormat.Unknown;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return BinaryFormat.Unknown;   // a damaged zip is not a JAR anybody can open
        }
    }
}
