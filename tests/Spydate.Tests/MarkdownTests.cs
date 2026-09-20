using Microsoft.Extensions.AI;
using Spydate.Agent;
using Spydate.Agent.Providers;
using Spydate.Agent.Text;

namespace Spydate.Tests;

/// <summary>
/// The renderer behind the assistant panel. The input is whatever a model writes, so the parser is
/// held to two standards: it produces the obvious thing for ordinary prose, and it produces
/// <em>something</em> for anything at all.
/// </summary>
public sealed class MarkdownTests
{
    private static string Flatten(IEnumerable<MarkdownSpan> spans)
        => string.Concat(spans.Select(s => s switch
        {
            TextSpan t => t.Text,
            CodeSpan t => t.Text,
            LineBreakSpan => "\n",
            StrongSpan t => Flatten(t.Children),
            EmphasisSpan t => Flatten(t.Children),
            StrikeSpan t => Flatten(t.Children),
            LinkSpan t => Flatten(t.Children),
            _ => string.Empty,
        }));

    private static IReadOnlyList<MarkdownSpan> Spans(MarkdownBlock block)
        => Assert.IsType<ParagraphBlock>(block).Spans;

    [Fact]
    public void PlainProseIsOneParagraph()
    {
        var blocks = Markdown.Parse("This function opens a file and returns a handle.");

        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(blocks));
        Assert.Equal("This function opens a file and returns a handle.", Flatten(paragraph.Spans));
    }

    [Fact]
    public void HeadingsCodeAndListsComeOutAsThemselves()
    {
        var blocks = Markdown.Parse("""
            ## What it does

            Opens the file, then:

            - reads the header
            - checks the magic

            ```c
            int f(void) { return 1; }
            ```
            """);

        Assert.Collection(
            blocks,
            b => Assert.Equal(2, Assert.IsType<HeadingBlock>(b).Level),
            b => Assert.Equal("Opens the file, then:", Flatten(Assert.IsType<ParagraphBlock>(b).Spans)),
            b =>
            {
                var list = Assert.IsType<ListBlock>(b);
                Assert.False(list.Ordered);
                Assert.Equal(2, list.Items.Count);
                Assert.Equal("reads the header", Flatten(Spans(list.Items[0].Blocks[0])));
            },
            b =>
            {
                var code = Assert.IsType<CodeBlock>(b);
                Assert.Equal("c", code.Language);
                Assert.Equal("int f(void) { return 1; }", code.Text);
            });
    }

    [Fact]
    public void NumberedAndBulletedListsDoNotRunTogether()
    {
        var blocks = Markdown.Parse("""
            1. first
            2. second
            - a bullet
            """);

        Assert.Collection(
            blocks,
            b => Assert.True(Assert.IsType<ListBlock>(b).Ordered),
            b => Assert.False(Assert.IsType<ListBlock>(b).Ordered));
    }

    [Fact]
    public void EmphasisIsRecognisedAndCodeProtectsWhatIsInsideIt()
    {
        var blocks = Markdown.Parse("Call **CreateFileW** through `sub_401000_helper` now.");
        var spans = Assert.IsType<ParagraphBlock>(Assert.Single(blocks)).Spans;

        Assert.Contains(spans, s => s is StrongSpan strong && Flatten(strong.Children) == "CreateFileW");

        // The underscores inside the code span must survive as themselves.
        Assert.Contains(spans, s => s is CodeSpan { Text: "sub_401000_helper" });
    }

    [Fact]
    public void AnUnmatchedDelimiterStaysLiteral()
    {
        // Arithmetic and bare symbol names are far more common here than emphasis, and turning
        // either into italics silently corrupts what the model actually said.
        Assert.Equal("size * 2 and sub_401000", Flatten(Assert.IsType<ParagraphBlock>(Markdown.Parse("size * 2 and sub_401000")[0]).Spans));
        Assert.Equal("a _lone underscore", Flatten(Assert.IsType<ParagraphBlock>(Markdown.Parse("a _lone underscore")[0]).Spans));
        Assert.Equal("`unclosed backtick", Flatten(Assert.IsType<ParagraphBlock>(Markdown.Parse("`unclosed backtick")[0]).Spans));
    }

    [Theory]
    [InlineData("sub_401000")]
    [InlineData("call find_strings then find_symbol")]
    [InlineData("__imp_CreateFileW")]
    [InlineData("a_b_c_d_e")]
    [InlineData("<||DSML||tool_calls> find_strings sub_1800D2F00 <||DSML||tool_calls>")]
    public void UnderscoresInsideNamesAreLeftAlone(string input)
    {
        // Every underscore must survive. Two names in one line used to pair their underscores into
        // an italic, which both deleted them and swallowed everything in between — and identifiers
        // are the single most common thing written about a binary.
        string rendered = Flatten(Assert.IsType<ParagraphBlock>(Assert.Single(Markdown.Parse(input))).Spans);

        Assert.Equal(input, rendered);
        Assert.Equal(input.Count(c => c == '_'), rendered.Count(c => c == '_'));
    }

    [Fact]
    public void EmphasisWithUnderscoresStillWorksBetweenWords()
    {
        var spans = Assert.IsType<ParagraphBlock>(Assert.Single(Markdown.Parse("this is _really_ important"))).Spans;
        Assert.Contains(spans, s => s is EmphasisSpan emphasis && Flatten(emphasis.Children) == "really");
    }

    [Fact]
    public void SingleNewlinesInsideAParagraphAreKept()
    {
        // Answers list addresses one per line; folding those into a space makes them unreadable.
        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(Markdown.Parse("0x401000 open\n0x401020 close")));
        Assert.Contains("\n", Flatten(paragraph.Spans), StringComparison.Ordinal);
    }

    [Fact]
    public void AnAnswerCutOffMidCodeBlockStillParses()
    {
        var blocks = Markdown.Parse("here:\n\n```c\nint f(void)");

        Assert.Equal(2, blocks.Count);
        Assert.Equal("int f(void)", Assert.IsType<CodeBlock>(blocks[1]).Text);
    }

    [Fact]
    public void APipeTableComesOutWithItsHeaderRowAndAlignments()
    {
        var blocks = Markdown.Parse("""
            | Name | Address |
            |:-----|--------:|
            | main | 0x401000 |
            | init | 0x401020 |
            """);

        var table = Assert.IsType<TableBlock>(Assert.Single(blocks));
        Assert.Equal(3, table.Rows.Count);
        Assert.True(table.Rows[0].IsHeader);
        Assert.False(table.Rows[1].IsHeader);
        Assert.Equal(2, table.Rows[0].Cells.Count);
        Assert.Equal("Name", Flatten(table.Rows[0].Cells[0]));
        Assert.Equal("0x401000", Flatten(table.Rows[1].Cells[1]));
        Assert.Equal([TableAlign.Left, TableAlign.Right], table.Aligns);
    }

    [Fact]
    public void AQuoteHoldsItsOwnBlocks()
    {
        var quote = Assert.IsType<QuoteBlock>(Assert.Single(Markdown.Parse("> the model is guessing here")));
        Assert.Equal("the model is guessing here", Flatten(Spans(Assert.Single(quote.Blocks))));
    }

    [Fact]
    public void AnIndentedBulletNestsInsideItsParent()
    {
        var blocks = Markdown.Parse("""
            - outer
              - inner
            """);

        var outer = Assert.IsType<ListBlock>(Assert.Single(blocks));
        var item = Assert.Single(outer.Items);

        // The item holds its own paragraph and, as another of its blocks, the nested list.
        Assert.Contains(item.Blocks, b => b is ListBlock);
        var inner = Assert.IsType<ListBlock>(item.Blocks.First(b => b is ListBlock));
        Assert.Equal("inner", Flatten(Spans(Assert.Single(inner.Items).Blocks[0])));
    }

    [Fact]
    public void ATaskItemCarriesItsBoxAndNotTheBracketsAsText()
    {
        var blocks = Markdown.Parse("""
            - [x] done
            - [ ] todo
            """);

        var list = Assert.IsType<ListBlock>(Assert.Single(blocks));
        Assert.Equal(2, list.Items.Count);
        Assert.True(list.Items[0].Checked);
        Assert.False(list.Items[1].Checked);
        Assert.Equal("done", Flatten(Spans(list.Items[0].Blocks[0])));
        Assert.Equal("todo", Flatten(Spans(list.Items[1].Blocks[0])));
    }

    [Fact]
    public void StrikethroughIsItsOwnSpan()
    {
        var spans = Assert.IsType<ParagraphBlock>(Assert.Single(Markdown.Parse("this is ~~wrong~~ now"))).Spans;
        Assert.Contains(spans, s => s is StrikeSpan strike && Flatten(strike.Children) == "wrong");
    }

    [Fact]
    public void ALinkKeepsItsTextAndUrl()
    {
        var link = Assert.IsType<LinkSpan>(Assert.Single(
            Assert.IsType<ParagraphBlock>(Assert.Single(Markdown.Parse("[the docs](http://example.com/x)"))).Spans));

        Assert.Equal("the docs", Flatten(link.Children));
        Assert.Equal("http://example.com/x", link.Url);
    }

    [Fact]
    public void AnImageIsALinkOfItsAltTextAndIsNeverFetched()
    {
        // The record for an image is a LinkSpan: nothing in the tree can make a request, and its
        // children are the alt text so the reader sees what it was meant to show.
        var span = Assert.Single(
            Assert.IsType<ParagraphBlock>(Assert.Single(Markdown.Parse("![a diagram](http://example.com/y.png)"))).Spans);

        var link = Assert.IsType<LinkSpan>(span);
        Assert.Equal("a diagram", Flatten(link.Children));
        Assert.Equal("http://example.com/y.png", link.Url);
    }

    [Theory]
    [InlineData("<b>bold</b>")]
    [InlineData("<||DSML||tool_calls> find_strings")]
    public void HtmlAndToolTemplatesStayLiteralText(string input)
    {
        // HTML is off, so a tag or a leaked tool-call template is shown as the characters it is,
        // never interpreted. Every one of them survives in the flattened text.
        string rendered = Flatten(Assert.IsType<ParagraphBlock>(Assert.Single(Markdown.Parse(input))).Spans);
        Assert.Equal(input, rendered);
    }

    [Fact]
    public void AThematicBreakIsARule()
    {
        Assert.IsType<RuleBlock>(Assert.Single(Markdown.Parse("---")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("***")]
    [InlineData("```")]
    [InlineData("#")]
    [InlineData("#######  not a heading")]
    [InlineData("- ")]
    [InlineData("1.")]
    [InlineData("`` ` ``")]
    [InlineData("**")]
    [InlineData("| a |")]
    [InlineData("|---|")]
    [InlineData(">")]
    [InlineData("~~")]
    [InlineData("- [ ]")]
    public void NothingAtAllThrows(string? input)
    {
        var blocks = Markdown.Parse(input);
        Assert.NotNull(blocks);
    }
}

/// <summary>
/// The plain text an answer reads as — what copying gives and searching looks through. It is one
/// walk over the parsed answer, the same walk the window draws from, so an offset into this text is
/// an offset into what is on screen.
/// </summary>
public sealed class FlatTextTests
{
    [Fact]
    public void ParagraphsAreSeparatedByABlankLine()
    {
        Assert.Equal("one\n\ntwo", FlatText.Of("one\n\ntwo"));
    }

    [Fact]
    public void CodeIsKeptVerbatim()
    {
        string flat = FlatText.Of("```c\nint f(void) { return 1; }\n```");
        Assert.Equal("int f(void) { return 1; }", flat);
    }

    [Fact]
    public void ATableIsTabsBetweenCellsAndNewlinesBetweenRows()
    {
        string flat = FlatText.Of("""
            | a | b |
            |---|---|
            | 1 | 2 |
            """);

        Assert.Equal("a\tb\n1\t2", flat);
    }

    [Fact]
    public void AListKeepsItsMarkers()
    {
        string flat = FlatText.Of("- one\n- two");
        Assert.Equal("- one\n- two", flat);
    }

    [Fact]
    public void ATaskListReadsAsBoxes()
    {
        string flat = FlatText.Of("- [x] done\n- [ ] todo");
        Assert.Equal("☑ done\n☐ todo", flat);
    }

    [Fact]
    public void EveryCharacterIsAccountedForByExactlyOneSinkCall()
    {
        // The invariant the whole selection design rests on: the counting sink and FlatText see the
        // same length, because every character of the flat text comes through one sink call.
        const string markdown = """
            # Heading

            A paragraph with **bold**, _italic_, `code`, ~~strike~~ and [a link](http://x).

            - one
              - nested
            - [x] a task

            > a quote

            | h1 | h2 |
            |----|----|
            | c1 | c2 |

            ```c
            int f(void);
            ```

            ---
            """;

        var blocks = Markdown.Parse(markdown);
        var counter = new CountingSink();
        MarkdownWalk.Walk(blocks, counter);

        Assert.Equal(FlatText.Of(blocks).Length, counter.Count);
    }

    private sealed class CountingSink : IMarkdownSink
    {
        public int Count { get; private set; }

        public void Open(BlockKind kind, BlockInfo info)
        {
        }

        public void Close(BlockKind kind)
        {
        }

        public void Text(string text, SpanInfo style) => Count += text.Length;

        public void Marker(string text) => Count += text.Length;

        public void Glue(string text) => Count += text.Length;
    }
}

/// <summary>Conversations kept between runs.</summary>
public sealed class ChatLogTests
{
    [Fact]
    public void AHistoryRoundTripsWithItsToolCallAndResult()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"spydate-chat-{Guid.NewGuid():N}");
        try
        {
            string path = ChatLog.PathFor(@"C:\bin\game.exe", directory);
            var history = new List<ChatMessage>
            {
                new(ChatRole.User, "what is sub_401000"),
                new(ChatRole.Assistant, new List<AIContent>
                {
                    new FunctionCallContent("call-1", "read_function", new Dictionary<string, object?> { ["target"] = "sub_401000" }),
                }),
                new(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("call-1", "it builds a CRC table") }),
                new(ChatRole.Assistant, "sub_401000 builds a CRC table."),
            };

            ChatLog.SaveBook(path, new ChatBook
            {
                Sessions = [new ChatSession
                {
                    Entries = [new ChatEntry { Kind = "you", Text = "what is sub_401000" }],
                    History = history,
                    Provider = ProviderKind.OpenAi,
                    Model = "gpt-5",
                }],
            });

            var session = Assert.Single(ChatLog.LoadBook(path).Sessions);

            Assert.Equal(ProviderKind.OpenAi, session.Provider);
            Assert.Equal("gpt-5", session.Model);

            // The call is back with its name and its argument.
            var call = Assert.Single(session.History.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
            Assert.Equal("read_function", call.Name);
            Assert.Equal("sub_401000", call.Arguments?["target"]?.ToString());

            // The result is back as a string, not a JsonElement — so it re-sends unquoted and its
            // length is measured right.
            var result = Assert.Single(session.History.SelectMany(m => m.Contents).OfType<FunctionResultContent>());
            Assert.IsType<string>(result.Result);
            Assert.Equal("it builds a CRC table", result.Result);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ALegacyFileWithNoHistoryLoadsWithOneBuiltFromItsEntries()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"spydate-chat-{Guid.NewGuid():N}");
        try
        {
            // A file from before histories were kept: a bare array of display entries.
            string path = ChatLog.PathFor(@"C:\bin\old.exe", directory);
            ChatLog.Save(path, new[]
            {
                new ChatEntry { Kind = "you", Text = "what runs first" },
                new ChatEntry { Kind = "tool", Text = "read_function(target=start)" },
                new ChatEntry { Kind = "assistant", Text = "the entry point" },
            });

            var session = Assert.Single(ChatLog.LoadBook(path).Sessions);

            // A text-only history, tool line dropped, so the model still has something to carry on
            // from even though the old format never stored its own messages.
            Assert.Collection(
                session.History,
                m => { Assert.Equal(ChatRole.User, m.Role); Assert.Equal("what runs first", m.Text); },
                m => { Assert.Equal(ChatRole.Assistant, m.Role); Assert.Equal("the entry point", m.Text); });
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void AnEmptyHistoryRoundTrips()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"spydate-chat-{Guid.NewGuid():N}");
        try
        {
            string path = ChatLog.PathFor(@"C:\bin\blank.exe", directory);
            ChatLog.SaveBook(path, new ChatBook
            {
                Sessions = [new ChatSession
                {
                    Entries = [new ChatEntry { Kind = "you", Text = "hi" }],
                    History = [],
                }],
            });

            var session = Assert.Single(ChatLog.LoadBook(path).Sessions);

            // No history stored, so it is rebuilt from the one entry rather than coming back null.
            Assert.Single(session.History);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void AConversationComesBackAsItWasWritten()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"spydate-chat-{Guid.NewGuid():N}");
        try
        {
            string path = ChatLog.PathFor(@"C:\bin\notepad.exe", directory);
            var written = new[]
            {
                new ChatEntry { Kind = "you", Text = "what is at 0x401000?", At = DateTimeOffset.UnixEpoch },
                new ChatEntry { Kind = "assistant", Text = "a **file** opener", At = DateTimeOffset.UnixEpoch },
            };

            ChatLog.Save(path, written);
            var read = ChatLog.Load(path);

            Assert.Equal(2, read.Count);
            Assert.Equal("you", read[0].Kind);
            Assert.Equal("a **file** opener", read[1].Text);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void HistoryFromKeepsProseAndDropsToolAndNoteLines()
    {
        // A legacy log holds only the display transcript. What can be replayed is what a person said
        // and what the assistant said; a tool line is the call with never its result, and a note is
        // the panel talking to itself — neither has a place in the model's history.
        var entries = new List<ChatEntry>
        {
            new() { Kind = "you", Text = "what is at 0x401000" },
            new() { Kind = "tool", Text = "read_function(target=sub_401000)" },
            new() { Kind = "note", Text = "— an aside from the panel —" },
            new() { Kind = "assistant", Text = "a file opener" },
        };

        var history = ChatLog.HistoryFrom(entries);

        Assert.Collection(
            history,
            m => { Assert.Equal(ChatRole.User, m.Role); Assert.Equal("what is at 0x401000", m.Text); },
            m => { Assert.Equal(ChatRole.Assistant, m.Role); Assert.Equal("a file opener", m.Text); });
    }

    [Fact]
    public void HistoryFromStripsLeakedToolCallMarkupAndDropsWhatIsOnlyMarkup()
    {
        // Taken from a real log: the model failed to make the call and printed its own template as
        // text, and the panel stored what it displayed. A history built from that keeps the prose
        // and drops the template — a message that was nothing but template drops out entirely.
        var entries = new List<ChatEntry>
        {
            new()
            {
                Kind = "assistant",
                Text = "I want to verify the GString candidate.\n\n"
                       + "<｜｜DSML｜｜tool_calls>\n"
                       + "<｜｜DSML｜｜invoke name=\"read_function\">\n"
                       + "</｜｜DSML｜｜tool_calls>",
            },
            new() { Kind = "assistant", Text = "<invoke name=\"xrefs\"><parameter name=\"target\">x</parameter></invoke>" },
        };

        var history = ChatLog.HistoryFrom(entries);

        var kept = Assert.Single(history);
        Assert.Equal("I want to verify the GString candidate.", kept.Text);
    }

    [Theory]
    [InlineData("plain prose about sub_401000", "plain prose about sub_401000")]
    [InlineData("some words <|tool_call|> junk", "some words")]
    [InlineData("<invoke name=\"read_function\">", "")]
    [InlineData("said it <tool_call>{}</tool_call>", "said it")]
    public void MarkupIsCutFromTheFirstSentinelOnwards(string text, string expected)
        => Assert.Equal(expected, ChatLog.WithoutMarkup(text));

    [Fact]
    public void HistoryFromKeepsNothingWorthlessAndNeverThrowsOnEmpty()
    {
        Assert.Empty(ChatLog.HistoryFrom([]));
        Assert.Empty(ChatLog.HistoryFrom([new ChatEntry { Kind = "tool", Text = "xrefs(target=x)" }]));
    }

    [Fact]
    public void TwoBinariesWithTheSameNameDoNotShareALog()
    {
        Assert.NotEqual(
            ChatLog.PathFor(@"C:\one\setup.exe"),
            ChatLog.PathFor(@"C:\two\setup.exe"));

        // The same binary is the same log whatever case the path was typed in.
        Assert.Equal(
            ChatLog.PathFor(@"C:\one\setup.exe"),
            ChatLog.PathFor(@"c:\ONE\setup.exe"));
    }

    [Fact]
    public void SavingNothingRemovesTheLogRatherThanLeavingAnEmptyOne()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"spydate-chat-{Guid.NewGuid():N}");
        try
        {
            string path = ChatLog.PathFor(@"C:\bin\a.exe", directory);
            ChatLog.Save(path, new[] { new ChatEntry { Kind = "you", Text = "hello" } });
            Assert.True(File.Exists(path));

            ChatLog.Save(path, Array.Empty<ChatEntry>());

            Assert.False(File.Exists(path));
            Assert.Empty(ChatLog.Load(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ADamagedLogReadsAsNoConversationRatherThanThrowing()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"spydate-chat-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            string path = ChatLog.PathFor(@"C:\bin\a.exe", directory);
            File.WriteAllText(path, "{ this is not json");

            Assert.Empty(ChatLog.Load(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

/// <summary>
/// A tool call the model wrote out as prose instead of making. Nothing runs, the turn ends
/// mid-thought, and it reads as the model being stupid rather than as a call dropped in transit.
/// </summary>
public sealed class ToolCallMarkupTests
{
    [Theory]
    [InlineData("<｜｜DSML｜｜tool_calls>")]
    [InlineData("<|tool_call|>")]
    [InlineData("<invoke name=\"debug_state\">")]
    [InlineData("<function_call>")]
    [InlineData("</parameter>")]
    public void EachProvidersShapeIsRecognised(string markup)
        => Assert.True(ToolCallMarkup.Present($"I will check that.\n\n{markup}"), markup);

    [Theory]
    [InlineData("sub_140001000 copies a GString and returns it.")]
    [InlineData("The comparison is a < b, and the flag at bit 6 is ZF.")]
    [InlineData("It calls operator new<T> and then frees it.")]
    public void OrdinaryProseAboutABinaryIsNot(string text)
        => Assert.False(ToolCallMarkup.Present(text), text);

    [Fact]
    public void TheToolItMeantToCallIsReadBackSoTheNoteCanSayWhich()
    {
        Assert.Equal("debug_state", ToolCallMarkup.NameIn(
            "<｜｜DSML｜｜tool_calls>\n<｜｜DSML｜｜invoke name=\"debug_state\">\n</｜｜DSML｜｜tool_calls>"));

        Assert.Equal("find_strings", ToolCallMarkup.NameIn("<invoke name='find_strings'>"));
        Assert.Null(ToolCallMarkup.NameIn("nothing here at all"));
    }

    [Fact]
    public void TheProseBeforeTheMarkupIsKeptAndTheMarkupIsNot()
    {
        Assert.Equal(
            "I want to verify the GString candidate.",
            ToolCallMarkup.Without("I want to verify the GString candidate.\n\n<｜｜DSML｜｜tool_calls>\n<｜｜DSML｜｜invoke name=\"read_function\">"));

        Assert.Equal(string.Empty, ToolCallMarkup.Without("<invoke name=\"xrefs\"></invoke>"));
    }
}
