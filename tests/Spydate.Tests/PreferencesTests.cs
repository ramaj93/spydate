using Spydate.Core.Project;

namespace Spydate.Tests;

/// <summary>
/// The per-user settings store. Small, but the window that edits it cannot be tested at all, so
/// whatever can be checked below the UI is worth checking there.
/// </summary>
public sealed class PreferencesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"spydate-prefs-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Fact]
    public void NothingWrittenYetReadsAsTheDefaults()
    {
        var preferences = PreferenceStore.Load(_path);

        // Ask, not one of the two answers. A first run must not silently pick a side on a question
        // the window has not put yet.
        Assert.Equal(OpenDestination.Ask, preferences.OpenDestination);
        Assert.Equal(ForeignModuleView.CurrentTab, preferences.ForeignModule);
    }

    [Fact]
    public void WhatIsSavedIsWhatComesBack()
    {
        Assert.True(PreferenceStore.Save(
            new Preferences { OpenDestination = OpenDestination.NewTab, ForeignModule = ForeignModuleView.OwnTab },
            _path));

        var read = PreferenceStore.Load(_path);

        Assert.Equal(OpenDestination.NewTab, read.OpenDestination);
        Assert.Equal(ForeignModuleView.OwnTab, read.ForeignModule);
    }

    [Fact]
    public void ItIsWrittenAsNamesRatherThanNumbers()
    {
        PreferenceStore.Save(new Preferences { OpenDestination = OpenDestination.ReplaceCurrent }, _path);

        // A file somebody may well edit by hand — there is no window for this until phase 4 — so
        // "ReplaceCurrent" rather than "2", which says nothing and breaks if the enum is reordered.
        string json = File.ReadAllText(_path);

        Assert.Contains("ReplaceCurrent", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"openDestination\": 2", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ADamagedFileReadsAsTheDefaultsRatherThanThrowing()
    {
        File.WriteAllText(_path, "{ this is not json");

        // Losing a preference silently is a smaller harm than refusing to start, and the window
        // reads this before it has anywhere to report a failure to.
        var preferences = PreferenceStore.Load(_path);

        Assert.Equal(OpenDestination.Ask, preferences.OpenDestination);
    }

    [Fact]
    public void AFileMissingAFieldKeepsThatFieldsDefault()
    {
        File.WriteAllText(_path, """{ "openDestination": "NewTab" }""");

        // Settings are added over time, and a file written by an older build must not reset the
        // ones it had never heard of.
        var preferences = PreferenceStore.Load(_path);

        Assert.Equal(OpenDestination.NewTab, preferences.OpenDestination);
        Assert.Equal(ForeignModuleView.CurrentTab, preferences.ForeignModule);
    }

    [Fact]
    public void AnUnwritablePathIsReportedRatherThanThrown()
    {
        // A folder where a file should be: the write cannot succeed and must not take the window
        // down with it.
        string folder = Path.Combine(Path.GetTempPath(), $"spydate-prefs-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            Assert.False(PreferenceStore.Save(new Preferences(), folder));
        }
        finally
        {
            Directory.Delete(folder);
        }
    }
}
