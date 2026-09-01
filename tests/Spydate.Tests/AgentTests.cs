using Microsoft.Extensions.AI;
using Spydate.Agent;
using Spydate.Agent.Providers;
using Spydate.Agent.Secrets;
using Spydate.Mcp;
using Spydate.Mcp.Session;

namespace Spydate.Tests;

/// <summary>
/// The assistant panel's engine: the tools a model is offered, and the loop that runs them. A fake
/// chat client stands in for a provider, so all of this is exercised without an API key and without
/// a network — the parts that would need one are the two SDKs, which are not ours to test.
/// </summary>
public class AgentTests
{
    private static SessionStore Store(string path)
    {
        var analysis = Corpus.Analysed(path);
        var store = new SessionStore();
        store.Set(new BinarySession(path, Corpus.Image(path), analysis, null, new DiscoveryState(analysis.FunctionCount, true, TimeSpan.Zero)));
        return store;
    }

    // ------------------------------------------------------------------
    // The tool surface, which is shared with the MCP server rather than rebuilt
    // ------------------------------------------------------------------

    [Fact]
    public void TheModelIsOfferedTheSameToolsTheMcpServerPublishes()
    {
        var tools = AnalysisAgent.ToolsFor(new SessionStore(), McpOptions.Default);

        // Two hosts, one definition. A second copy would drift, and the half that drifted would be
        // the one nobody was testing.
        Assert.Contains(tools, t => t.Name == "open_binary");
        Assert.Contains(tools, t => t.Name == "list_functions");
        Assert.Contains(tools, t => t.Name == "read_function");
        Assert.Contains(tools, t => t.Name == "annotate");
        Assert.True(tools.Count >= 13, $"only {tools.Count} tools were offered");
    }

    [Fact]
    public void EveryToolTheModelSeesExplainsItself()
    {
        // These descriptions are the only documentation the model gets, and they are sent every turn.
        foreach (var tool in AnalysisAgent.ToolsFor(new SessionStore(), McpOptions.Default))
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), $"{tool.Name} has no description");
        }
    }

    // ------------------------------------------------------------------
    // The loop
    // ------------------------------------------------------------------

    [Fact]
    public async Task AToolTheModelAsksForIsRunAndItsAnswerComesBack()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        // The model asks for list_functions, the loop runs it against the real session, and what the
        // tool returned reaches the model on the next turn. That round trip is the whole feature.
        var fake = new ScriptedChatClient(
            turn1: new FunctionCallContent("call-1", "list_functions", new Dictionary<string, object?> { ["limit"] = 3 }),
            turn2: "there are functions");

        using var agent = new AnalysisAgent(fake, Store(Corpus.NotepadX64), McpOptions.Default, new ProviderSettings { Model = "test" });

        var steps = new List<AgentStep>();
        string answer = await agent.AskAsync("what is in here?", new Progress<AgentStep>(steps.Add));

        Assert.Equal("there are functions", answer);
        Assert.Contains(steps, s => s.Kind == "tool" && s.Text.StartsWith("list_functions(", StringComparison.Ordinal));

        // The tool's own output, not a summary of it, went back to the model.
        Assert.Contains(fake.ToolResults, r => r.Contains("0x", StringComparison.Ordinal) && r.Contains("blocks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusedWriteIsReportedRatherThanThrown()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        // A tool that throws inside the loop looks to the model like the request was impossible. One
        // that answers tells it what to do instead, which is the difference between a retry and a stall.
        var fake = new ScriptedChatClient(
            turn1: new FunctionCallContent("call-1", "annotate", new Dictionary<string, object?> { ["target"] = "0x140001000", ["name"] = "Nope" }),
            turn2: "it would not let me");

        using var agent = new AnalysisAgent(
            fake,
            Store(Corpus.NotepadX64),
            new McpOptions { ReadOnly = true },
            new ProviderSettings { Model = "test" });

        await agent.AskAsync("rename something");

        Assert.Contains(fake.ToolResults, r => r.Contains("--read-only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheConversationIsKeptSoTheModelCanFollowItsOwnWork()
    {
        var fake = new ScriptedChatClient(turn1: null, turn2: "hello");
        using var agent = new AnalysisAgent(fake, new SessionStore(), McpOptions.Default, new ProviderSettings { Model = "test" });

        await agent.AskAsync("first");

        Assert.Contains(agent.History, m => m.Role == ChatRole.System);
        Assert.Contains(agent.History, m => m.Role == ChatRole.User && m.Text == "first");

        agent.Reset();

        // Reset forgets the conversation but not the instructions.
        Assert.Single(agent.History);
        Assert.Equal(ChatRole.System, agent.History[0].Role);
    }

    // ------------------------------------------------------------------
    // Providers
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(ProviderKind.OpenAi, "https://api.openai.com/v1")]
    [InlineData(ProviderKind.OpenRouter, "https://openrouter.ai/api/v1")]
    [InlineData(ProviderKind.DeepSeek, "https://api.deepseek.com/v1")]
    [InlineData(ProviderKind.Anthropic, "https://api.anthropic.com")]
    public void EachProviderKnowsWhereToGo(ProviderKind kind, string expected)
        => Assert.Equal(expected, new ProviderSettings { Kind = kind }.BaseUri.ToString().TrimEnd('/'));

    [Fact]
    public void AnEndpointCanBeOverriddenForAProxyOrACompatibleServer()
        => Assert.Equal("https://gateway.internal/v1", new ProviderSettings { Endpoint = "https://gateway.internal/v1" }.BaseUri.ToString().TrimEnd('/'));

    [Fact]
    public void AProviderWithNoModelIsNotUsable()
    {
        Assert.False(new ProviderSettings().IsComplete);
        Assert.True(new ProviderSettings { Model = "claude-sonnet-5" }.IsComplete);
        Assert.Throws<ArgumentException>(() => ChatProviders.Create(new ProviderSettings(), "sk-test"));
    }

    [Fact]
    public void EveryProviderBuildsAClientFromNothingButAKeyAndAModel()
    {
        // Constructing must not reach the network, so this proves the four are wired up without one
        // of them needing anything the settings dialog does not ask for.
        foreach (var kind in Enum.GetValues<ProviderKind>())
        {
            using var client = ChatProviders.Create(
                new ProviderSettings { Kind = kind, Model = ProviderSettings.SuggestedModel(kind) },
                "sk-not-a-real-key");

            Assert.NotNull(client);
        }
    }

    // ------------------------------------------------------------------
    // Keys
    // ------------------------------------------------------------------

    [Fact]
    public void AKeyGoesInAndComesBackAndCanBeForgotten()
    {
        ISecretStore store = new InMemorySecretStore();

        store.Set("Anthropic", "sk-ant-secret");
        Assert.Equal("sk-ant-secret", store.Get("Anthropic"));
        Assert.Equal(new[] { "Anthropic" }, store.Names());

        store.Set("Anthropic", null);
        Assert.Null(store.Get("Anthropic"));
        Assert.Empty(store.Names());
    }

    [Fact]
    public void AKeyOnDiskIsEncryptedToThisAccountAndNotReadableAsText()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), "spydate-keys-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new DpapiSecretStore(directory);
            store.Set("OpenAi", "sk-plainly-visible-if-this-fails");

            Assert.Equal("sk-plainly-visible-if-this-fails", store.Get("OpenAi"));

            // The point of DPAPI: what lands on disk is not the key. Copying this file elsewhere,
            // or to another account, yields nothing.
            string onDisk = File.ReadAllText(Path.Combine(directory, "OpenAi.key"));
            Assert.DoesNotContain("sk-plainly-visible", onDisk, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void AMissingOrDamagedKeyReadsAsAbsentRatherThanThrowing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), "spydate-keys-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "DeepSeek.key"), new byte[] { 1, 2, 3, 4 });
            var store = new DpapiSecretStore(directory);

            // A key written by another account looks exactly like this, and "no key configured" is
            // the truth from here; a crash on startup would not be.
            Assert.Null(store.Get("DeepSeek"));
            Assert.Null(store.Get("Anthropic"));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void AProviderNameCannotEscapeTheKeyFolder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The attribute as well as the guard: the analyser cannot see a runtime check from inside
        // the lambda below, and suppressing it would hide the next call that really is unguarded.
        var store = new DpapiSecretStore(Path.GetTempPath());

        Assert.Throws<ArgumentException>(() => store.Get(@"..\..\something"));
    }

    /// <summary>
    /// A provider that says what it was told to say: one tool call, then an answer. Enough to drive
    /// the loop end to end without a key, and it records what the tools handed back.
    /// </summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly FunctionCallContent? _call;
        private readonly string _final;
        private int _turn;

        public ScriptedChatClient(FunctionCallContent? turn1, string turn2)
        {
            _call = turn1;
            _final = turn2;
        }

        /// <summary>What the tools returned, as the model would have seen it.</summary>
        public List<string> ToolResults { get; } = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            foreach (var result in messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>())
            {
                ToolResults.Add(result.Result?.ToString() ?? string.Empty);
            }

            if (_turn++ == 0 && _call is not null)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent> { _call })));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _final)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("the panel does not stream yet");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
