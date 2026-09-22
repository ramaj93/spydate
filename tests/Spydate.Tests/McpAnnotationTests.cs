using Spydate.Core.PE;
using Spydate.Core.Project;
using Spydate.Disassembly;
using Spydate.Mcp;
using Spydate.Mcp.Session;
using Spydate.Mcp.Tools;

namespace Spydate.Tests;

/// <summary>
/// The agent naming things. These build their own analysis rather than using the shared corpus —
/// AGENTS.md is explicit that a test whose point is a mutation must own what it mutates, because
/// every other test in the run would otherwise see it.
/// </summary>
public sealed class McpAnnotationTests : IDisposable
{
    private const ulong Entry = 0x140001000;

    /// <summary>
    /// The callee, not the entry. The entry is the first byte of the section, where the section's own
    /// symbol wins, so naming it reads back as ".text" and says nothing about the code.
    /// </summary>
    private const ulong Callee = 0x14000100A;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "spydate-mcp-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly PeImage _image;
    private readonly BinaryAnalysis _analysis;
    private readonly SessionStore _store;
    private readonly NoteStore _notes;
    private readonly string _projectPath;

    public McpAnnotationTests()
    {
        // call +5 ; ret ; xor eax,eax ; ret — two functions, so there is something to name.
        var code = new byte[0x40];
        Array.Fill(code, (byte)0xCC);
        new byte[] { 0xE8, 0x05, 0x00, 0x00, 0x00 }.CopyTo(code, 0x00);
        new byte[] { 0xC3 }.CopyTo(code, 0x05);
        new byte[] { 0x31, 0xC0, 0xC3 }.CopyTo(code, 0x0A);

        _image = SyntheticPe.WithSectionData(code);
        _analysis = new BinaryAnalysis(_image);
        _analysis.Annotations.Source = AnnotationSource.Agent;
        _analysis.DiscoverAll();
        _analysis.GetOrDiscoverFunction(Entry);
        _analysis.GetOrDiscoverFunction(Callee);

        Directory.CreateDirectory(_directory);
        _projectPath = Path.Combine(_directory, "sample.spydate");

        _notes = new NoteStore { Source = AnnotationSource.Agent };
        _store = new SessionStore();
        _store.Set(new BinarySession(
            "sample.exe",
            _image,
            _analysis,
            null,
            new DiscoveryState(_analysis.FunctionCount, true, TimeSpan.Zero),
            save: (image, annotations) =>
            {
                SpydateProject.SaveTo(_projectPath, image, annotations, notes: _notes);
                return _projectPath;
            },
            notes: _notes));
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AnnotationTools Tools(bool readOnly = false)
        => new(_store, readOnly ? new McpOptions { ReadOnly = true } : McpOptions.Default);

    [Fact]
    public void ANameIsAppliedAndSaidBack()
    {
        string answer = Tools().Annotate($"0x{Callee:X}", name: "ParseCommandLine");

        Assert.Contains("is now ParseCommandLine", answer, StringComparison.Ordinal);
        Assert.Contains("was sub_14000100A", answer, StringComparison.Ordinal);
        Assert.Contains("saved to", answer, StringComparison.Ordinal);
        Assert.Equal("ParseCommandLine", _analysis.NameFor(Callee));
    }

    [Fact]
    public void TheNameThatWasStoredIsEchoed_NotTheOneAskedFor()
    {
        // CleanName turns whitespace into underscores and truncates at 255. An agent that assumes
        // its own string went in will go looking for a name that does not exist.
        string answer = Tools().Annotate($"0x{Callee:X}", name: "parse command line");

        Assert.Contains("parse_command_line", answer, StringComparison.Ordinal);
        Assert.Equal("parse_command_line", _analysis.NameFor(Callee));
    }

    [Fact]
    public void ANameTheAgentGaveCanBeUsedAsATargetAfterwards()
    {
        // The loop depends on this: an agent has to be able to build on what it just established.
        Tools().Annotate($"0x{Callee:X}", name: "Established");

        string answer = Tools().Annotate("Established", comment: "found by following its caller");

        Assert.Contains("comment: found by following its caller", answer, StringComparison.Ordinal);
        Assert.Equal("found by following its caller", _analysis.CommentFor(Callee));
    }

    [Fact]
    public void ClearingANameGoesBackToWhatAnalysisFound()
    {
        var tools = Tools();
        tools.Annotate($"0x{Callee:X}", name: "Wrong");

        string answer = tools.Annotate($"0x{Callee:X}", name: string.Empty);

        Assert.Contains("is back to sub_14000100A", answer, StringComparison.Ordinal);
        Assert.Equal("sub_14000100A", _analysis.NameFor(Callee));
    }

    [Fact]
    public void AStackSlotIsNamedUnderItsOwnFunction()
    {
        string answer = Tools().AnnotateLocal($"0x{Callee:X}", "arg_0", "commandLine");

        Assert.Contains("arg_0 in", answer, StringComparison.Ordinal);
        Assert.Contains("is now commandLine", answer, StringComparison.Ordinal);
        Assert.Equal("commandLine", _analysis.Annotations.LocalNameFor(Callee, "arg_0"));
    }

    [Fact]
    public void AnAddressOutsideTheImageIsRefusedBeforeItIsSilentlyLost()
    {
        // It has no RVA, so it could never be written to a project file; accepting it would mean
        // reporting success for something that vanishes at save time.
        string answer = Tools().Annotate("0xDEADBEEF00", name: "Nowhere");

        Assert.Contains("outside the image", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddressPastTheEndOfTheImageIsRefusedToo()
    {
        // Above the image base but past its end. VaToRva alone accepts this - it only asks whether
        // the address is at or above the base - so it used to be written to the project file as a
        // plausible-looking RVA for a place that does not exist.
        string answer = Tools().Annotate($"0x{Entry + 0x10000000:X}", name: "Nowhere");

        Assert.Contains("outside the image", answer, StringComparison.Ordinal);
        Assert.Equal(0, _analysis.Annotations.Count);
    }

    [Fact]
    public void ReadOnlyRefusesToWriteAndSaysWhy()
    {
        string named = Tools(readOnly: true).Annotate($"0x{Callee:X}", name: "ShouldNotStick");
        string local = Tools(readOnly: true).AnnotateLocal($"0x{Callee:X}", "arg_0", "neither");

        Assert.Contains("--read-only", named, StringComparison.Ordinal);
        Assert.Contains("--read-only", local, StringComparison.Ordinal);
        Assert.Equal("sub_14000100A", _analysis.NameFor(Callee));
        Assert.Equal(0, _analysis.Annotations.Count);
    }

    [Fact]
    public void WhatTheAgentDidCanBeListedAndToldApartFromWhatAPersonDid()
    {
        var tools = Tools();
        tools.Annotate($"0x{Callee:X}", name: "ByAgent");

        _analysis.Annotations.Source = AnnotationSource.User;
        tools.Annotate($"0x{Entry:X}", name: "ByHand");

        string all = tools.ListAnnotations();
        string agentOnly = tools.ListAnnotations(source: "agent");

        Assert.Contains("ByAgent", all, StringComparison.Ordinal);
        Assert.Contains("ByHand", all, StringComparison.Ordinal);
        Assert.Contains("ByAgent", agentOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("ByHand", agentOnly, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatTheAgentWroteIsWhatTheWindowWouldRead()
    {
        // The end of the chain, and the promise the whole design rests on: the agent and the window
        // share one file, so a name given here shows up there.
        var tools = Tools();
        tools.Annotate($"0x{Callee:X}", name: "SeenByTheWindow", comment: "and its comment");
        tools.AnnotateLocal($"0x{Callee:X}", "arg_0", "slotName");

        var fresh = new AnnotationStore();
        var result = SpydateProject.Load(_projectPath, _image, fresh);

        Assert.True(result.Loaded, result.Reason);
        Assert.Equal("SeenByTheWindow", fresh.NameFor(Callee));
        Assert.Equal("and its comment", fresh.CommentFor(Callee));
        Assert.Equal("slotName", fresh.LocalNameFor(Callee, "arg_0"));
        Assert.Equal(AnnotationSource.Agent, fresh.Get(Callee)!.Source);
    }

    [Fact]
    public void AgentWritesDoNotEraseWhatSomebodyElseRecorded()
    {
        // The window has the same binary open and saves while the agent is working.
        var person = new AnnotationStore();
        person.SetName(Entry, "TypedInTheWindow");
        SpydateProject.SaveTo(_projectPath, _image, person);

        Tools().Annotate($"0x{Callee:X}", name: "AddedByAgent");

        var fresh = new AnnotationStore();
        SpydateProject.Load(_projectPath, _image, fresh);

        Assert.Equal("TypedInTheWindow", fresh.NameFor(Entry));
        Assert.Equal("AddedByAgent", fresh.NameFor(Callee));
    }

    // --- notes --------------------------------------------------------

    [Fact]
    public void ANoteIsSavedAndEchoesTheCleanedKey()
    {
        string result = Tools().Note("String XOR", "strings are xor'd with 0x5A");

        Assert.Contains("string-xor", result);
        Assert.Contains("saved", result);

        var fresh = new NoteStore();
        SpydateProject.Load(_projectPath, _image, new AnnotationStore(), notes: fresh);
        Assert.Equal("strings are xor'd with 0x5A", fresh.Get("string-xor")!.Text);
    }

    [Fact]
    public void AnOverLongNoteIsRefusedWithTheOverageAndNothingIsWritten()
    {
        string tooLong = new('x', NoteStore.MaxTextLength + 12);
        string result = Tools().Note("overview", tooLong);

        Assert.Contains("12", result);
        Assert.Equal(0, _notes.Count);
    }

    [Fact]
    public void ReadNotesListsEveryKeyEvenWhenBodiesAreClipped()
    {
        // Sections whose bodies together far exceed the budget, so the index must still name them all.
        string big = new('a', 3_500);
        for (int i = 0; i < 8; i++)
        {
            Tools().Note($"section-{i}", big);
        }

        string index = Tools().ReadNotes();
        for (int i = 0; i < 8; i++)
        {
            Assert.Contains($"section-{i}", index);
        }

        Assert.Contains("read_notes(offset=", index);
    }

    [Fact]
    public void NoteIndexOrdersTheSections()
    {
        Tools().Note("details", "written first");
        Tools().Note("overview", "belongs first", index: -1);

        string index = Tools().ReadNotes();
        Assert.True(index.IndexOf("overview", StringComparison.Ordinal) < index.IndexOf("details", StringComparison.Ordinal),
            "overview should be listed before details");
    }

    [Fact]
    public void NoteWithAnIndexAndNoTextReordersWithoutClearing()
    {
        Tools().Note("overview", "the text stays");
        string result = Tools().Note("overview", index: 5);

        Assert.Contains("moved", result);
        Assert.Equal("the text stays", _notes.Get("overview")!.Text);
    }

    [Fact]
    public void ReadNotesWithAKeyReturnsThatSectionWhole()
    {
        string body = "line one\nline two\nline three";
        Tools().Note("overview", body);

        string one = Tools().ReadNotes("overview");
        Assert.Contains(body, one);
    }

    [Fact]
    public void ReadNotesOffsetPagesThroughTheRest()
    {
        string big = new('b', 3_500);
        for (int i = 0; i < 6; i++)
        {
            Tools().Note($"s{i}", big);
        }

        string page = Tools().ReadNotes(offset: 4);
        Assert.Contains("## s4", page);
    }

    [Fact]
    public void ReadAnnotationShowsTheWholeRecordAndMentioningNotes()
    {
        Tools().Annotate($"0x{Callee:X}", name: "ClearString", comment: "clears the decoded buffer");
        Tools().AnnotateLocal($"0x{Callee:X}", "arg_0", "buffer");
        Tools().Note("string-xor", "ClearString is called after the xor pass");

        string record = Tools().ReadAnnotation($"0x{Callee:X}");

        Assert.Contains("ClearString", record);
        Assert.Contains("clears the decoded buffer", record);
        Assert.Contains("arg_0=buffer", record);
        Assert.Contains("string-xor", record);   // the note that names it
    }

    [Fact]
    public void ReadAnnotationOnAMidFunctionAddressFallsBackToItsFunction()
    {
        Tools().Annotate($"0x{Entry:X}", name: "TheCaller");

        // An address inside TheCaller with nothing on it: the answer is the function, not "nothing".
        string record = Tools().ReadAnnotation($"0x{Entry + 2:X}");
        Assert.Contains("TheCaller", record);
    }

    [Fact]
    public void ListAnnotationsFiltersByQuery()
    {
        Tools().Annotate($"0x{Entry:X}", name: "ParseHeader", comment: "reads the magic");
        Tools().Annotate($"0x{Callee:X}", name: "ClearString", comment: "wipes the buffer");

        string magic = Tools().ListAnnotations(query: "magic");
        Assert.Contains("ParseHeader", magic);
        Assert.DoesNotContain("ClearString", magic);
    }

    [Fact]
    public void ReadOnlyRefusesNoteButStillReadsIt()
    {
        Tools().Note("overview", "written while writable");

        string refusal = Tools(readOnly: true).Note("overview", "should not take");
        Assert.Contains("read-only", refusal);
        Assert.Equal("written while writable", _notes.Get("overview")!.Text);

        // Reading still works under read-only.
        string read = Tools(readOnly: true).ReadNotes("overview");
        Assert.Contains("written while writable", read);
    }
}
