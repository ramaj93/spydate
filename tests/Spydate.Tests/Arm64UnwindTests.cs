using Spydate.Core.PE;

namespace Spydate.Tests;

/// <summary>
/// The ARM64 exception directory. Entries are 8 bytes and state a length rather than an end
/// address, either packed into the entry or in an .xdata record.
/// </summary>
public class Arm64UnwindTests
{
    private const uint Code = 0x1000;

    [Fact]
    public void PackedEntryLengthComesFromTheEntryItself()
    {
        // Flag 1 = packed; bits 2-12 hold the length in 4-byte instruction words.
        const uint words = 9;
        uint packed = 1 | (words << 2);

        var pe = SyntheticPe.WithArm64ExceptionTable(new[] { (Code, packed) });

        var entry = Assert.Single(pe.ExceptionTable);
        Assert.Equal(Code, entry.BeginRva);
        Assert.Equal(Code + (words * 4), entry.EndRva);
        Assert.Equal(words * 4, entry.Length);
        Assert.True(entry.IsPacked);
        Assert.False(entry.IsChained);
        Assert.Equal(0u, entry.UnwindInfoRva);
    }

    [Fact]
    public void PackedFragmentIsMarkedChained()
    {
        // Flag 2 = a packed fragment: the continuation of another function, not a function start.
        uint fragment = 2 | (4u << 2);

        var pe = SyntheticPe.WithArm64ExceptionTable(new[] { (Code, fragment) });

        Assert.True(Assert.Single(pe.ExceptionTable).IsChained);
    }

    [Fact]
    public void UnpackedEntryTakesItsLengthFromTheXdataHeader()
    {
        // Low bits clear: the word is an RVA to an .xdata header whose low 18 bits are the length
        // in words.
        const uint xdataRva = 0x1100;
        const uint words = 37;

        var pe = SyntheticPe.WithArm64ExceptionTable(
            new[] { (Code, xdataRva) },
            new[] { (xdataRva, words | (1u << 20)) }); // an X bit set alongside the length

        var entry = Assert.Single(pe.ExceptionTable);
        Assert.Equal(Code + (words * 4), entry.EndRva);
        Assert.False(entry.IsPacked);
        Assert.Equal(xdataRva, entry.UnwindInfoRva);
    }

    [Fact]
    public void PaddingEntriesAreSkipped()
    {
        var pe = SyntheticPe.WithArm64ExceptionTable(new[] { (Code, 1u | (4u << 2)), (0u, 0u), (0u, 0u) });

        Assert.Single(pe.ExceptionTable);
    }

    [Fact]
    public void UnmappedXdataIsAWarningNotACrash()
    {
        var pe = SyntheticPe.WithArm64ExceptionTable(new[] { (Code, 0x7FFF_0000u) });

        var entry = Assert.Single(pe.ExceptionTable);
        Assert.Equal(entry.BeginRva, entry.EndRva); // unknown length rather than a guess
        Assert.Contains(pe.Warnings, w => w.Contains("Unwind data", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EntriesFeedDiscoveryBoundsLikeTheX64Table()
    {
        var pe = SyntheticPe.WithArm64ExceptionTable(new[] { (Code, 1u | (8u << 2)) });

        // The bounds are what analysis consumes.
        Assert.Equal(MachineType.Arm64, pe.Machine);
        Assert.False(pe.IsX86Family);
        Assert.Equal(64, pe.Bitness);
        Assert.Equal(Code + 32, Assert.Single(pe.ExceptionTable).EndRva);
    }

    [Fact]
    public void AShrinkWrappedFunctionsLaterRecordsAreFragmentsOfIt()
    {
        // MSVC gives each unwind state of a shrink-wrapped function a record of its own. The first ends its
        // unwind codes with end (0xE4); the later ones reach end_c (0xE5), "go on with the enclosing codes" —
        // here after a save_regp of their own, and with none at all.
        const uint first = 0x1100;
        const uint second = 0x1110;
        const uint third = 0x1120;
        const uint oneCodeWord = 1u << 27;
        var pe = SyntheticPe.WithArm64ExceptionTable(
            new[] { (Code, first), (Code + 0x24, second), (Code + 0x40, third) },
            new[]
            {
                (first, 9 | oneCodeWord), (first + 4, 0xE4FC83E1u),       // set_fp; save_fplr_x; pac_sign_lr; end
                (second, 7 | oneCodeWord), (second + 4, 0xE1E504C8u),     // save_regp; end_c; set_fp
                (third, 4 | oneCodeWord), (third + 4, 0xE3E3E4E5u),       // end_c; end
            });

        Assert.Equal([false, true, true], pe.ExceptionTable.Select(e => e.IsChained));
        Assert.Equal([(Code, Code + 0x50)], pe.UnwindRanges);
    }
}
