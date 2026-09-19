using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spydate.Core.Project;

/// <summary>Where a newly opened file goes when one is already open.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OpenDestination>))]
public enum OpenDestination
{
    /// <summary>Ask each time. The default, and the only thing the window can set today.</summary>
    Ask,

    /// <summary>Always open a new tab, leaving what is open alone.</summary>
    NewTab,

    /// <summary>Always close the current tab and open in its place.</summary>
    ReplaceCurrent,
}

/// <summary>What the window does when execution steps into a module other than the open file.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ForeignModuleView>))]
public enum ForeignModuleView
{
    /// <summary>Show the module's code as documents inside the current tab. The default.</summary>
    CurrentTab,

    /// <summary>Open the module as a read-only file tab of its own.</summary>
    OwnTab,
}

/// <summary>
/// Settings that belong to the person rather than to a binary.
///
/// Kept beside the recent list and the remembered debug targets, and for the same reason: a project
/// file is meant to be shareable, and none of this says anything about the program being analysed.
/// </summary>
public sealed record Preferences
{
    [JsonPropertyName("openDestination")]
    public OpenDestination OpenDestination { get; init; } = OpenDestination.Ask;

    [JsonPropertyName("foreignModule")]
    public ForeignModuleView ForeignModule { get; init; } = ForeignModuleView.CurrentTab;
}

/// <summary>
/// Reads and writes <see cref="Preferences"/>, and never throws.
///
/// A damaged preferences file is worth losing silently — the defaults are what almost everyone is
/// running anyway — and it is certainly not worth failing to start the window over. The same
/// judgement the recent list makes, for the same reason.
/// </summary>
public static class PreferenceStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Spydate",
        "preferences.json");

    /// <summary>What is set, or the defaults when there is nothing readable to read.</summary>
    public static Preferences Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!System.IO.File.Exists(path))
            {
                return new Preferences();
            }

            return JsonSerializer.Deserialize<Preferences>(System.IO.File.ReadAllText(path), Options) ?? new Preferences();
        }
        catch (Exception ex) when (ex is System.IO.IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            return new Preferences();
        }
    }

    /// <summary>Writes them out. False when it could not be written, which is not worth interrupting anyone for.</summary>
    public static bool Save(Preferences preferences, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        try
        {
            path ??= DefaultPath;
            string? folder = System.IO.Path.GetDirectoryName(path);
            if (folder is { Length: > 0 })
            {
                System.IO.Directory.CreateDirectory(folder);
            }

            System.IO.File.WriteAllText(path, JsonSerializer.Serialize(preferences, Options));
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
