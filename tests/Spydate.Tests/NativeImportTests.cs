using Spydate.Decompiler.Managed;

namespace Spydate.Tests;

/// <summary>
/// The libraries in P/Invoke declarations, found in decompiled C# so a click can follow them.
///
/// The spans are the whole of it: a reference that is one column out highlights the quote instead
/// of the name, and one that is a line out is offered over the wrong code entirely.
/// </summary>
public sealed class NativeImportTests
{
    [Fact]
    public void TheLibraryInADllImportIsFound()
    {
        const string text = """
            [DllImport("Mingus.dll", SetLastError = true)]
            private static extern int HardDongleCheck();
            """;

        var reference = Assert.Single(NativeImports.In(text));

        Assert.Equal("Mingus.dll", reference.Assembly);
        Assert.Equal(NativeImports.ModuleToken, reference.Token);
        Assert.Equal(1, reference.Line);
    }

    [Fact]
    public void TheSpanCoversTheNameAndNotTheQuotes()
    {
        const string text = """[DllImport("Mingus.dll")]""";

        var reference = Assert.Single(NativeImports.In(text));

        // Columns are 1-based, as the decompiler records them. Reconstructing the substring is the
        // only check that actually proves the span: an off-by-one reads as a plausible name too.
        string[] lines = text.Split('\n');
        string covered = lines[reference.Line - 1].Substring(reference.Column - 1, reference.Length);

        Assert.Equal("Mingus.dll", covered);
    }

    [Fact]
    public void EveryImportInATypeIsFound()
    {
        const string text = """
            internal static class Native
            {
                [DllImport("Mingus.dll")]
                internal static extern int A();

                [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
                internal static extern int B();
            }
            """;

        var found = NativeImports.In(text);

        Assert.Equal(2, found.Count);
        Assert.Equal(["Mingus.dll", "kernel32.dll"], found.Select(r => r.Assembly));

        // Third and sixth lines of the block above; a whole type's worth of declarations is the
        // normal case and each has to point at its own line.
        Assert.Equal([3, 6], found.Select(r => r.Line));
    }

    [Fact]
    public void CodeWithNoImportsOffersNothing()
    {
        const string text = """
            public int Add(int a, int b)
            {
                return a + b;
            }
            """;

        Assert.Empty(NativeImports.In(text));
    }

    [Fact]
    public void AnImportWithNoLibraryNameIsSkippedRatherThanOffered()
    {
        // An empty name names nothing to open, and a reference of length zero would be a click
        // target that can never be hit.
        Assert.Empty(NativeImports.In("""[DllImport("")]"""));
        Assert.Empty(NativeImports.In("[DllImport]"));
    }

    [Fact]
    public void TheAddressCommentsTheViewAppendsDoNotMoveTheSpan()
    {
        // The C# view writes each line's file address in a trailing comment. Those go after the
        // code, never before it, so a span measured from the start of the line still holds — which
        // is why the references are taken from the addressed text rather than the raw text.
        const string text = """[DllImport("Mingus.dll")]    // 0x00401000""";

        var reference = Assert.Single(NativeImports.In(text));
        string covered = text.Substring(reference.Column - 1, reference.Length);

        Assert.Equal("Mingus.dll", covered);
    }
}
