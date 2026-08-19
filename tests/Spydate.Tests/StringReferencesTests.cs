using Spydate.Core.PE;
using Spydate.Core.Strings;
using Spydate.Disassembly;

namespace Spydate.Tests;

public class StringReferencesTests
{
    private const ulong Base = 0x140000000;

    private static FoundString String(ulong va, string text, StringEncodingKind encoding = StringEncodingKind.Ascii) => new()
    {
        Offset = (long)(va - Base),
        Rva = (uint)(va - Base),
        Va = va,
        Text = text,
        Encoding = encoding,
        Section = ".rdata",
        NullTerminated = true,
    };

    private static XrefTable TableOf(params Xref[] xrefs)
    {
        var table = new XrefTable();
        foreach (var x in xrefs)
        {
            table.Add(x);
        }

        return table;
    }

    [Fact]
    public void ReferenceToTheStartIsAttributed()
    {
        var strings = new[] { String(0x140002000, "CreateFileW") };
        var table = TableOf(new Xref(0x140001000, 0x140002000, XrefKind.Offset));

        var resolved = Assert.Single(StringReferences.Resolve(strings, table));
        var xref = Assert.Single(resolved.References);
        Assert.Equal(0x140001000UL, xref.FromVa);
        Assert.Equal(1, resolved.Count);
    }

    [Fact]
    public void ReferenceIntoTheMiddleIsAttributed()
    {
        // lea rcx, [str+4] — compilers point past a prefix all the time.
        var strings = new[] { String(0x140002000, "C:\\Windows\\System32") };
        var table = TableOf(new Xref(0x140001000, 0x140002004, XrefKind.Offset));

        var resolved = Assert.Single(StringReferences.Resolve(strings, table));
        Assert.Single(resolved.References);
    }

    [Fact]
    public void ReferenceJustPastTheEndIsNotAttributed()
    {
        var strings = new[] { String(0x140002000, "abcde") }; // occupies 0x2000..0x2005
        var table = TableOf(new Xref(0x140001000, 0x140002005, XrefKind.Offset));

        Assert.Empty(Assert.Single(StringReferences.Resolve(strings, table)).References);
    }

    [Fact]
    public void Utf16StringsCoverTwoBytesPerCharacter()
    {
        var strings = new[] { String(0x140002000, "Software", StringEncodingKind.Utf16) }; // 16 bytes
        var table = TableOf(
            new Xref(0x140001000, 0x14000200E, XrefKind.Offset),  // inside
            new Xref(0x140001010, 0x140002010, XrefKind.Offset)); // one past the end

        var resolved = Assert.Single(StringReferences.Resolve(strings, table));
        Assert.Single(resolved.References);
        Assert.Equal(0x140001000UL, resolved.References[0].FromVa);
    }

    [Fact]
    public void EachReferenceLandsInExactlyOneString()
    {
        var strings = new[]
        {
            String(0x140002000, "first"),   // 0x2000..0x2005
            String(0x140002008, "second"),  // 0x2008..0x200E
            String(0x140002020, "third"),
        };
        var table = TableOf(
            new Xref(0x140001000, 0x140002000, XrefKind.Offset),
            new Xref(0x140001010, 0x140002009, XrefKind.Read),
            new Xref(0x140001020, 0x140002009, XrefKind.Offset),
            new Xref(0x140001030, 0x140002100, XrefKind.Offset)); // nothing there

        var resolved = StringReferences.Resolve(strings, table);

        Assert.Equal(1, resolved[0].Count);
        Assert.Equal(2, resolved[1].Count);
        Assert.Equal(0, resolved[2].Count);
    }

    [Fact]
    public void ReferencesAreSortedByReferringAddress()
    {
        var strings = new[] { String(0x140002000, "sorted") };
        var table = TableOf(
            new Xref(0x140003000, 0x140002000, XrefKind.Offset),
            new Xref(0x140001000, 0x140002000, XrefKind.Read),
            new Xref(0x140002FF0, 0x140002000, XrefKind.Offset));

        var refs = Assert.Single(StringReferences.Resolve(strings, table)).References;
        Assert.Equal(refs.OrderBy(r => r.FromVa).Select(r => r.FromVa), refs.Select(r => r.FromVa));
    }

    [Fact]
    public void UnmappedStringsNeverGetReferences()
    {
        var overlay = new FoundString
        {
            Offset = 0x9000,
            Rva = null,
            Va = null,
            Text = "in the overlay",
            Encoding = StringEncodingKind.Ascii,
            Section = "(overlay)",
            NullTerminated = false,
        };
        var table = TableOf(new Xref(0x140001000, 0x140002000, XrefKind.Offset));

        Assert.Empty(Assert.Single(StringReferences.Resolve(new[] { overlay }, table)).References);
    }

    [Fact]
    public void ResultOrderMatchesTheInput()
    {
        var strings = new[] { String(0x140002020, "later"), String(0x140002000, "earlier") };
        var resolved = StringReferences.Resolve(strings, new XrefTable());

        Assert.Equal("later", resolved[0].String.Text);
        Assert.Equal("earlier", resolved[1].String.Text);
    }

    [SkippableFact]
    public void RealBinaryLinksStringsToCode()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        Skip.IfNot(File.Exists(path), "notepad.exe not found");

        var pe = PeImage.Load(path);
        Skip.IfNot(pe.IsX86Family, $"{pe.Machine} is not x86/x64");

        var analysis = new BinaryAnalysis(pe);
        analysis.DiscoverAll(maxFunctions: 2000);
        var strings = StringScanner.Scan(pe);

        var resolved = StringReferences.Resolve(strings, analysis.Xrefs);

        Assert.Equal(strings.Count, resolved.Count);
        var referenced = resolved.Where(r => r.Count > 0).ToList();
        Assert.NotEmpty(referenced);

        // Every attributed reference must actually fall inside the string it was attached to,
        // and must come from an executable address.
        Assert.All(referenced, r => Assert.All(r.References, x =>
        {
            Assert.InRange(x.ToVa, r.StartVa, r.EndVa - 1);
            Assert.True(analysis.Source.IsExecutable(x.FromVa));
        }));
    }
}
