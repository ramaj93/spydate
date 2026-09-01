using Spydate.Agent;
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
            StrongSpan t => t.Text,
            EmphasisSpan t => t.Text,
            CodeSpan t => t.Text,
            _ => string.Empty,
        }));

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
                Assert.Equal("reads the header", Flatten(list.Items[0].Spans));
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

        Assert.Contains(spans, s => s is StrongSpan { Text: "CreateFileW" });

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
    public void NothingAtAllThrows(string? input)
    {
        var blocks = Markdown.Parse(input);
        Assert.NotNull(blocks);
    }
}

/// <summary>Conversations kept between runs.</summary>
public sealed class ChatLogTests
{
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
