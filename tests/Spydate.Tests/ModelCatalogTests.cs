using System.Collections.Specialized;
using System.Net;
using System.Text;
using Spydate.Agent.Providers;

namespace Spydate.Tests;

/// <summary>
/// Asking a provider which models it has. Answered by a server on this machine, so the two response
/// shapes and every way the request can fail are exercised without a key or a network.
/// </summary>
public sealed class ModelCatalogTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _prefix;
    private readonly CancellationTokenSource _stopping = new();

    private string _body = "{}";
    private HttpStatusCode _status = HttpStatusCode.OK;

    /// <summary>Headers of the last request, so the auth each provider needs can be checked.</summary>
    private NameValueCollection? _lastHeaders;
    private string? _lastPath;

    public ModelCatalogTests()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Close();
        _stopping.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _lastHeaders = context.Request.Headers;
            _lastPath = context.Request.Url?.PathAndQuery;

            byte[] bytes = Encoding.UTF8.GetBytes(_body);
            context.Response.StatusCode = (int)_status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.Close();
        }
    }

    private ProviderSettings Settings(ProviderKind kind = ProviderKind.OpenAi)
        => new() { Kind = kind, Model = "unused", Endpoint = _prefix.TrimEnd('/') };

    [Fact]
    public async Task AnOpenAiCompatibleListComesBackSorted()
    {
        _body = """{"object":"list","data":[{"id":"gpt-5"},{"id":"gpt-4o"},{"id":"o3"}]}""";

        var result = await ModelCatalog.ListAsync(Settings(), "sk-test");

        Assert.True(result.Ok, result.Problem);
        Assert.Equal(new[] { "gpt-4o", "gpt-5", "o3" }, result.Models);
        Assert.Equal("Bearer sk-test", _lastHeaders!["Authorization"]);
    }

    [Fact]
    public async Task AnthropicIsAskedItsOwnWay()
    {
        // Different path, different auth header, and a page size — it defaults to twenty, which is
        // fewer models than it has.
        _body = """{"data":[{"id":"claude-opus-5","display_name":"Claude Opus 5","type":"model"}],"has_more":false}""";

        var result = await ModelCatalog.ListAsync(Settings(ProviderKind.Anthropic), "sk-ant-test");

        Assert.True(result.Ok, result.Problem);
        Assert.Equal(new[] { "claude-opus-5" }, result.Models);
        Assert.Equal("sk-ant-test", _lastHeaders!["x-api-key"]);
        Assert.Equal("2023-06-01", _lastHeaders["anthropic-version"]);
        Assert.Contains("/v1/models", _lastPath, StringComparison.Ordinal);
        Assert.Contains("limit=1000", _lastPath, StringComparison.Ordinal);
        Assert.Null(_lastHeaders["Authorization"]);
    }

    [Theory]
    [InlineData(ProviderKind.OpenAi, "https://api.openai.com/v1/models")]
    [InlineData(ProviderKind.OpenRouter, "https://openrouter.ai/api/v1/models")]
    [InlineData(ProviderKind.DeepSeek, "https://api.deepseek.com/v1/models")]
    public void TheOpenAiCompatibleProvidersAskAtTheSamePlace(ProviderKind kind, string expected)
        => Assert.Equal(expected, ModelCatalog.ModelsUri(new ProviderSettings { Kind = kind }).ToString());

    [Fact]
    public async Task ARejectedKeySaysSoRatherThanLookingEmpty()
    {
        // The status is the useful half: 401 means the key, not that the provider has no models.
        _status = HttpStatusCode.Unauthorized;
        _body = """{"error":{"message":"Incorrect API key provided"}}""";

        var result = await ModelCatalog.ListAsync(Settings(), "sk-wrong");

        Assert.False(result.Ok);
        Assert.Contains("401", result.Problem, StringComparison.Ordinal);
        Assert.Contains("Incorrect API key", result.Problem, StringComparison.Ordinal);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task AnEndpointWithoutTheModelsRouteIsNotFatal()
    {
        // A proxy or a compatible server need not implement it, and typing a model still works.
        _status = HttpStatusCode.NotFound;
        _body = "not found";

        var result = await ModelCatalog.ListAsync(Settings(), "sk-test");

        Assert.False(result.Ok);
        Assert.Contains("404", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonsenseComesBackAsAProblemRatherThanAnException()
    {
        _body = "<html>not json at all</html>";

        var result = await ModelCatalog.ListAsync(Settings(), "sk-test");

        Assert.False(result.Ok);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task AnEmptyListIsReportedAsEmptyRatherThanAsSuccess()
    {
        _body = """{"object":"list","data":[]}""";

        var result = await ModelCatalog.ListAsync(Settings(), "sk-test");

        Assert.False(result.Ok);
        Assert.Contains("listed no models", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingIsSentWhenThereIsNoKey()
    {
        // OpenRouter publishes its catalogue without one, so a key is not required to look.
        _body = """{"data":[{"id":"anthropic/claude-sonnet-5"}]}""";

        var result = await ModelCatalog.ListAsync(Settings(ProviderKind.OpenRouter), apiKey: null);

        Assert.True(result.Ok, result.Problem);
        Assert.Null(_lastHeaders!["Authorization"]);
    }

    [Fact]
    public async Task AServerThatIsNotThereIsReportedNotThrown()
    {
        var settings = new ProviderSettings { Kind = ProviderKind.OpenAi, Model = "m", Endpoint = "http://127.0.0.1:1/v1" };

        var result = await ModelCatalog.ListAsync(settings, "sk-test");

        Assert.False(result.Ok);
        Assert.Contains("could not reach", result.Problem, StringComparison.Ordinal);
    }
}
