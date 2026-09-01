using System.ClientModel;
using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Spydate.Agent.Providers;

/// <summary>Who is being asked. Bring your own key: none of these is a default and none is bundled.</summary>
public enum ProviderKind
{
    OpenAi,
    OpenRouter,
    DeepSeek,
    Anthropic,
}

/// <summary>
/// Which provider, which model, and where to reach it.
///
/// Three of the four speak the OpenAI chat API and differ only by base URL, so they share one client
/// and the difference is a string. Anthropic has its own SDK, which ships an <see cref="IChatClient"/>
/// of its own — so there is no hand-written HTTP anywhere in here, which is the point: a request
/// signed or framed slightly wrong fails in ways that look like the model being stupid.
/// </summary>
public sealed record ProviderSettings
{
    public ProviderKind Kind { get; init; } = ProviderKind.Anthropic;

    /// <summary>Model id, exactly as the provider names it.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Overrides the provider's usual endpoint. For a proxy, or a compatible server.</summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// How many times the model may call tools before the turn is cut off. A loop that reads one
    /// function, then its callers, then theirs, is doing exactly what it should; one that reads the
    /// same thing forty times is not, and the difference has to be bounded somewhere.
    /// </summary>
    public int MaxToolCalls { get; init; } = 24;

    /// <summary>The name this provider's key is stored under.</summary>
    public string KeyName => Kind.ToString();

    /// <summary>Whether it has been told enough to be usable.</summary>
    public bool IsComplete => !string.IsNullOrWhiteSpace(Model);

    /// <summary>What to call the endpoint when nothing overrides it.</summary>
    public Uri BaseUri => new(Endpoint is { Length: > 0 } custom ? custom : DefaultEndpoint(Kind));

    public static string DefaultEndpoint(ProviderKind kind) => kind switch
    {
        ProviderKind.OpenRouter => "https://openrouter.ai/api/v1",
        ProviderKind.DeepSeek => "https://api.deepseek.com/v1",
        ProviderKind.Anthropic => "https://api.anthropic.com",
        _ => "https://api.openai.com/v1",
    };

    /// <summary>A model worth defaulting to, so a first-time setup has something in the box.</summary>
    public static string SuggestedModel(ProviderKind kind) => kind switch
    {
        ProviderKind.Anthropic => "claude-sonnet-5",
        ProviderKind.OpenRouter => "anthropic/claude-sonnet-5",
        ProviderKind.DeepSeek => "deepseek-chat",
        _ => "gpt-5",
    };
}

/// <summary>Builds the client for a provider. The only place that knows any provider apart.</summary>
public static class ChatProviders
{
    public static IChatClient Create(ProviderSettings settings, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new ArgumentException("no model was chosen", nameof(settings));
        }

        return settings.Kind == ProviderKind.Anthropic
            ? new AnthropicClient(new ClientOptions { ApiKey = apiKey, BaseUrl = settings.BaseUri.ToString() }).AsIChatClient(settings.Model)
            : new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = settings.BaseUri })
                .GetChatClient(settings.Model)
                .AsIChatClient();
    }
}
