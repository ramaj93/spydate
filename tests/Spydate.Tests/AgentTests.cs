using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Spydate.Agent;
using Spydate.Agent.Providers;
using Spydate.Agent.Text;
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

    [Fact]
    public async Task AnEarlierConversationIsTheFirstMessageAndNotPartOfTheInstructions()
    {
        var fake = new ScriptedChatClient(turn1: null, turn2: "hello");
        using var agent = new AnalysisAgent(
            fake, new SessionStore(), McpOptions.Default,
            new ProviderSettings { Model = "test" },
            earlier: "assistant: sub_140001000 looks like a CRC table build. Want me to name it?");

        await agent.AskAsync("yes");

        // The instructions stay the instructions: they are about the binary and never stop being
        // true, and a record of one afternoon does not belong in them.
        Assert.Equal(ChatRole.System, agent.History[0].Role);
        Assert.DoesNotContain("CRC table build", agent.History[0].Text, StringComparison.Ordinal);

        Assert.Equal(ChatRole.User, agent.History[1].Role);
        Assert.Contains("CRC table build", agent.History[1].Text, StringComparison.Ordinal);

        // And it says what it is, so "yes" is answered against a record rather than a request.
        Assert.Contains("they did not type it now", agent.History[1].Text, StringComparison.Ordinal);

        // The question itself is still its own message, after it.
        Assert.Contains(agent.History, m => m.Role == ChatRole.User && m.Text == "yes");
    }

    [Fact]
    public async Task TheEarlierConversationIsTheFirstThingDroppedWhenTheWindowFills()
    {
        // The budget is measured off the real system prompt rather than guessed at, so that editing
        // that prompt cannot quietly turn this into a test of nothing: too small and the record
        // never survives the first question, too large and nothing is ever dropped at all.
        using var probe = new AnalysisAgent(
            new ScriptedChatClient(turn1: null, turn2: "x"), new SessionStore(), McpOptions.Default,
            new ProviderSettings { Model = "test" });

        string record = "assistant: shall I carry on with the hot list? " + new string('.', 4000);
        int room = probe.History[0].Text.Length + record.Length + 1000;

        var fake = new ScriptedChatClient(turn1: null, turn2: "hello");
        using var agent = new AnalysisAgent(
            fake, new SessionStore(), McpOptions.Default,
            new ProviderSettings { Model = "test", MaxContextTokens = room / 4 },
            earlier: record);

        await agent.AskAsync("yes");
        Assert.Contains(agent.History, m => m.Text.Contains("hot list", StringComparison.Ordinal));

        // Enough to go over, and still smaller than the record, so exactly one thing has to go.
        await agent.AskAsync("and the next one " + new string('.', 1000));

        // Shed before any of the real conversation, which is the right order: by now the reference
        // it existed to resolve has been resolved, and the exchange after it has not.
        Assert.DoesNotContain(agent.History, m => m.Text.Contains("hot list", StringComparison.Ordinal));
        Assert.Equal(ChatRole.System, agent.History[0].Role);
        Assert.Contains(agent.History, m => m.Role == ChatRole.User && m.Text == "yes");
    }

    /// <summary>
    /// Stop, then ask something else. The conversation before the stop is untouched; what matters is
    /// that the stopped turn is not a hole — the panel still shows the half-answer, and a history
    /// without it puts the model back to not seeing what the reader is looking at.
    /// </summary>
    [Fact]
    public async Task StoppingATurnKeepsWhatItHadSaidAndSaysWhatWasLost()
    {
        var stop = new CancellationTokenSource();
        using var agent = new AnalysisAgent(
            new StoppingChatClient(stop), new SessionStore(), McpOptions.Default,
            new ProviderSettings { Model = "test" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => agent.AskAsync("what does the entry point do", cancellationToken: stop.Token));

        // The question is answered by something, rather than left with nothing after it.
        Assert.Equal(ChatRole.User, agent.History[^2].Role);
        Assert.Equal(ChatRole.Assistant, agent.History[^1].Role);

        // What it managed to say is kept, and the part it can no longer stand behind is flagged.
        Assert.Contains("the entry point sets up", agent.History[^1].Text, StringComparison.Ordinal);
        Assert.Contains("Stopped there", agent.History[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskingAgainAfterAStopStillHasEverythingSaidBeforeIt()
    {
        var stop = new CancellationTokenSource();
        using var agent = new AnalysisAgent(
            new StoppingChatClient(stop), new SessionStore(), McpOptions.Default,
            new ProviderSettings { Model = "test" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => agent.AskAsync("first", cancellationToken: stop.Token));

        // The next question carries its own token, the way the panel gives each turn a fresh one.
        await agent.AskAsync("second");

        // Cancelling ended one request, not the session: everything from before it is still here,
        // including the half-answer the stop interrupted.
        Assert.Contains(agent.History, m => m.Role == ChatRole.User && m.Text == "first");
        Assert.Contains(agent.History, m => m.Role == ChatRole.User && m.Text == "second");
        Assert.Contains(agent.History, m => m.Text.Contains("the entry point sets up", StringComparison.Ordinal));
        Assert.Contains(agent.History, m => m.Text.Contains("carrying on then", StringComparison.Ordinal));
    }

    /// <summary>
    /// Says half an answer and then stops, the way pressing Stop mid-stream does — once. The turn
    /// after it answers normally, so that what a stop does to the conversation can be looked at from
    /// the other side of one.
    /// </summary>
    private sealed class StoppingChatClient(CancellationTokenSource stop) : IChatClient
    {
        private int _turn;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new OperationCanceledException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_turn++ == 0)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "the entry point sets up");

                await stop.CancelAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "carrying on then");
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// The turn degenerated into markup, but not before it had really called tools. Those calls
    /// happened - in a process that is running - so throwing them away made the retry begin the
    /// work again, which for a debugging session means starting the binary over and losing
    /// everywhere it had reached.
    /// </summary>
    [Fact]
    public async Task WorkAlreadyDoneSurvivesARecoveredTurn()
    {
        var client = new DegradingChatClient();
        using var agent = new AnalysisAgent(client, new SessionStore(), McpOptions.Default, new ProviderSettings { Model = "test" });

        await agent.AskAsync("what does the entry point do");

        // The call it really made, and what came back, are both still in the conversation.
        Assert.Contains(agent.History.SelectMany(m => m.Contents).OfType<FunctionCallContent>(), c => c.Name == "read_function");
        Assert.Contains(agent.History.SelectMany(m => m.Contents).OfType<FunctionResultContent>(), r => r.Result?.ToString() == "the listing");

        // And the template that ran nothing is not, in any message.
        Assert.DoesNotContain(agent.History.SelectMany(m => m.Contents).OfType<TextContent>(), t => ToolCallMarkup.Present(t.Text));

        // The second request saw the first attempt is work, so it asks it to carry on rather than
        // handing it the bare question again.
        Assert.True(client.SecondRequestSawTheToolResult, "the retry did not carry the work forward");
    }

    /// <summary>Streams a real tool call, then a leaked template; answers plainly the second time.</summary>
    private sealed class DegradingChatClient : IChatClient
    {
        private int _turn;

        public bool SecondRequestSawTheToolResult { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            SecondRequestSawTheToolResult = messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionResultContent>()
                .Any(r => r.Result?.ToString() == "the listing");

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "it sets up the CRT.")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _turn++;

            // A call that really happened, its result, and then the template that did not.
            yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("call-1", "read_function", new Dictionary<string, object?> { ["target"] = "entry" }),
            });

            yield return new ChatResponseUpdate(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("call-1", "the listing"),
            });

            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                "Now I will look at the caller.\n\n<｜｜DSML｜｜tool_calls>\n<｜｜DSML｜｜invoke name=\"debug_run\">");

            await Task.CompletedTask.ConfigureAwait(false);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
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
    // How much conversation is carried
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(ProviderKind.Anthropic, "claude-sonnet-5")]
    [InlineData(ProviderKind.OpenRouter, "google/gemini-2.5-pro")]
    [InlineData(ProviderKind.OpenAi, "gpt-5")]
    [InlineData(ProviderKind.DeepSeek, "deepseek-chat")]
    public void EveryModelIsOfferedMoreThanTheOneNumberEverybodyUsedToGet(ProviderKind kind, string model)
    {
        // The point of the table is that it is not one number. Anything at or below the old flat
        // default for a model with a large window means it has fallen back to a default it should
        // not have reached, which is the failure this replaced and would be invisible otherwise.
        Assert.True(
            ProviderSettings.SuggestedContextTokens(kind, model) > ProviderSettings.LegacyContextTokens,
            $"{model} was suggested no more than the old flat default");
    }

    [Fact]
    public void TheModelDecidesTheBudgetBeforeTheProviderDoes()
    {
        // OpenRouter serves every family, so under it the provider name says nothing at all about
        // the window and only the model id does.
        Assert.Equal(
            ProviderSettings.SuggestedContextTokens(ProviderKind.Anthropic, "claude-sonnet-5"),
            ProviderSettings.SuggestedContextTokens(ProviderKind.OpenRouter, "anthropic/claude-sonnet-5"));

        Assert.NotEqual(
            ProviderSettings.SuggestedContextTokens(ProviderKind.OpenRouter, "anthropic/claude-sonnet-5"),
            ProviderSettings.SuggestedContextTokens(ProviderKind.OpenRouter, "google/gemini-2.5-pro"));
    }

    [Fact]
    public void AModelNobodyHasHeardOfStillGetsSomethingUsable()
    {
        foreach (var kind in Enum.GetValues<ProviderKind>())
        {
            Assert.True(ProviderSettings.SuggestedContextTokens(kind, "some-model-shipped-tomorrow") >= 96_000);
            Assert.True(ProviderSettings.SuggestedContextTokens(kind, null) >= 96_000);
        }
    }

    [Fact]
    public void SettingsStillHoldingTheOldFlatDefaultAreReadAsNeverSet()
    {
        string path = Path.Combine(Path.GetTempPath(), $"spydate-agent-{Guid.NewGuid():N}.json");
        try
        {
            new AgentSettings
            {
                Provider = ProviderKind.OpenRouter,
                Model = "google/gemini-2.5-pro",
                MaxContextTokens = ProviderSettings.LegacyContextTokens,
            }.Save(path);

            var loaded = AgentSettings.Load(path);

            Assert.Equal(
                ProviderSettings.SuggestedContextTokens(ProviderKind.OpenRouter, "google/gemini-2.5-pro"),
                loaded.MaxContextTokens);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SettingsWithNoContextKeyAtAllFollowTheirOwnModelAndNotSomeOtherProvidersDefault()
    {
        string path = Path.Combine(Path.GetTempPath(), $"spydate-agent-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "provider": "deepSeek", "model": "deepseek-chat" }""");

            var loaded = AgentSettings.Load(path);

            Assert.Null(loaded.ContextTokens);
            Assert.Equal(
                ProviderSettings.SuggestedContextTokens(ProviderKind.DeepSeek, "deepseek-chat"),
                loaded.MaxContextTokens);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AContextSizeSomebodyChoseIsLeftAlone()
    {
        string path = Path.Combine(Path.GetTempPath(), $"spydate-agent-{Guid.NewGuid():N}.json");
        try
        {
            new AgentSettings { Model = "claude-sonnet-5", MaxContextTokens = 12_000 }.Save(path);
            Assert.Equal(12_000, AgentSettings.Load(path).MaxContextTokens);
        }
        finally
        {
            File.Delete(path);
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

        /// <summary>
        /// The same script, in pieces. The answer is deliberately split so the agent has to put it
        /// back together — a version that yielded it whole would pass without testing anything.
        /// </summary>
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var result in messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>())
            {
                ToolResults.Add(result.Result?.ToString() ?? string.Empty);
            }

            if (_turn++ == 0 && _call is not null)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { _call });
                yield break;
            }

            int half = _final.Length / 2;
            yield return new ChatResponseUpdate(ChatRole.Assistant, _final[..half]);
            yield return new ChatResponseUpdate(ChatRole.Assistant, _final[half..]);

            await Task.CompletedTask.ConfigureAwait(false);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
