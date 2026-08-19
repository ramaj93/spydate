using System.Text.Json;
using Spydate.Core.PE;
using Spydate.Core.Project;

namespace Spydate.Tests;

/// <summary>The annotation store and the <c>.spydate</c> file it is written to.</summary>
public class ProjectTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "spydate-tests", Guid.NewGuid().ToString("N"));

    private string TempFile(string name)
    {
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, name);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // a leftover temp directory is not worth failing a test over
        }
    }

    // ------------------------------------------------------------------
    // Store
    // ------------------------------------------------------------------

    [Fact]
    public void ANameIsStoredAndReadBack()
    {
        var store = new AnnotationStore();

        Assert.Equal("ParseCommandLine", store.SetName(0x140001260, "ParseCommandLine"));
        Assert.Equal("ParseCommandLine", store.NameFor(0x140001260));
        Assert.Equal(1, store.Count);
        Assert.True(store.IsDirty);
    }

    [Fact]
    public void ClearingTheNameRemovesTheAnnotation()
    {
        var store = new AnnotationStore();
        store.SetName(0x1000, "Thing");

        Assert.Null(store.SetName(0x1000, "   "));
        Assert.Null(store.NameFor(0x1000));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void ACommentSurvivesRenamingAndViceVersa()
    {
        var store = new AnnotationStore();
        store.SetComment(0x1000, "called from the message loop");
        store.SetName(0x1000, "OnCommand");

        Assert.Equal("OnCommand", store.NameFor(0x1000));
        Assert.Equal("called from the message loop", store.CommentFor(0x1000));

        store.SetName(0x1000, null);
        Assert.Equal("called from the message loop", store.CommentFor(0x1000));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void NamesAreCleanedButPunctuationIsKept()
    {
        Assert.Equal("Two_words", AnnotationStore.CleanName("  Two words  "));
        Assert.Equal("?Foo@Bar@@QEAAXXZ", AnnotationStore.CleanName("?Foo@Bar@@QEAAXXZ"));
        Assert.Equal("kernel32!CreateFileW", AnnotationStore.CleanName("kernel32!CreateFileW"));
        Assert.Null(AnnotationStore.CleanName(""));
        Assert.Equal(AnnotationStore.MaxNameLength, AnnotationStore.CleanName(new string('a', 400))!.Length);
    }

    [Fact]
    public void ChangesAreAnnounced()
    {
        var store = new AnnotationStore();
        var seen = new List<AnnotationChange>();
        store.Changed += (_, change) => seen.Add(change);

        store.SetName(0x1000, "First");
        store.SetName(0x1000, "Second");
        store.SetName(0x1000, "Second");   // no change, so no event

        Assert.Equal(2, seen.Count);
        Assert.Null(seen[0].Before);
        Assert.Equal("First", seen[0].After?.Name);
        Assert.Equal("Second", seen[1].After?.Name);
        Assert.True(seen[1].NameChanged);
    }

    // ------------------------------------------------------------------
    // File
    // ------------------------------------------------------------------

    [Fact]
    public void AProjectRoundTrips()
    {
        var image = SyntheticPe.WithSectionData(new byte[] { 1, 2, 3 });
        ulong va = image.RvaToVa(0x1004);
        string path = TempFile("round.spydate");

        var saved = new AnnotationStore();
        saved.SetName(va, "Interesting");
        saved.SetComment(va, "the loop body");
        SpydateProject.SaveTo(path, image, saved);

        var loaded = new AnnotationStore();
        var result = SpydateProject.Load(path, image, loaded);

        Assert.True(result.Loaded, result.Reason);
        Assert.Equal(1, result.Applied);
        Assert.Equal("Interesting", loaded.NameFor(va));
        Assert.Equal("the loop body", loaded.CommentFor(va));
        Assert.False(loaded.IsDirty);   // just loaded, nothing to write back
    }

    [Fact]
    public void AddressesAreStoredAsRvas()
    {
        // The file must describe the image, not where it happened to be based when it was written.
        var image = SyntheticPe.WithSectionData(new byte[] { 0 });
        string path = TempFile("rva.spydate");
        var store = new AnnotationStore();
        store.SetName(image.RvaToVa(0x1004), "Thing");

        SpydateProject.SaveTo(path, image, store);
        string json = File.ReadAllText(path);

        Assert.Contains("\"0x1004\"", json);
        Assert.DoesNotContain(image.ImageBase.ToString("X"), json.Replace("\"imageBase\": \"0x140000000\"", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void AProjectForADifferentBuildIsRejected()
    {
        // Annotations from another build land at the wrong addresses, which is worse than having none.
        var image = SyntheticPe.WithSectionData(new byte[] { 0 });
        string path = TempFile("other.spydate");
        File.WriteAllText(path, """
        {
          "format": 1,
          "image": { "name": "other.exe", "size": 999999, "timeDateStamp": "0x1234", "checkSum": "0xABCD" },
          "annotations": [ { "rva": "0x1000", "name": "Wrong" } ]
        }
        """);

        var store = new AnnotationStore();
        var result = SpydateProject.Load(path, image, store);

        Assert.False(result.Loaded);
        Assert.Contains("different build", result.Reason);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void AFileFromANewerFormatIsRefused()
    {
        var image = SyntheticPe.WithSectionData(new byte[] { 0 });
        string path = TempFile("future.spydate");
        File.WriteAllText(path, """{ "format": 99, "image": { "name": "x", "size": 0 }, "annotations": [] }""");

        var result = SpydateProject.Load(path, image, new AnnotationStore());

        Assert.False(result.Loaded);
        Assert.Contains("format 99", result.Reason);
    }

    [Fact]
    public void AnUnreadableFileIsReportedNotThrown()
    {
        var image = SyntheticPe.WithSectionData(new byte[] { 0 });
        string path = TempFile("broken.spydate");
        File.WriteAllText(path, "this is not json");

        var result = SpydateProject.Load(path, image, new AnnotationStore());

        Assert.False(result.Loaded);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void TheFileIsReadableJson()
    {
        var image = SyntheticPe.WithSectionData(new byte[] { 0 });
        string path = TempFile("readable.spydate");
        var store = new AnnotationStore();
        store.SetName(image.RvaToVa(0x1000), "Entry");

        SpydateProject.SaveTo(path, image, store);
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        Assert.Equal(SpydateProject.FormatVersion, document.RootElement.GetProperty("format").GetInt32());
        Assert.Equal("Entry", document.RootElement.GetProperty("annotations")[0].GetProperty("name").GetString());
        Assert.Contains('\n', File.ReadAllText(path));   // indented, so it diffs sensibly
    }

    [Fact]
    public void SavingIsAtomicEnoughToNotLoseTheOldFile()
    {
        var image = SyntheticPe.WithSectionData(new byte[] { 0 });
        string path = TempFile("atomic.spydate");
        var store = new AnnotationStore();
        store.SetName(image.RvaToVa(0x1000), "First");
        SpydateProject.SaveTo(path, image, store);

        store.SetName(image.RvaToVa(0x1000), "Second");
        SpydateProject.SaveTo(path, image, store);

        Assert.False(File.Exists(path + ".tmp"));
        var reloaded = new AnnotationStore();
        SpydateProject.Load(path, image, reloaded);
        Assert.Equal("Second", reloaded.NameFor(image.RvaToVa(0x1000)));
    }

    [Fact]
    public void APathIsOfferedBesideTheBinaryAndPerUser()
    {
        var image = SyntheticPe.WithSectionData(new byte[] { 0 });

        var candidates = SpydateProject.CandidatePaths(image);

        // The synthetic image has no path on disk, so only the per-user store applies.
        Assert.Single(candidates);
        Assert.Contains("Spydate", candidates[0], StringComparison.Ordinal);
        Assert.EndsWith(SpydateProject.Extension, candidates[0], StringComparison.Ordinal);
    }
}
