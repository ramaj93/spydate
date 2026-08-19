using System.Diagnostics;
using Spydate.Core.PE;
using Xunit.Abstractions;

namespace Spydate.Tests;

/// <summary>Decoding version blocks, manifests and string tables out of resource bytes.</summary>
public class ResourceDecoderTests
{
    private readonly ITestOutputHelper _output;

    public ResourceDecoderTests(ITestOutputHelper output) => _output = output;

    private static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);

    private static IEnumerable<ResourceNode> Leaves(ResourceNode? node)
    {
        if (node is null)
        {
            yield break;
        }

        if (!node.IsDirectory)
        {
            yield return node;
            yield break;
        }

        foreach (var child in node.Children!)
        {
            foreach (var leaf in Leaves(child))
            {
                yield return leaf;
            }
        }
    }

    private static ResourceNode? TypeNode(PeImage pe, ResourceType type)
        => pe.Resources?.Children?.FirstOrDefault(c => c.Name is null && c.Id == (uint)type);

    [SkippableTheory]
    [InlineData("kernel32.dll")]
    [InlineData("user32.dll")]
    [InlineData("notepad.exe")]
    public void VersionInfoMatchesWindowsOwnReader(string fileName)
    {
        // Windows parses the same block through FileVersionInfo, which makes it an oracle.
        string path = Path.Combine(System32, fileName);
        Skip.IfNot(File.Exists(path), $"{fileName} not found");

        var pe = PeImage.Load(path);
        var versionNode = TypeNode(pe, ResourceType.Version);
        Skip.If(versionNode is null, "no version resource");

        var leaf = Leaves(versionNode).First();
        var info = ResourceDecoder.ReadVersionInfo(ResourceDecoder.ReadData(pe, leaf).Span);
        Assert.NotNull(info);

        var expected = FileVersionInfo.GetVersionInfo(path);
        _output.WriteLine($"{fileName}: {info!.FileVersion} / {info.ProductVersion} — {info.CompanyName} — {info.FileDescription}");

        Assert.Equal(expected.FileMajorPart, info.FileVersion!.Major);
        Assert.Equal(expected.FileMinorPart, info.FileVersion.Minor);
        Assert.Equal(expected.FileBuildPart, info.FileVersion.Build);
        Assert.Equal(expected.FilePrivatePart, info.FileVersion.Revision);
        Assert.Equal(expected.ProductMajorPart, info.ProductVersion!.Major);
        Assert.Equal(expected.CompanyName?.Trim(), info.CompanyName?.Trim());

        // Windows merges MUI resources when it reads a localised binary: for notepad.exe it
        // reports NOTEPAD.EXE.MUI, the satellite file, where the binary itself says NOTEPAD.EXE.
        // A reverse-engineering tool must report what is in the file, so ours is the prefix.
        Assert.StartsWith(info.OriginalFilename!.Trim(), expected.OriginalFilename!.Trim(), StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(info.StringTables);
    }

    [SkippableFact]
    public void ManifestDecodesAsXml()
    {
        string path = Path.Combine(System32, "notepad.exe");
        Skip.IfNot(File.Exists(path), "notepad.exe not found");

        var pe = PeImage.Load(path);
        var manifestNode = TypeNode(pe, ResourceType.Manifest);
        Skip.If(manifestNode is null, "no manifest");

        string xml = ResourceDecoder.ReadManifest(ResourceDecoder.ReadData(pe, Leaves(manifestNode).First()).Span);
        _output.WriteLine(xml.Length > 300 ? xml[..300] + "…" : xml);

        Assert.StartsWith("<?xml", xml.TrimStart(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("assembly", xml, StringComparison.OrdinalIgnoreCase);
        // It must parse as real XML, not just look like it.
        var document = System.Xml.Linq.XDocument.Parse(xml);
        Assert.Equal("assembly", document.Root!.Name.LocalName);
    }

    [Fact]
    public void StringTableIdsFollowTheBlockNumbering()
    {
        // An RT_STRING block is 16 length-prefixed UTF-16 slots; block N holds ids (N-1)*16 + slot.
        var data = new List<byte>();
        void Slot(string? text)
        {
            if (text is null)
            {
                data.AddRange(BitConverter.GetBytes((ushort)0));
                return;
            }

            data.AddRange(BitConverter.GetBytes((ushort)text.Length));
            data.AddRange(System.Text.Encoding.Unicode.GetBytes(text));
        }

        Slot("first");
        Slot(null);
        Slot(null);
        Slot("fourth");
        for (int i = 4; i < 15; i++)
        {
            Slot(null);
        }

        Slot("last");

        var strings = ResourceDecoder.ReadStringTable(data.ToArray(), blockId: 5);

        Assert.Equal(3, strings.Count);
        Assert.Equal(new ResourceString(64, "first"), strings[0]);
        Assert.Equal(new ResourceString(67, "fourth"), strings[1]);
        Assert.Equal(new ResourceString(79, "last"), strings[2]);
    }

    [Fact]
    public void StringTableStopsAtTheEndOfTheData()
    {
        // A slot claiming more characters than remain must not read past the block.
        var data = new List<byte>();
        data.AddRange(BitConverter.GetBytes((ushort)100));
        data.AddRange(System.Text.Encoding.Unicode.GetBytes("short"));

        var strings = ResourceDecoder.ReadStringTable(data.ToArray(), blockId: 1);

        Assert.Equal("short", Assert.Single(strings).Text);
    }

    [SkippableFact]
    public void RealStringTableDecodes()
    {
        // Localised system binaries move their strings into MUI satellites, so scan for any
        // System32 binary that still carries an RT_STRING block.
        PeImage? image = null;
        ResourceNode? stringNode = null;

        foreach (string candidate in Directory.EnumerateFiles(System32, "*.dll").Take(60))
        {
            PeImage pe2;
            try
            {
                pe2 = PeImage.Load(candidate);
            }
            catch (PeParseException)
            {
                continue;
            }

            if (TypeNode(pe2, ResourceType.String) is { Children.Count: > 0 } node)
            {
                image = pe2;
                stringNode = node;
                break;
            }
        }

        Skip.If(stringNode is null, "no System32 binary with an RT_STRING block");
        _output.WriteLine($"using {image!.FileName}");

        int decoded = 0;
        foreach (var block in stringNode!.Children!)
        {
            var leaf = Leaves(block).FirstOrDefault();
            if (leaf is null)
            {
                continue;
            }

            var strings = ResourceDecoder.ReadStringTable(ResourceDecoder.ReadData(image, leaf).Span, block.Id);
            uint first = (block.Id - 1) * 16;
            Assert.All(strings, s => Assert.InRange(s.Id, first, first + 15));
            decoded += strings.Count;

            if (decoded > 0 && strings.Count > 0)
            {
                _output.WriteLine($"block #{block.Id}: " + string.Join(" | ", strings.Take(3).Select(s => $"{s.Id}={s.Text}")));
                break;
            }
        }

        Assert.True(decoded > 0, "no strings decoded");
    }

    [Fact]
    public void GarbageIsRejectedRatherThanThrowing()
    {
        var random = new byte[512];
        new Random(7).NextBytes(random);

        Assert.Null(ResourceDecoder.ReadVersionInfo(random));
        Assert.NotNull(ResourceDecoder.ReadManifest(random));           // any bytes decode as text
        Assert.NotNull(ResourceDecoder.ReadStringTable(random, 1));     // bounded by the 16 slots
        Assert.Null(ResourceDecoder.ReadVersionInfo(ReadOnlySpan<byte>.Empty));
        Assert.Empty(ResourceDecoder.ReadStringTable(ReadOnlySpan<byte>.Empty, 1));
    }

    [Fact]
    public void TruncatedVersionBlockIsRejected()
    {
        // A well-formed header claiming a huge length, then nothing: the key never completes,
        // so this is not a version block.
        var data = new byte[16];
        BitConverter.GetBytes((ushort)0xFFFF).CopyTo(data, 0); // wLength
        BitConverter.GetBytes((ushort)52).CopyTo(data, 2);     // wValueLength
        BitConverter.GetBytes((ushort)0).CopyTo(data, 4);      // wType
        System.Text.Encoding.Unicode.GetBytes("VS_").CopyTo(data, 6);

        Assert.Null(ResourceDecoder.ReadVersionInfo(data));
    }
}
