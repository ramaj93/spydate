using Spydate.Core.PE;
using Spydate.Core.Project;

namespace Spydate.Tests;

/// <summary>
/// Two writers, one project file. The window and an agent driving the MCP server each hold their own
/// store, loaded at their own moment, and both save the whole thing — so saving has to merge or one
/// of them silently deletes the other's work. The direction that matters is an agent erasing names a
/// person typed: agent names are cheaply regenerated, a person's are not.
/// </summary>
public sealed class AnnotationMergeTests : IDisposable
{
    private const ulong Base = 0x140001000;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "spydate-merge-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly PeImage _image;
    private readonly string _path;

    public AnnotationMergeTests()
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

    /// <summary>A store that has already loaded whatever is on disk, as a second process would have.</summary>
    private AnnotationStore Opened(AnnotationSource source = AnnotationSource.User)
    {
        var store = new AnnotationStore { Source = source };
        if (File.Exists(_path))
        {
            SpydateProject.Load(_path, _image, store);
        }

        return store;
    }

    private AnnotationStore Reload()
    {
        var store = new AnnotationStore();
        SpydateProject.Load(_path, _image, store);
        return store;
    }

    [Fact]
    public void AWriterDoesNotDeleteWhatItNeverTouched()
    {
        // The whole point. The person names one thing; the agent, whose store predates that, names
        // another and saves. Overwriting would take the person's name with it.
        var person = Opened();
        person.SetName(Base, "MainLoop");
        SpydateProject.SaveTo(_path, _image, person);

        var agent = new AnnotationStore { Source = AnnotationSource.Agent };   // opened before the above
        agent.SetName(Base + 0x10, "ParseArgs");
        SpydateProject.SaveTo(_path, _image, agent);

        var saved = Reload();
        Assert.Equal("MainLoop", saved.NameFor(Base));
        Assert.Equal("ParseArgs", saved.NameFor(Base + 0x10));
    }

    [Fact]
    public void ClearingSomethingRemovesItFromTheFile()
    {
        var first = Opened();
        first.SetName(Base, "Doomed");
        first.SetName(Base + 0x10, "Kept");
        SpydateProject.SaveTo(_path, _image, first);

        var second = Opened();
        second.SetName(Base, null);
        SpydateProject.SaveTo(_path, _image, second);

        var saved = Reload();
        Assert.Null(saved.NameFor(Base));
        Assert.Equal("Kept", saved.NameFor(Base + 0x10));
    }

    [Fact]
    public void TheWriterWinsWhenBothTouchedTheSameAddress()
    {
        var first = Opened();
        first.SetName(Base, "FirstGuess");
        SpydateProject.SaveTo(_path, _image, first);

        var second = Opened();
        second.SetName(Base, "BetterGuess");
        SpydateProject.SaveTo(_path, _image, second);

        Assert.Equal("BetterGuess", Reload().NameFor(Base));
    }

    [Fact]
    public void SavingTwiceDoesNotResurrectWhatTheOtherWriterRemoved()
    {
        // After a save the store agrees with the file, so its next save must not push its earlier
        // changes over the top of somebody else's later ones.
        var agent = Opened(AnnotationSource.Agent);
        agent.SetName(Base, "AgentGuess");
        SpydateProject.SaveTo(_path, _image, agent);

        var person = Opened();
        person.SetName(Base, null);
        SpydateProject.SaveTo(_path, _image, person);

        agent.SetName(Base + 0x20, "Unrelated");
        SpydateProject.SaveTo(_path, _image, agent);

        var saved = Reload();
        Assert.Null(saved.NameFor(Base));
        Assert.Equal("Unrelated", saved.NameFor(Base + 0x20));
    }

    [Fact]
    public void AFileForADifferentBuildIsReplacedRatherThanMergedInto()
    {
        // Merging would mix two binaries' annotations at addresses that mean different things. The
        // stale file is written by hand: two synthetic images differ only in their section bytes,
        // and identity is name/size/timestamp/checksum, so they would otherwise look like one build.
        File.WriteAllText(_path, $$"""
            {
              "format": 1,
              "image": { "name": "other.exe", "size": 4096, "timeDateStamp": "0xDEADBEEF", "checkSum": "0x1234" },
              "annotations": [ { "rva": "0x1000", "name": "FromAnotherBuild" } ]
            }
            """);

        var mine = new AnnotationStore();
        mine.SetName(Base + 0x10, "FromThisBuild");
        SpydateProject.SaveTo(_path, _image, mine);

        var saved = Reload();
        Assert.Null(saved.NameFor(Base));
        Assert.Equal("FromThisBuild", saved.NameFor(Base + 0x10));
    }

    // ------------------------------------------------------------------
    // Provenance
    // ------------------------------------------------------------------

    [Fact]
    public void WhoSetSomethingSurvivesARoundTrip()
    {
        var agent = Opened(AnnotationSource.Agent);
        agent.SetName(Base, "GuessedByAgent");
        agent.SetComment(Base + 0x10, "also the agent");
        SpydateProject.SaveTo(_path, _image, agent);

        var saved = Reload();

        Assert.Equal(AnnotationSource.Agent, saved.Get(Base)!.Source);
        Assert.Equal(AnnotationSource.Agent, saved.Get(Base + 0x10)!.Source);
        Assert.NotNull(saved.Get(Base)!.Modified);
    }

    [Fact]
    public void EverythingOneSourceIsResponsibleForCanBeListed()
    {
        // What makes direct agent writes tolerable: forty names from one misread function are a set
        // that can be found and undone, rather than something to pick out of the file by hand.
        var store = new AnnotationStore { Source = AnnotationSource.Agent };
        store.SetName(Base, "AgentOne");
        store.SetName(Base + 0x10, "AgentTwo");
        store.Source = AnnotationSource.User;
        store.SetName(Base + 0x20, "Mine");

        var byAgent = store.SnapshotOf(AnnotationSource.Agent);

        Assert.Equal(2, byAgent.Count);
        Assert.All(byAgent, e => Assert.StartsWith("Agent", e.Value.Name, StringComparison.Ordinal));
        Assert.Single(store.SnapshotOf(AnnotationSource.User));
    }

    [Fact]
    public void AProjectWrittenBeforeProvenanceExistedReadsAsAPersonsWork()
    {
        // Every annotation in every file written until now was typed by hand, and that is what an
        // absent source has to mean — not "unknown", which would put it at risk of a bulk revert.
        File.WriteAllText(_path, $$"""
            {
              "format": 1,
              "image": {
                "name": "{{_image.FileName}}",
                "size": {{_image.Length}},
                "timeDateStamp": "0x{{_image.FileHeader.TimeDateStamp:X}}",
                "checkSum": "0x{{_image.OptionalHeader.CheckSum:X}}"
              },
              "annotations": [ { "rva": "0x1000", "name": "TypedByHand" } ]
            }
            """);

        var store = Reload();

        Assert.Equal("TypedByHand", store.NameFor(Base));
        Assert.Equal(AnnotationSource.User, store.Get(Base)!.Source);
    }

    [Fact]
    public void APersonRetypingAnAgentsNameTakesItOver()
    {
        var store = new AnnotationStore { Source = AnnotationSource.Agent };
        store.SetName(Base, "SameName");
        store.Source = AnnotationSource.User;

        store.SetName(Base, "SameName");

        Assert.Equal(AnnotationSource.User, store.Get(Base)!.Source);
    }

    // ------------------------------------------------------------------
    // Clearing announces itself
    // ------------------------------------------------------------------

    [Fact]
    public void ClearingTheStoreAnnouncesEveryRemoval()
    {
        // Names reach the symbol table through Changed, so a silent Clear would leave all of them
        // still showing in listings with nothing behind them.
        var store = new AnnotationStore();
        store.SetName(Base, "One");
        store.SetName(Base + 0x10, "Two");

        var announced = new List<ulong>();
        store.Changed += (_, e) => announced.Add(e.Va);
        store.Clear();

        Assert.Equal(new[] { Base, Base + 0x10 }, announced.OrderBy(v => v));
        Assert.Equal(0, store.Count);
    }
}
