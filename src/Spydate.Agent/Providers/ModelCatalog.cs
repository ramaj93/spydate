using System.Net.Http.Headers;
using System.Text.Json;

namespace Spydate.Agent.Providers;

/// <summary>
/// What a provider said it can run, or why it would not say. Never an exception: failing to list
/// models is not a reason to stop someone typing one in, and the settings dialog stays usable.
/// </summary>
public sealed record ModelListResult(IReadOnlyList<string> Models, string? Problem)
{
    public bool Ok => Problem is null;

    public static ModelListResult Failed(string problem) => new(Array.Empty<string>(), problem);
}

/// <summary>
/// Asks a provider which models it has.
///
/// Every one of the four answers the same question at a slightly different address with different
/// headers, but they all return <c>data[].id</c>, so one method covers them. Typing a model id from
/// memory is a coin toss — providers rename them, and a wrong one fails at the first question with
/// an error that says nothing useful — so this exists to turn that into a list. It is a convenience,
/// not a gate: the box stays editable, because a brand-new model is usually usable before it is
/// listed, and a proxy may not implement the endpoint at all.
/// </summary>
public static class ModelCatalog
{
    /// <summary>Short: a settings dialog waiting on a hung endpoint is worse than one that gives up.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Anthropic pages this endpoint and defaults to twenty, which is fewer than it has.</summary>
    private const int PageSize = 1000;

    private const string AnthropicVersion = "2023-06-01";

    public static async Task<ModelListResult> ListAsync(
        ProviderSettings settings,
        string? apiKey,
        HttpClient? http = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        bool ownsClient = http is null;
        http ??= new HttpClient { Timeout = Timeout };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ModelsUri(settings));
            Authenticate(request, settings.Kind, apiKey);

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // The status is the useful half — 401 means the key, 404 means the endpoint is not
                // there — and a line of the body usually names the actual complaint.
                return ModelListResult.Failed($"{settings.Kind} answered {(int)response.StatusCode} {response.StatusCode}{Detail(body)}");
            }

            var models = Parse(body);
            return models.Count == 0
                ? ModelListResult.Failed($"{settings.Kind} listed no models")
                : new ModelListResult(models, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ModelListResult.Failed($"{settings.Kind} did not answer within {Timeout.TotalSeconds:F0} seconds");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException or InvalidOperationException)
        {
            return ModelListResult.Failed($"could not reach {settings.Kind}: {ex.Message}");
        }
        finally
        {
            if (ownsClient)
            {
                http.Dispose();
            }
        }
    }

    /// <summary>
    /// Where to ask. The OpenAI-compatible base URLs already end in <c>/v1</c> and Anthropic's does
    /// not, which is the whole of the difference.
    /// </summary>
    public static Uri ModelsUri(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string root = settings.BaseUri.ToString().TrimEnd('/');
        return settings.Kind == ProviderKind.Anthropic
            ? new Uri($"{root}/v1/models?limit={PageSize}")
            : new Uri($"{root}/models");
    }

    private static void Authenticate(HttpRequestMessage request, ProviderKind kind, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return;   // OpenRouter lists its catalogue without one, and a proxy may not want one
        }

        if (kind == ProviderKind.Anthropic)
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    /// <summary>Ids out of <c>data[]</c>, which is the one thing every provider's answer has in common.</summary>
    private static IReadOnlyList<string> Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return data.EnumerateArray()
            .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null)
            .OfType<string>()
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static string Detail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        string line = body.Trim().ReplaceLineEndings(" ");
        return $": {(line.Length > 160 ? line[..160] + "..." : line)}";
    }
}
