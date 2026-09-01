using Spydate.Core.Project;

namespace Spydate.Tests;

/// <summary>The list behind the File menu.</summary>
public sealed class RecentFilesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"spydate-recent-{Guid.NewGuid():N}");
    private readonly string _path;

    public RecentFilesTests() => _path = Path.Combine(_directory, "recent.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void TheNewestIsFirst()
    {
        RecentFiles.Add(@"C:\bin\one.exe", _path);
        RecentFiles.Add(@"C:\bin\two.exe", _path);

        var recent = RecentFiles.Load(_path);

        Assert.Equal(2, recent.Count);
        Assert.Equal(@"C:\bin\two.exe", recent[0].Path);
        Assert.Equal("one.exe", recent[1].Name);
        Assert.Equal(@"C:\bin", recent[1].Folder);
    }

    [Fact]
    public void OpeningSomethingAgainMovesItRatherThanRepeatingIt()
    {
        RecentFiles.Add(@"C:\bin\one.exe", _path);
        RecentFiles.Add(@"C:\bin\two.exe", _path);

        // Same file, typed the way Windows would also accept it.
        RecentFiles.Add(@"c:\BIN\ONE.exe", _path);

        var recent = RecentFiles.Load(_path);

        Assert.Equal(2, recent.Count);
        Assert.Equal(@"c:\BIN\ONE.exe", recent[0].Path);
    }

    [Fact]
    public void TheListStopsGrowing()
    {
        for (int i = 0; i < RecentFiles.Max + 6; i++)
        {
            RecentFiles.Add($@"C:\bin\file{i}.exe", _path);
        }

        var recent = RecentFiles.Load(_path);

        Assert.Equal(RecentFiles.Max, recent.Count);
        Assert.Equal($@"C:\bin\file{RecentFiles.Max + 5}.exe", recent[0].Path);
        Assert.DoesNotContain(recent, r => r.Path.EndsWith(@"file0.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void OneThatHasGoneCanBeTakenOut()
    {
        RecentFiles.Add(@"C:\bin\one.exe", _path);
        RecentFiles.Add(@"C:\bin\two.exe", _path);

        var left = RecentFiles.Remove(@"C:\bin\one.exe", _path);

        Assert.Equal(@"C:\bin\two.exe", Assert.Single(left).Path);
        Assert.Single(RecentFiles.Load(_path));
    }

    [Fact]
    public void ClearingLeavesNothingBehind()
    {
        RecentFiles.Add(@"C:\bin\one.exe", _path);
        RecentFiles.Clear(_path);

        Assert.Empty(RecentFiles.Load(_path));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void ADamagedListReadsAsEmptyRatherThanThrowing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_path, "{ this is not json");

        Assert.Empty(RecentFiles.Load(_path));

        // And it recovers: the next open writes a good file over the bad one.
        RecentFiles.Add(@"C:\bin\one.exe", _path);
        Assert.Single(RecentFiles.Load(_path));
    }

    [Fact]
    public void ARelativePathIsRecordedAsWhereItActuallyWas()
    {
        // The command line is one of the ways a binary gets opened, and "notepad.exe" means
        // something else as soon as the working directory changes.
        RecentFiles.Add("notepad.exe", _path);

        string recorded = RecentFiles.Load(_path)[0].Path;

        Assert.True(Path.IsPathFullyQualified(recorded), recorded);
    }

    [Fact]
    public void ReadingAListThatWasNeverWrittenIsNotAnError()
        => Assert.Empty(RecentFiles.Load(Path.Combine(_directory, "nothing-here.json")));
}
