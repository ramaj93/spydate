using Spydate.Core.PE;
using Spydate.Core.Project;

namespace Spydate.Tests;

/// <summary>
/// Noticing that somebody else rewrote the project file. This is what lets a name an agent gives
/// appear in the window while it is open, and it lives in Core rather than in the window precisely so
/// it can be checked — nothing in the app is reachable from a test.
/// </summary>
public sealed class ProjectWatcherTests : IDisposable
{
    private const ulong Va = 0x140001000;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "spydate-watch-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly PeImage _image;
    private readonly string _path;

    public ProjectWatcherTests()
    {
        Directory.CreateDirectory(_directory);
        var code = new byte[0x20];
        Array.Fill(code, (byte)0xCC);
        _image = SyntheticPe.WithSectionData(code);
        // Not _image.FileName: a synthetic image has no path, so it calls itself "<memory>", and
        // angle brackets are not legal in a file name.
        _path = Path.Combine(_directory, "sample" + SpydateProject.Extension);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Waits for the watcher to report, rather than sleeping and hoping.</summary>
    private static bool Fired(ProjectFileWatcher watcher, Action write, TimeSpan timeout)
    {
        using var signal = new ManualResetEventSlim();
        watcher.Changed += (_, _) => signal.Set();
        write();
        return signal.Wait(timeout);
    }

    [Fact]
    public void AProjectWrittenBySomethingElseIsNoticed()
    {
        using var watcher = new ProjectFileWatcher(_image, new[] { _directory });

        bool fired = Fired(
            watcher,
            () =>
            {
                var store = new AnnotationStore { Source = AnnotationSource.Agent };
                store.SetName(Va, "WrittenElsewhere");
                SpydateProject.SaveTo(_path, _image, store);
            },
            TimeSpan.FromSeconds(10));

        Assert.True(fired, "the watcher never reported the write");
    }

    [Fact]
    public void ASaveIsReportedOnceRatherThanThreeTimes()
    {
        // A save lands as several events — the temp file appears, then is renamed over the target —
        // and reloading the whole project once per event would be visible as a stutter.
        using var watcher = new ProjectFileWatcher(_image, new[] { _directory });
        int fired = 0;
        using var settled = new ManualResetEventSlim();
        watcher.Changed += (_, _) =>
        {
            Interlocked.Increment(ref fired);
            settled.Set();
        };

        var store = new AnnotationStore();
        store.SetName(Va, "One");
        SpydateProject.SaveTo(_path, _image, store);
        store.SetName(Va, "Two");
        SpydateProject.SaveTo(_path, _image, store);

        Assert.True(settled.Wait(TimeSpan.FromSeconds(10)), "the watcher never reported the writes");
        Thread.Sleep(600);   // past the settle window, so any extra reports would have arrived

        Assert.Equal(1, Volatile.Read(ref fired));
    }

    [Fact]
    public void ADirectoryThatCannotBeWatchedIsNotFatal()
    {
        // A project can live beside the binary or in the per-user store, and neither is guaranteed to
        // exist. Failing to watch one must not stop a file being opened.
        using var watcher = new ProjectFileWatcher(_image, new[] { Path.Combine(_directory, "does-not-exist") });

        Assert.False(Fired(watcher, () => { }, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void NothingIsReportedAfterItIsDisposed()
    {
        var watcher = new ProjectFileWatcher(_image, new[] { _directory });
        int fired = 0;
        watcher.Changed += (_, _) => Interlocked.Increment(ref fired);
        watcher.Dispose();

        var store = new AnnotationStore();
        store.SetName(Va, "AfterDisposal");
        SpydateProject.SaveTo(_path, _image, store);
        Thread.Sleep(900);

        Assert.Equal(0, Volatile.Read(ref fired));
    }
}
