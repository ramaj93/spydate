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

        // read_file among them, so the in-app assistant can look at a blob open_binary cannot — a .inx
        // beside the target, say — rather than reconstructing its bytes out of process memory.
        Assert.Contains(tools, t => t.Name == "read_file");
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

        var steps = new Steps();
        string answer = await agent.AskAsync("what is in here?", steps);

        Assert.Equal("there are functions", answer);
        Assert.Contains(steps.All, s => s.Kind == "tool" && s.Text.StartsWith("list_functions(", StringComparison.Ordinal));

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

    /// <summary>An earlier conversation's tool call and its result, the way a restart hands them back.</summary>
    private static RestoredHistory Earlier(bool fromEarlierRun = true, ProviderKind provider = ProviderKind.Anthropic) =>
        new(
            new List<ChatMessage>
            {
                new(ChatRole.User, "what is sub_140001000"),
                new(ChatRole.Assistant, new List<AIContent>
                {
                    new FunctionCallContent("call-1", "read_function", new Dictionary<string, object?> { ["target"] = "sub_140001000" }),
                }),
                new(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("call-1", "it builds a CRC table") }),
                new(ChatRole.Assistant, "sub_140001000 builds a CRC table. Want me to name it?"),
            },
            provider,
            fromEarlierRun);

    [Fact]
    public async Task ARestoredConversationIsReplayedAsHistoryWithItsToolCallsAndResults()
    {
        var fake = new ScriptedChatClient(turn1: null, turn2: "done");
        using var agent = new AnalysisAgent(
            fake, new SessionStore(), McpOptions.Default,
            new ProviderSettings { Model = "test" },
            restored: Earlier());

        await agent.AskAsync("yes");

        // The instructions stay first and are freshly built, not carried from the file.
        Assert.Equal(ChatRole.System, agent.History[0].Role);
        Assert.DoesNotContain("CRC table", agent.History[0].Text, StringComparison.Ordinal);

        // The earlier turn is really there — the call it made and the result it got, not a paraphrase
        // of them — so the model builds on what it found rather than finding it again.
        Assert.Contains(
            agent.History.SelectMany(m => m.Contents).OfType<FunctionCallContent>(),
            c => c.Name == "read_function");
        Assert.Contains(
            agent.History.SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
            r => r.Result?.ToString() == "it builds a CRC table");

        // One marker after the restored messages says the run they came from has ended, then the new
        // question follows it.
        int marker = IndexOf(agent.History, m => m.Text.Contains("earlier run of Spydate", StringComparison.Ordinal));
        int asked = IndexOf(agent.History, m => m.Role == ChatRole.User && m.Text == "yes");
        Assert.True(marker >= 0 && asked > marker, "the marker should sit between the restored history and the new question");
    }

    [Fact]
    public async Task AWithinRunSwitchReplaysTheHistoryWithoutTheEarlierRunMarker()
    {
        var fake = new ScriptedChatClient(turn1: null, turn2: "done");
        using var agent = new AnalysisAgent(
            fake, new SessionStore(), McpOptions.Default,
            new ProviderSettings { Model = "test" },
            restored: Earlier(fromEarlierRun: false));

        await agent.AskAsync("yes");

        // Nothing has restarted, so the debugger-state warning would be a lie; the history is still
        // all there.
        Assert.DoesNotContain(agent.History, m => m.Text.Contains("earlier run of Spydate", StringComparison.Ordinal));
        Assert.Contains(
            agent.History.SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
            r => r.Result?.ToString() == "it builds a CRC table");
    }

    [Fact]
    public async Task ARestoredConversationIsTrimmedOldestFirstBeforeTheFirstQuestion()
    {
        // Budget measured off the real system prompt plus one big restored message, so exactly the
        // oldest exchange has to go once the new question pushes it over — and editing the prompt
        // cannot quietly turn this into a test of nothing.
        using var probe = new AnalysisAgent(
            new ScriptedChatClient(turn1: null, turn2: "x"), new SessionStore(), McpOptions.Default,
            new ProviderSettings { Model = "test" });

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "the old one " + new string('.', 4000)),
            new(ChatRole.Assistant, "answered the old one"),
            new(ChatRole.User, "the recent one"),
            new(ChatRole.Assistant, "answered the recent one"),
        };

        // Room for the system prompt and everything but the 4000-character oldest message, so the
        // new question pushes the total over and exactly that oldest exchange has to be shed.
        int room = probe.History[0].Text.Length + 2000;

        var fake = new ScriptedChatClient(turn1: null, turn2: "done");
        using var agent = new AnalysisAgent(
            fake, new SessionStore(), McpOptions.Default,
            new ProviderSettings { Model = "test", MaxContextTokens = room / 4 },
            restored: new RestoredHistory(history, ProviderKind.Anthropic, FromEarlierRun: false));

        var progress = new CollectingProgress();
        await agent.AskAsync("carry on", progress);

        // The oldest exchange is shed; the recent one and the new question stay.
        Assert.DoesNotContain(agent.History, m => m.Text.StartsWith("the old one", StringComparison.Ordinal));
        Assert.Contains(agent.History, m => m.Text == "the recent one");
        Assert.Contains(agent.History, m => m.Role == ChatRole.User && m.Text == "carry on");
        Assert.Equal(ChatRole.System, agent.History[0].Role);

        // And the reader is told memory was shed, not left to wonder.
        Assert.Contains(progress.Notes, n => n.Contains("left the assistant's memory", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ProviderKind.Anthropic, ProviderKind.Anthropic, true)]
    [InlineData(ProviderKind.OpenAi, ProviderKind.Anthropic, false)]
    [InlineData(ProviderKind.DeepSeek, ProviderKind.DeepSeek, false)]
    public void AHistoryReplaysAsBlocksOnlyForTheSameProviderAndNeverDeepSeek(ProviderKind current, ProviderKind saved, bool expected)
        => Assert.Equal(expected, AnalysisAgent.Replayable(current, saved));

    [Fact]
    public void AFlattenedHistoryHasNoToolBlocksAndItsRecordsAreUserMessages()
    {
        var flat = ChatLog.Flatten(Earlier().Messages);

        Assert.DoesNotContain(flat.SelectMany(m => m.Contents), c => c is FunctionCallContent or FunctionResultContent);

        // The tool result survives as readable text, in a user-role record — never an assistant
        // message, which would teach the model to write tool calls as prose.
        var record = Assert.Single(flat, m => m.Text.Contains("it builds a CRC table", StringComparison.Ordinal));
        Assert.Equal(ChatRole.User, record.Role);
        Assert.Contains("read_function", record.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARestoredHalfPairIsDroppedRatherThanSent()
    {
        // A file written by a crash between two saves can hold a call with no result. A provider
        // rejects that outright, so it must not reach one.
        var history = new List<ChatMessage>
        {
            new(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("orphan", "read_function", new Dictionary<string, object?> { ["target"] = "x" }),
            }),
        };

        var fake = new ScriptedChatClient(turn1: null, turn2: "done");
        using var agent = new AnalysisAgent(
            fake, new SessionStore(), McpOptions.Default,
            new ProviderSettings { Model = "test" },
            restored: new RestoredHistory(history, ProviderKind.Anthropic, FromEarlierRun: false));

        await agent.AskAsync("hello");

        Assert.DoesNotContain(
            agent.History.SelectMany(m => m.Contents).OfType<FunctionCallContent>(),
            c => c.CallId == "orphan");
    }

    private static int IndexOf(IReadOnlyList<ChatMessage> history, Func<ChatMessage, bool> match)
    {
        for (int i = 0; i < history.Count; i++)
        {
            if (match(history[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class CollectingProgress : IProgress<AgentStep>
    {
        public List<string> Notes { get; } = new();

        public void Report(AgentStep value)
        {
            if (value.Kind == "note")
            {
                Notes.Add(value.Text);
            }
        }
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

    /// <summary>
    /// The panel has to show what it is doing whether or not there is a stream to show it from.
    /// A recovery retry is never streamed, and the streaming setting can be off, and either way a
    /// turn that reads several functions is minutes of silence with no way to tell working from
    /// hung.
    /// </summary>
    [Fact]
    public async Task ToolCallsAreReportedEvenWhenNothingIsStreaming()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var fake = new ScriptedChatClient(
            turn1: new FunctionCallContent("call-1", "list_functions", new Dictionary<string, object?> { ["limit"] = 3 }),
            turn2: "there are functions");

        using var agent = new AnalysisAgent(
            fake, Store(Corpus.NotepadX64), McpOptions.Default,
            new ProviderSettings { Model = "test", Stream = false });

        var steps = new Steps();
        await agent.AskAsync("what is in here?", steps);

        Assert.Contains(steps.All, s => s.Kind == "tool" && s.Text.StartsWith("list_functions(", StringComparison.Ordinal));
    }

    /// <summary>
    /// A salvaged turn ends on a tool result, which is a conversation caught mid-sentence. DeepSeek
    /// reads that as a reasoning turn still in progress and demands the reasoning behind the call,
    /// which does not survive being folded into messages - so the retry was refused outright with a
    /// 400, a worse failure than the dropped call it was recovering from.
    /// </summary>
    [Fact]
    public async Task ARecoveredTurnIsClosedOffBeforeItIsSentAgain()
    {
        var client = new DegradingChatClient();
        using var agent = new AnalysisAgent(client, new SessionStore(), McpOptions.Default, new ProviderSettings { Model = "test" });

        await agent.AskAsync("what does the entry point do");

        // Nothing is left hanging: the last thing before the retry was asked for is the assistant
        // speaking, not a tool result waiting for somebody to make something of it.
        int lastResult = -1;
        for (int i = 0; i < agent.History.Count; i++)
        {
            if (agent.History[i].Contents.OfType<FunctionResultContent>().Any())
            {
                lastResult = i;
            }
        }

        Assert.True(lastResult >= 0, "the tool result was not kept at all");
        Assert.Contains(
            agent.History.Skip(lastResult + 1),
            m => m.Role == ChatRole.Assistant && m.Text.Length > 0);
    }

    /// <summary>
    /// A message carries more than its contents. A thinking model's reasoning rides on it, and
    /// DeepSeek refuses the whole request with a 400 when the reasoning behind a tool call does not
    /// come back with the call — so salvaging must hand back the messages themselves and never
    /// equivalent ones built from their parts.
    /// </summary>
    [Fact]
    public void SalvagingKeepsTheMessagesThemselvesRatherThanCopiesOfThem()
    {
        var call = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call-1", "read_function", new Dictionary<string, object?> { ["target"] = "entry" }),
        })
        {
            RawRepresentation = "reasoning: read the entry point first",
        };

        var result = new ChatMessage(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("call-1", "the listing") });
        var template = new ChatMessage(ChatRole.Assistant, "Now the caller.\n\n<｜｜DSML｜｜tool_calls>");

        var kept = AnalysisAgent.Salvage([call, result, template]).ToList();

        Assert.Equal(2, kept.Count);
        Assert.Same(call, kept[0]);
        Assert.Same(result, kept[1]);
        Assert.Equal("reasoning: read the entry point first", kept[0].RawRepresentation);
    }

    [Fact]
    public void DroppingAMessageTakesItsCallsWithItAndTheResultsThatAnsweredThem()
    {
        // The template shared a message with the call, so the call goes too — and the result that
        // answered it is then as broken as a call with no result. Providers reject either.
        var both = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call-1", "xrefs", null),
            new TextContent("looking now.\n\n<invoke name=\"debug_memory\">"),
        });

        var answer = new ChatMessage(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("call-1", "sites") });
        var fine = new ChatMessage(ChatRole.Assistant, "an ordinary sentence");

        var kept = AnalysisAgent.Salvage([both, answer, fine]).ToList();

        Assert.Single(kept);
        Assert.Same(fine, kept[0]);
    }

    /// <summary>Streams a real tool call, then a leaked template; answers plainly the second time.</summary>
    private sealed class DegradingChatClient : IChatClient
    {
        private int _turn;

        /// <summary>Whether the retry request carried the result of the call that really ran.</summary>
        public bool SecondRequestSawTheToolResult { get; private set; }

        private void Notice(IEnumerable<ChatMessage> messages)
        {
            if (_turn > 0)
            {
                SecondRequestSawTheToolResult |= messages
                    .SelectMany(m => m.Contents)
                    .OfType<FunctionResultContent>()
                    .Any(r => r.Result?.ToString() == "the listing");
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Notice(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "it sets up the CRT.")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // The retry is streamed too, because that is the transport the session is using.
            Notice(messages);

            if (_turn++ > 0)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "carrying on then");
                await Task.CompletedTask.ConfigureAwait(false);
                yield break;
            }

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

    // ------------------------------------------------------------------
    // Running out of tool calls, which is not the same as being finished
    // ------------------------------------------------------------------

    [Fact]
    public async Task SpendingTheWholeToolBudgetIsSaidOutLoudRatherThanJustHappening()
    {
        // The loop takes the tools away for its last request, so a well-behaved model answers - and
        // the answer reads like a conclusion when it is really a model finding its tools gone. The
        // turn is not broken; what was missing is anyone saying why it ended.
        var fake = new BudgetChatClient(callsAnyway: false);
        using var agent = new AnalysisAgent(fake, new SessionStore(), McpOptions.Default,
            new ProviderSettings { Model = "test", MaxToolCalls = 3, Stream = false });

        var steps = new Steps();
        string answer = await agent.AskAsync("go", steps);

        Assert.Equal("as far as I got", answer);
        Assert.Contains(steps.All, s => s.Kind == "note" && s.Text.Contains("all 3 tool calls", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACallMadeAfterTheToolsWereTakenAwayStillEndsInAnAnswer()
    {
        // The hard shape: the model asks for a tool it can no longer be given, so nothing runs and
        // the turn has no answer at all. Before, that reached the panel as silence - the assistant
        // appearing to quit mid-investigation with no reason given and no Stop having been clicked.
        var fake = new BudgetChatClient(callsAnyway: true);
        using var agent = new AnalysisAgent(fake, new SessionStore(), McpOptions.Default,
            new ProviderSettings { Model = "test", MaxToolCalls = 3, Stream = false });

        var steps = new Steps();
        string answer = await agent.AskAsync("go", steps);

        Assert.Equal("as far as I got", answer);
        Assert.Contains(steps.All, s => s.Kind == "note" && s.Text.Contains("about to call list_functions", StringComparison.Ordinal));

        // And the turn it was told about is one it was actually asked to sum up, without its tools.
        Assert.Contains(fake.Asked, m => m.Text.Contains("last of the 3 tool calls", StringComparison.Ordinal));
        Assert.Contains(0, fake.ToolsOffered);
    }

    [Fact]
    public async Task TheHalfPairLeftBehindByTheBudgetDoesNotReachTheNextQuestion()
    {
        // A call with no result is what providers reject outright, so leaving one in the history
        // would not spoil the turn that produced it - it would break the next one, somewhere the
        // cause is no longer visible.
        var fake = new BudgetChatClient(callsAnyway: true);
        using var agent = new AnalysisAgent(fake, new SessionStore(), McpOptions.Default,
            new ProviderSettings { Model = "test", MaxToolCalls = 3, Stream = false });

        await agent.AskAsync("go");

        var answered = agent.History.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Select(r => r.CallId).ToHashSet(StringComparer.Ordinal);
        var orphans = agent.History.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .Where(c => !answered.Contains(c.CallId)).ToList();

        Assert.Empty(orphans);

        // And it ends on something said, not on a tool result: a conversation caught mid-exchange is
        // the shape a thinking model refuses to be handed back.
        Assert.Equal(ChatRole.Assistant, agent.History[^1].Role);
        Assert.NotEmpty(agent.History[^1].Text);
    }

    /// <summary>
    /// The steps a turn reported, collected as they are reported.
    ///
    /// Not <see cref="Progress{T}"/>, which is what the panel uses and is right there: it captures a
    /// synchronisation context so the UI is updated on the UI thread, and a test has none, so every
    /// callback is queued to the thread pool instead. The steps then arrive some time after the turn
    /// they belong to has finished — usually before the assertion that reads them, sometimes not.
    /// That is a test which passes for reasons unrelated to what it is testing, and it was failing
    /// about one run in twenty for the same reason. Reporting straight onto the list removes the
    /// question entirely.
    /// </summary>
    private sealed class Steps : IProgress<AgentStep>
    {
        private readonly List<AgentStep> _steps = new();

        public IReadOnlyList<AgentStep> All => _steps;

        public void Report(AgentStep value) => _steps.Add(value);
    }

    /// <summary>
    /// A model with more to do than the budget allows, so the loop always runs out on it.
    ///
    /// <paramref name="callsAnyway"/> is the difference between the two ways that ends. With it off,
    /// the model does the reasonable thing when the loop takes its tools away for the last request
    /// and answers in words. With it on it asks for a call regardless — which some do — and the turn
    /// comes back with no answer and a call that nothing ran. Either way it answers once it is told
    /// in words that the budget is gone, which is the only thing the wrap-up request adds.
    /// </summary>
    private sealed class BudgetChatClient(bool callsAnyway) : IChatClient
    {
        private int _turn;

        /// <summary>How many tools each request was offered, so the wrap-up can be shown to have none.</summary>
        public List<int> ToolsOffered { get; } = new();

        /// <summary>The last messages sent, so the nudge that went with the wrap-up can be read.</summary>
        public List<ChatMessage> Asked { get; } = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            int tools = options?.Tools?.Count ?? 0;
            ToolsOffered.Add(tools);
            Asked.Clear();
            Asked.AddRange(messages);

            bool told = Asked.Any(m => m.Text.Contains("tool calls allowed in one turn", StringComparison.Ordinal));
            if (!told && (tools > 0 || callsAnyway))
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    new List<AIContent> { new FunctionCallContent($"c{++_turn}", "list_functions", new Dictionary<string, object?> { ["limit"] = 1 }) })));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "as far as I got")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var content in (await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false)).Messages[0].Contents)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { content });
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
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
