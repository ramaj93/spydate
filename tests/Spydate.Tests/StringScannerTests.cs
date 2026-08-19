using System.Text;
using Spydate.Core.PE;
using Spydate.Core.Strings;

namespace Spydate.Tests;

public class StringScannerTests
{
    private static PeImage ImageWith(params byte[][] chunks)
    {
        var payload = chunks.SelectMany(c => c).ToArray();
        return SyntheticPe.WithSectionData(payload);
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Utf16(string s) => Encoding.Unicode.GetBytes(s);

    private static byte[] Zeros(int n) => new byte[n];

    /// <summary>Hits inside the payload section. The section name in the PE header is itself a
    /// printable run, and the scanner is right to report it - it just is not what these tests assert on.</summary>
    private static List<FoundString> InPayload(IEnumerable<FoundString> found)
        => found.Where(s => s.Section == ".rdata").ToList();

    [Fact]
    public void FindsAsciiRunsAboveTheMinimumLength()
    {
        var pe = ImageWith(Ascii("CreateFileW"), Zeros(1), Ascii("abc"), Zeros(1), Ascii("kernel32.dll"), Zeros(1));
        var found = StringScanner.Scan(pe, new StringScanOptions { MinLength = 5, ScanUtf16 = false });

        Assert.Contains(found, s => s.Text == "CreateFileW" && s.Encoding == StringEncodingKind.Ascii);
        Assert.Contains(found, s => s.Text == "kernel32.dll");
        Assert.DoesNotContain(found, s => s.Text == "abc"); // shorter than MinLength
    }

    [Fact]
    public void FindsUtf16Runs()
    {
        var pe = ImageWith(Utf16("Software\\Microsoft"), Zeros(2));
        var found = StringScanner.Scan(pe, new StringScanOptions { MinLength = 5 });

        var wide = Assert.Single(found, s => s.Encoding == StringEncodingKind.Utf16);
        Assert.Equal("Software\\Microsoft", wide.Text);
        Assert.True(wide.NullTerminated);
    }

    [Fact]
    public void Utf16TextIsNotAlsoReportedAsAscii()
    {
        // Every other byte is NUL, so ASCII runs are single characters and fall under MinLength.
        var pe = ImageWith(Utf16("RegOpenKeyExW"), Zeros(2));
        var found = InPayload(StringScanner.Scan(pe, new StringScanOptions { MinLength = 5 }));

        Assert.Single(found);
        Assert.Equal(StringEncodingKind.Utf16, found[0].Encoding);
    }

    [Fact]
    public void ReportsLocationAndSection()
    {
        var pe = ImageWith(Ascii("hello world"), Zeros(1));
        var found = InPayload(StringScanner.Scan(pe, new StringScanOptions { MinLength = 5, ScanUtf16 = false }));

        var s = Assert.Single(found);
        Assert.Equal(".rdata", s.Section);
        Assert.NotNull(s.Rva);
        Assert.NotNull(s.Va);
        Assert.Equal(pe.RvaToVa(s.Rva!.Value), s.Va!.Value);
        Assert.Equal((uint)s.Offset, pe.RvaToOffset(s.Rva.Value)!.Value);
        Assert.True(s.NullTerminated);
    }

    [Fact]
    public void UnterminatedRunIsFlagged()
    {
        // Runs into a non-printable byte that is not NUL.
        var pe = ImageWith(Ascii("not terminated"), new byte[] { 0xFF }, Zeros(1));
        var found = InPayload(StringScanner.Scan(pe, new StringScanOptions { MinLength = 5, ScanUtf16 = false }));

        Assert.False(Assert.Single(found).NullTerminated);
    }

    [Fact]
    public void LongRunIsTruncatedNotDropped()
    {
        var pe = ImageWith(Ascii(new string('A', 300)), Zeros(1));
        var found = InPayload(StringScanner.Scan(pe, new StringScanOptions { MinLength = 5, MaxLength = 64, ScanUtf16 = false }));

        Assert.Equal(64, Assert.Single(found).Text.Length);
    }

    [Fact]
    public void ResultLimitIsRespected()
    {
        var chunks = Enumerable.Range(0, 30).SelectMany(i => new[] { Ascii($"string{i:D4}"), Zeros(1) }).ToArray();
        var pe = ImageWith(chunks);
        var found = StringScanner.Scan(pe, new StringScanOptions { MinLength = 5, MaxResults = 10, ScanUtf16 = false });

        Assert.Equal(10, found.Count);
    }

    [Fact]
    public void FindsUtf16AtAnUnalignedOffset()
    {
        // One byte of padding pushes the wide string to an odd offset; packers do this.
        var pe = ImageWith(new byte[] { 0xCC }, Utf16("Unaligned wide"), Zeros(2));
        var found = InPayload(StringScanner.Scan(pe, new StringScanOptions { MinLength = 5 }));

        var wide = Assert.Single(found, s => s.Encoding == StringEncodingKind.Utf16);
        Assert.Equal("Unaligned wide", wide.Text);
        Assert.True(wide.Offset % 2 == 1, "expected the hit at an odd offset");
    }

    [Fact]
    public void ResultsAreSortedByOffset()
    {
        var pe = ImageWith(Ascii("first string"), Zeros(1), Utf16("second string"), Zeros(2), Ascii("third string"), Zeros(1));
        var found = InPayload(StringScanner.Scan(pe, new StringScanOptions { MinLength = 5 }));

        Assert.Equal(3, found.Count);
        Assert.Equal(found.OrderBy(s => s.Offset).Select(s => s.Text), found.Select(s => s.Text));
    }

    [SkippableFact]
    public void RealBinaryYieldsKnownStrings()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");
        Skip.IfNot(File.Exists(path), "kernel32.dll not found");

        var pe = PeImage.Load(path);
        var found = StringScanner.Scan(pe);

        Assert.NotEmpty(found);
        // The import name table lives in .rdata and is plain ASCII.
        Assert.Contains(found, s => s.Text.Contains("api-ms-win", StringComparison.OrdinalIgnoreCase));
        Assert.All(found, s =>
        {
            Assert.True(s.Text.Length >= 5);
            Assert.InRange(s.Offset, 0, pe.Length - 1);
        });
    }
}
