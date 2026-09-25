using Spydate.Core.Binary;

namespace Spydate.Tests;

/// <summary>
/// A file that repeats one complaint for every method it holds must not become a list the window lays out line by
/// line: the digest keeps one of each kind and counts the rest.
/// </summary>
public class WarningDigestTests
{
    [Fact]
    public void AFewWarningsAreShownAsTheyAre()
    {
        string[] warnings = ["a: first", "b: second"];

        Assert.Same(warnings, WarningDigest.Summarize(warnings));
    }

    [Fact]
    public void RepeatsOfOneKindBecomeOneLineWithACount()
    {
        var warnings = Enumerable.Range(0, 17_807)
            .Select(i => $"classes.dex: C{i}.m: debug info: The line table moves to {i + 1}, past the {i} code units.")
            .Append("classes.dex: a string is not valid UTF-8.")
            .ToList();

        var digest = WarningDigest.Summarize(warnings);

        Assert.Equal(2, digest.Count);
        Assert.StartsWith("classes.dex: C0.m: debug info: The line table moves to 1", digest[0], StringComparison.Ordinal);
        Assert.EndsWith("(and 17,806 more like it)", digest[0], StringComparison.Ordinal);
        Assert.Equal("classes.dex: a string is not valid UTF-8.", digest[1]);
    }

    [Fact]
    public void ManyKindsAreCutToTheLimitAndTheRestCounted()
    {
        var warnings = Enumerable.Range(0, 500).Select(i => $"kind {(char)('a' + (i % 26))}{(char)('a' + (i / 26))}").ToList();

        var digest = WarningDigest.Summarize(warnings, limit: 10);

        Assert.Equal(10, digest.Count);
        Assert.Equal("… and 491 more warnings of 491 other kinds.", digest[^1]);
    }
}
