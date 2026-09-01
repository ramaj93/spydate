using System.Text.Json;
using System.Text.Json.Serialization;
using Spydate.Agent.Providers;

namespace Spydate.Agent;

/// <summary>
/// Which provider and model the assistant should use, remembered between runs.
///
/// The key is not here. It lives in <see cref="Secrets.ISecretStore"/>, encrypted, and this file is
/// plain JSON — keeping them apart is what stops a key reaching a backup, a screenshot or a bug
/// report along with the settings someone was asked to check.
/// </summary>
public sealed class AgentSettings
{
    [JsonPropertyName("provider")]
    public ProviderKind Provider { get; set; } = ProviderKind.Anthropic;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>Non-empty only when pointing at a proxy or a compatible server.</summary>
    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; set; }

    [JsonPropertyName("maxToolCalls")]
    public int MaxToolCalls { get; set; } = 24;

    public ProviderSettings ToProviderSettings() => new()
    {
        Kind = Provider,
        Model = Model,
        Endpoint = Endpoint,
        MaxToolCalls = MaxToolCalls,
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Spydate",
        "assistant.json");

    /// <summary>
    /// Reads the settings, or returns fresh ones. Never throws: a damaged file means the assistant
    /// asks to be set up again, which is a far better outcome than the window failing to start.
    /// </summary>
    public static AgentSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(path), Options) ?? new AgentSettings()
                : new AgentSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AgentSettings();
        }
    }

    public void Save(string? path = null)
    {
        path = path ?? DefaultPath;
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        // Written beside and moved into place, the way every other file this program owns is.
        string temporary = $"{path}.{Environment.ProcessId:X}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
        File.Move(temporary, path, overwrite: true);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
