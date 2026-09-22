using Spydate.Core.PE;
using Spydate.Core.Project;

namespace Spydate.Tests;

/// <summary>
/// The analyst's notes: keyed sections held in the project file, saved by the same merge as the
/// annotations so the window and an agent do not delete each other's. The store's own rules — a key
/// folded to one canonical form, a section refused rather than truncated — and the round trip through
/// the file, including two writers sharing it.
/// </summary>
public sealed class NotesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "spydate-notes-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly PeImage _image;
    private readonly string _path;

    public NotesTests()
    {
        var code = new byte[0x40];
        Array.Fill(code, (byte)0xCC);
        _image = SyntheticPe.WithSectionData(code);
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "sample.spydate");
    }

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

    private NoteStore Opened(AnnotationSource source = AnnotationSource.User)
    {
        var store = new NoteStore { Source = source };
        if (File.Exists(_path))
        {
            SpydateProject.Load(_path, _image, new AnnotationStore(), notes: store);
        }

        return store;
    }

    private NoteStore Reload()
    {
        var store = new NoteStore();
        SpydateProject.Load(_path, _image, new AnnotationStore(), notes: store);
        return store;
    }

    [Theory]
    [InlineData("Dead Ends")]
    [InlineData("dead_ends")]
    [InlineData("DEAD-ENDS")]
    [InlineData("  dead   ends  ")]
    public void KeysFoldToOneCanonicalSection(string key)
    {
        var store = new NoteStore();
        store.Set(key, "a dead end");

        Assert.Equal("dead-ends", store.Snapshot().Single().Key);
        Assert.Equal("a dead end", store.Get("Dead-Ends")!.Text);
    }

    [Fact]
    public void AKeyThatCleansToNothingIsRefused()
    {
        var store = new NoteStore();
        Assert.Throws<ArgumentException>(() => store.Set("   ", "text"));
        Assert.Throws<ArgumentException>(() => store.Set("___", "text"));
    }

    [Fact]
    public void NewlinesAreFoldedToLf()
    {
        var store = new NoteStore();
        store.Set("k", "line one\r\nline two\rline three");

        Assert.Equal("line one\nline two\nline three", store.Get("k")!.Text);
    }

    [Fact]
    public void BlankTextClearsTheSection()
    {
        var store = new NoteStore();
        store.Set("k", "something");
        store.Set("k", "   ");

        Assert.Equal(0, store.Count);
        Assert.Null(store.Get("k"));
    }

    [Fact]
    public void TextOverTheLimitIsRefusedWithTheOverage()
    {
        var store = new NoteStore();
        string tooLong = new('x', NoteStore.MaxTextLength + 17);

        var ex = Assert.Throws<ArgumentException>(() => store.Set("k", tooLong));
        Assert.Contains("17", ex.Message);
        Assert.Equal(0, store.Count);   // nothing was written
    }

    [Fact]
    public void TextAtTheLimitIsAccepted()
    {
        var store = new NoteStore();
        string atLimit = new('x', NoteStore.MaxTextLength);

        store.Set("k", atLimit);
        Assert.Equal(atLimit, store.Get("k")!.Text);
    }

    [Fact]
    public void AWriteIsStampedWithSourceAndTime()
    {
        var store = new NoteStore { Source = AnnotationSource.Agent };
        var before = DateTimeOffset.UtcNow;

        var note = store.Set("k", "text")!;

        Assert.Equal(AnnotationSource.Agent, note.Source);
        Assert.NotNull(note.Modified);
        Assert.True(note.Modified >= before);
    }

    [Fact]
    public void RestoreKeepsTheRecordedProvenance()
    {
        var store = new NoteStore { Source = AnnotationSource.Agent };
        var when = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        store.Restore("k", new Note { Text = "text", Source = AnnotationSource.User, Modified = when });

        var note = store.Get("k")!;
        Assert.Equal(AnnotationSource.User, note.Source);   // not the store's Agent
        Assert.Equal(when, note.Modified);
    }

    [Fact]
    public void ChangedFiresOncePerRealChange()
    {
        var store = new NoteStore();
        int changes = 0;
        store.Changed += (_, _) => changes++;

        store.Set("k", "text");
        store.Set("k", "text");   // no change
        store.Set("k", "different");
        store.Set("k", "");       // cleared

        Assert.Equal(3, changes);
    }

    [Fact]
    public void ARoundTripThroughTheFileKeepsEverything()
    {
        var store = new NoteStore { Source = AnnotationSource.Agent };
        store.Set("overview", "a packer stub that unpacks then jumps to the OEP");
        store.Set("string-xor", "strings are xor'd with 0x5A before use");
        SpydateProject.SaveTo(_path, _image, new AnnotationStore(), notes: store);

        var back = Reload();
        Assert.Equal(2, back.Count);
        Assert.Equal("a packer stub that unpacks then jumps to the OEP", back.Get("overview")!.Text);
        Assert.Equal(AnnotationSource.Agent, back.Get("string-xor")!.Source);
    }

    [Fact]
    public void AFileWithoutNotesLoadsWithNone()
    {
        var annotations = new AnnotationStore();
        annotations.SetName(_image.RvaToVa(0x1000), "MainLoop");
        SpydateProject.SaveTo(_path, _image, annotations);

        var notes = Reload();
        Assert.Equal(0, notes.Count);

        // And the annotations still load from the same file, so the new member did not disturb them.
        var back = new AnnotationStore();
        SpydateProject.Load(_path, _image, back);
        Assert.Equal("MainLoop", back.NameFor(_image.RvaToVa(0x1000)));
    }

    [Fact]
    public void ANoteWithNoAnnotationsIsWrittenAndReadsBack()
    {
        // The risk is the merge dropping notes when there are no annotations to anchor the save.
        var store = new NoteStore();
        store.Set("overview", "the only thing recorded so far");

        SpydateProject.SaveTo(_path, _image, new AnnotationStore(), notes: store);

        Assert.True(File.Exists(_path));
        Assert.Equal("the only thing recorded so far", Reload().Get("overview")!.Text);
    }

    [Fact]
    public void AWriterDoesNotDeleteASectionItNeverTouched()
    {
        var person = Opened();
        person.Set("overview", "what a person concluded");
        SpydateProject.SaveTo(_path, _image, new AnnotationStore(), notes: person);

        var agent = new NoteStore { Source = AnnotationSource.Agent };   // opened before the above
        agent.Set("strings", "what the agent found");
        SpydateProject.SaveTo(_path, _image, new AnnotationStore(), notes: agent);

        var saved = Reload();
        Assert.Equal("what a person concluded", saved.Get("overview")!.Text);
        Assert.Equal("what the agent found", saved.Get("strings")!.Text);
    }

    [Fact]
    public void ClearingASectionRemovesItFromTheFile()
    {
        var first = Opened();
        first.Set("doomed", "goes away");
        first.Set("kept", "stays");
        SpydateProject.SaveTo(_path, _image, new AnnotationStore(), notes: first);

        var second = Opened();
        second.Set("doomed", "");
        SpydateProject.SaveTo(_path, _image, new AnnotationStore(), notes: second);

        var saved = Reload();
        Assert.Null(saved.Get("doomed"));
        Assert.Equal("stays", saved.Get("kept")!.Text);
    }

    [Fact]
    public void TheWriterWinsWhenBothEditedTheSameSection()
    {
        var first = Opened();
        first.Set("overview", "first guess");
        SpydateProject.SaveTo(_path, _image, new AnnotationStore(), notes: first);

        var second = Opened();
        second.Set("overview", "better guess");
        SpydateProject.SaveTo(_path, _image, new AnnotationStore(), notes: second);

        Assert.Equal("better guess", Reload().Get("overview")!.Text);
    }

    [Fact]
    public void NotesAndAnnotationsShareTheFileWithoutDisturbingEachOther()
    {
        var annotations = new AnnotationStore();
        annotations.SetName(_image.RvaToVa(0x1000), "MainLoop");
        var notes = new NoteStore();
        notes.Set("overview", "a small program");
        SpydateProject.SaveTo(_path, _image, annotations, notes: notes);

        var backAnnotations = new AnnotationStore();
        var backNotes = new NoteStore();
        var result = SpydateProject.Load(_path, _image, backAnnotations, notes: backNotes);

        Assert.Equal("MainLoop", backAnnotations.NameFor(_image.RvaToVa(0x1000)));
        Assert.Equal("a small program", backNotes.Get("overview")!.Text);
        Assert.Equal(1, result.NotesApplied);
    }
}
