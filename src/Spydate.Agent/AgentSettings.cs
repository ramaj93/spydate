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

    /// <summary>Take the answer as it is generated. Off is the fallback for a provider that only
    /// parses its own tool calls correctly when it answers in one piece.</summary>
    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = true;

    /// <summary>
    /// The context budget as it was chosen, or null if nobody has ever chosen one.
    ///
    /// Nullable so that absent and chosen are different things. A plain int cannot tell them apart:
    /// a file with no such key would take whatever constant the class was written with, which is a
    /// number picked for one provider being applied to whichever one is actually configured.
    /// </summary>
    [JsonPropertyName("maxContextTokens")]
    public int? ContextTokens { get; set; }

    /// <summary>
    /// How much conversation to carry, in tokens. Context windows run from tens of thousands to a
    /// million depending on the model, so unless it has been set by hand this follows the model.
    /// </summary>
    [JsonIgnore]
    public int MaxContextTokens
    {
        get => ContextTokens ?? ProviderSettings.SuggestedContextTokens(Provider, Model);
        set => ContextTokens = value;
    }

    public ProviderSettings ToProviderSettings() => new()
    {
        Kind = Provider,
        Model = Model,
        Endpoint = Endpoint,
        MaxToolCalls = MaxToolCalls,
        Stream = Stream,
        MaxContextTokens = MaxContextTokens,
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
            if (!File.Exists(path))
            {
                return new AgentSettings();
            }

            var settings = JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(path), Options) ?? new AgentSettings();

            // Every file written before the budget followed the model says 48000, whether or not
            // anybody ever looked at the setting. Taken at face value it would leave the assistant
            // with a third of Claude's memory and a twentieth of Gemini's on exactly the machines
            // that had already hit the problem, so it is read as never chosen instead.
            if (settings.ContextTokens == ProviderSettings.LegacyContextTokens)
            {
                settings.ContextTokens = null;
            }

            return settings;
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
