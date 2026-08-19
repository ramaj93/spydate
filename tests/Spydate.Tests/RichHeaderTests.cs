using Spydate.Core.PE;
using Xunit.Abstractions;

namespace Spydate.Tests;

/// <summary>
/// The Rich header. Product ids are undocumented, so Spydate reports ids and builds rather than
/// guessing tool names; what it can prove is the checksum, which the linker computes over the DOS
/// stub and the entries.
/// </summary>
public class RichHeaderTests
{
    private readonly ITestOutputHelper _output;

    public RichHeaderTests(ITestOutputHelper output) => _output = output;

    private static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);

    [SkippableTheory]
    [InlineData("notepad.exe")]
    [InlineData("kernel32.dll")]
    [InlineData("user32.dll")]
    public void ChecksumOfAnUntouchedBinaryValidates(string fileName)
    {
        string path = Path.Combine(System32, fileName);
        Skip.IfNot(File.Exists(path), $"{fileName} not found");

        var pe = PeImage.Load(path);
        Skip.If(pe.RichHeader is null, "no Rich header");

        var rich = pe.RichHeader!;
        _output.WriteLine($"{fileName}: stored 0x{rich.Checksum:X8}, computed 0x{rich.ComputedChecksum:X8}, {rich.Entries.Count} entries");

        Assert.True(rich.IsChecksumValid, $"stored 0x{rich.Checksum:X8} but computed 0x{rich.ComputedChecksum:X8}");
    }

    [SkippableFact]
    public void EditingAnEntryBreaksTheChecksum()
    {
        // This is the point of checking: a forged or patched Rich header rarely recomputes it.
        string path = Path.Combine(System32, "notepad.exe");
        Skip.IfNot(File.Exists(path), "notepad.exe not found");

        var bytes = File.ReadAllBytes(path);
        var original = PeImage.Parse(bytes);
        Skip.If(original.RichHeader is null, "no Rich header");
        Assert.True(original.RichHeader!.IsChecksumValid);

        // Entries start after the DanS marker and three padding words, and are XOR-encrypted.
        int firstEntry = (int)original.RichHeader.Offset + 16;
        bytes[firstEntry] ^= 0xFF;

        var tampered = PeImage.Parse(bytes);

        Assert.NotNull(tampered.RichHeader);
        Assert.False(tampered.RichHeader!.IsChecksumValid);
    }

    [SkippableFact]
    public void EntriesReportIdsAndBuildsWithoutInventingToolNames()
    {
        string path = Path.Combine(System32, "notepad.exe");
        Skip.IfNot(File.Exists(path), "notepad.exe not found");

        var pe = PeImage.Load(path);
        Skip.If(pe.RichHeader is null, "no Rich header");

        Assert.All(pe.RichHeader!.Entries, e =>
        {
            Assert.True(e.UseCount > 0);
            Assert.Contains($"0x{e.ProductId:X4}", e.Description);
            // Build 0 means an imported object with no build stamp.
            Assert.Equal(e.BuildNumber == 0, e.Description.Contains("no build stamp", StringComparison.Ordinal));
        });
    }
}
