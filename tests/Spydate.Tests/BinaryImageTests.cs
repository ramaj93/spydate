using System.IO.Compression;
using System.Text;
using Spydate.Core.Binary;
using Spydate.Core.PE;
using Spydate.Core.Project;

namespace Spydate.Tests;

/// <summary>
/// The seam between "a binary" and "a PE file". A PE seen through <see cref="IBinaryImage"/> must say exactly
/// what it says through its own members — the analysis now reads the interface, so any disagreement is a
/// behaviour change hiding behind a refactor. And recognising a file must name what it is, without changing
/// the answer for anything it does not recognise.
/// </summary>
public sealed class BinaryImageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "spydate-image-" + Guid.NewGuid().ToString("N")[..8]);

    public BinaryImageTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that will not go away is not a test failure.
        }
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Zip(string name, params string[] entries)
    {
        string path = Path.Combine(_directory, name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (string entry in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
                writer.Write("x");
            }
        }

        return path;
    }

    // --- a PE through the interface -------------------------------------

    [Fact]
    public void APeSeenThroughTheInterfaceSaysWhatItSaysItself()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var pe = Corpus.Image(Corpus.NotepadX64);
        IBinaryImage image = pe;

        Assert.Equal(BinaryFormat.Pe, image.Format);
        Assert.Equal(Architecture.X64, image.Architecture);
        Assert.Equal(pe.OptionalHeader.SizeOfImage, image.ImageSize);
        Assert.Equal(pe.IsDll, image.IsLibrary);
        Assert.Equal(pe.EntryPointVa, image.EntryPointVa);

        Assert.Equal(pe.Sections.Count, image.Sections.Count);
        for (int i = 0; i < pe.Sections.Count; i++)
        {
            var header = pe.Sections[i];
            var section = image.Sections[i];
            Assert.Equal(header.VirtualAddress, section.Rva);
            Assert.Equal(header.VirtualExtent, section.Extent);
            Assert.Equal(header.VirtualSize, section.VirtualSize);
            Assert.Equal(header.PointerToRawData, section.RawOffset);
            Assert.Equal(header.SizeOfRawData, section.RawSize);
            Assert.Equal(header.IsExecutable, section.IsExecutable);
        }

        int functions = pe.Imports.Concat(pe.DelayImports).Sum(m => m.Functions.Count);
        Assert.Equal(functions, image.Imports.Count);
        Assert.Equal(pe.Exports?.Entries.Count ?? 0, image.Exports.Count);
    }

    [Fact]
    public void ImportsKeepTheirOrderNormalThenDelayLoaded()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var pe = Corpus.Image(Corpus.NotepadX64);
        var expected = pe.Imports.Concat(pe.DelayImports)
            .SelectMany(m => m.Functions.Select(f => (m.Name, f.IatRva)))
            .ToList();
        var actual = ((IBinaryImage)pe).Imports.Select(i => (i.Module, i.SlotRva)).ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UnwindRangesAreTheNonChainedPdataEntries()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var pe = Corpus.Image(Corpus.NotepadX64);
        int expected = pe.ExceptionTable.Count(rf => !rf.IsChained && rf.BeginRva != 0);

        Assert.True(expected > 0, "an x64 notepad has a .pdata table");
        Assert.Equal(expected, pe.UnwindRanges.Count);
    }

    // --- recognising a file ---------------------------------------------

    [Fact]
    public void ThePeMagicIsRecognised()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        Assert.Equal(BinaryFormat.Pe, BinaryImage.Detect(Corpus.NotepadX64));
        Assert.IsType<PeImage>(BinaryImage.Load(Corpus.NotepadX64));
    }

    [Fact]
    public void AnElfIsNamedAndRefused()
    {
        string path = Write("tool", [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        Assert.Equal(BinaryFormat.Elf, BinaryImage.Detect(path));
        var ex = Assert.Throws<UnsupportedFormatException>(() => BinaryImage.Load(path));
        Assert.Equal(BinaryFormat.Elf, ex.Format);
        Assert.Contains("ELF", ex.Message);
    }

    [Fact]
    public void AZipIsAJarOnlyWhenItHoldsJava()
    {
        Assert.Equal(BinaryFormat.Jar, BinaryImage.Detect(Zip("lib.jar", "META-INF/MANIFEST.MF", "a/B.class")));
        Assert.Equal(BinaryFormat.Apk, BinaryImage.Detect(Zip("app.apk", "AndroidManifest.xml", "classes.dex")));

        // A Word document is a zip too. Calling it a Java archive would be a confident wrong answer.
        Assert.Equal(BinaryFormat.Unknown, BinaryImage.Detect(Zip("letter.docx", "word/document.xml")));
    }

    [Fact]
    public void AnythingUnrecognisedGetsThePeParsersOwnAnswer()
    {
        // Unchanged behaviour: a file nothing recognises still reaches the PE parser, whose error says what
        // it found where a header should be — the same error opening it has always produced.
        string garbage = Write("notes.txt", Encoding.ASCII.GetBytes("just some text, not a binary"));
        string docx = Zip("letter.docx", "word/document.xml");

        Assert.Throws<PeParseException>(() => BinaryImage.Load(garbage));
        Assert.Throws<PeParseException>(() => BinaryImage.Load(docx));
    }

    [Fact]
    public void BothRefusalsShareOneBaseSoOpenCanCatchEither()
    {
        string elf = Write("tool", [0x7F, (byte)'E', (byte)'L', (byte)'F']);
        string garbage = Write("junk.bin", [1, 2, 3, 4]);

        Assert.ThrowsAny<BinaryParseException>(() => BinaryImage.Load(elf));
        Assert.ThrowsAny<BinaryParseException>(() => BinaryImage.Load(garbage));
    }

    [Fact]
    public async Task OpenBinaryNamesAnElfAndPointsAtReadFile()
    {
        // Without the shared base the refusal would escape open_binary as an unhandled exception. It must be
        // an answer instead — naming the format, and naming the tool that can still read the bytes.
        string elf = Write("tool", [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0]);
        var tools = new Spydate.Mcp.Tools.SessionTools(new Spydate.Mcp.Session.SessionStore(), Spydate.Mcp.McpOptions.Default);

        string answer = await tools.OpenBinaryAsync(elf);

        Assert.Contains("ELF", answer, StringComparison.Ordinal);
        Assert.Contains("read_file", answer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { (byte)'M' })]
    [InlineData(new byte[] { 0x7F, (byte)'E' })]
    [InlineData(new byte[] { (byte)'P', (byte)'K', 3 })]
    public void ATruncatedHeadIsUnknownNotACrash(byte[] head)
    {
        Assert.Equal(BinaryFormat.Unknown, BinaryImage.Sniff(head));
    }

    // --- the project file still finds its binary -------------------------

    [Fact]
    public void APeFingerprintIsTheStampAndChecksumTheStoreHasAlwaysUsed()
    {
        var pe = SyntheticPe.WithSectionData(new byte[0x40]);
        string old = $"{pe.FileHeader.TimeDateStamp:X8}-{pe.OptionalHeader.CheckSum:X8}";

        Assert.Equal(old, pe.Fingerprint);
    }

    [Fact]
    public void TheUserStorePathIsUnchanged()
    {
        // Byte-for-byte the name the store used before formats were generalised, so every existing project in
        // %LOCALAPPDATA%\Spydate\Projects still resolves.
        var pe = SyntheticPe.WithSectionData(new byte[0x40]);
        string key = $"{pe.FileHeader.TimeDateStamp:X8}-{pe.OptionalHeader.CheckSum:X8}-{pe.Data.Length:X}";
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Spydate", "Projects", $"{pe.FileName}.{key}{SpydateProject.Extension}");

        Assert.Equal(expected, SpydateProject.UserStorePath(pe));
    }

    [Fact]
    public void AProjectWrittenBeforeFingerprintsStillLoads()
    {
        var pe = SyntheticPe.WithSectionData(new byte[0x40]);
        string path = Path.Combine(_directory, "old.spydate");
        File.WriteAllText(path, $$"""
            {
              "format": 1,
              "image": {
                "name": "{{pe.FileName}}",
                "size": {{pe.Data.Length}},
                "timeDateStamp": "0x{{pe.FileHeader.TimeDateStamp:X}}",
                "checkSum": "0x{{pe.OptionalHeader.CheckSum:X}}",
                "imageBase": "0x{{pe.ImageBase:X}}"
              },
              "annotations": [ { "rva": "0x1000", "name": "Kept" } ]
            }
            """);

        var store = new AnnotationStore();
        var result = SpydateProject.Load(path, pe, store);

        Assert.True(result.Loaded, result.Reason);
        Assert.Equal("Kept", store.NameFor(pe.RvaToVa(0x1000)));
    }

    [Fact]
    public void APeProjectIsWrittenInTheSameShapeAsBefore()
    {
        // No new member for a PE: its fingerprint is its stamp and checksum, which the file already records.
        // A project people keep in version control must not grow a line because of a refactor.
        var pe = SyntheticPe.WithSectionData(new byte[0x40]);
        var store = new AnnotationStore();
        store.SetName(pe.RvaToVa(0x1000), "Named");
        string path = Path.Combine(_directory, "new.spydate");

        SpydateProject.SaveTo(path, pe, store);
        string json = File.ReadAllText(path);

        Assert.Contains("\"timeDateStamp\"", json);
        Assert.Contains("\"checkSum\"", json);
        Assert.DoesNotContain("\"fingerprint\"", json);
    }

    [Fact]
    public void AMismatchIsStillDescribedAsStampAndChecksum()
    {
        var identity = new ProjectIdentity("a.exe", 4096, "5F5E1000-0001ABCD");

        Assert.Equal("a.exe, 4096 bytes, stamp 0x5F5E1000, checksum 0x0001ABCD", identity.Describe());
        Assert.Equal("b.so, 10 bytes, build 0123abcd", new ProjectIdentity("b.so", 10, "0123abcd").Describe());
    }
}
