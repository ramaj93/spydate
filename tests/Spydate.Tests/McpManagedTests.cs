using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Spydate.Core.PE;
using Spydate.Decompiler.Managed;
using Spydate.Mcp;
using Spydate.Mcp.Session;
using Spydate.Mcp.Tools;

namespace Spydate.Tests;

/// <summary>
/// The tools reading a .NET assembly.
///
/// The binary under test is Spydate's own <c>Spydate.Core.dll</c>, which is on disk beside the test
/// assembly whenever the suite runs at all — so unlike the native tests there is nothing here to
/// skip. It is also a fair sample: hundreds of types, nested ones, overloads, records and generics.
/// </summary>
public class McpManagedTests
{
    private static string CoreAssembly => typeof(PeImage).Assembly.Location;

    private static SessionStore Store()
    {
        var store = new SessionStore();
        store.Set(BinarySession.Open(CoreAssembly, McpOptions.Default));
        return store;
    }

    private static CodeTools Code() => new(Store());

    private static NavigationTools Nav() => new(Store());

    // ------------------------------------------------------------------
    // Orientation
    // ------------------------------------------------------------------

    [Fact]
    public void AnAssemblyIsDescribedAsAnAssemblyAndNotOnlyAsAStub()
    {
        // The native lines are true and useless here: for an IL-only assembly they describe the
        // loader stub. An agent that reads them as the program spends its whole budget on nothing,
        // so the overview has to say which of the two readings is about the program.
        string text = new SessionTools(Store(), McpOptions.Default).GetOverview();

        Assert.Contains("assembly  ", text, StringComparison.Ordinal);
        Assert.Contains("Spydate.Core", text, StringComparison.Ordinal);
        Assert.Contains("IL-only", text, StringComparison.Ordinal);
        Assert.Contains("read_function", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOverviewCountsWhatIsActuallyInThere()
    {
        string text = new SessionTools(Store(), McpOptions.Default).GetOverview();

        string types = text.Split('\n').First(l => l.StartsWith("types", StringComparison.Ordinal));
        Assert.Contains("namespaces", types, StringComparison.Ordinal);
        Assert.Contains("members", types, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------

    [Fact]
    public void ATypeIsReadAsCSharpWithoutBeingAsked()
    {
        // "csharp" is the default for a managed target the way "pseudo_c" is for a native one: an
        // agent that has just been handed a type name should not need to know a second parameter.
        string text = Code().ReadFunction("Spydate.Core.PE.PeParseException");

        Assert.Contains("class PeParseException", text, StringComparison.Ordinal);
        Assert.Contains("Spydate.Core.PE.PeParseException", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberIsReadAsIlWhenIlIsAskedFor()
    {
        string text = Code().ReadFunction("Spydate.Core.PE.PeImage::Load", view: "il");

        Assert.Contains(".method", text, StringComparison.Ordinal);
        Assert.Contains("IL_0000", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameMemberReadsBothWays()
    {
        var tools = Code();
        string cs = tools.ReadFunction("Spydate.Core.PE.PeImage::Load", view: "csharp");
        string il = tools.ReadFunction("Spydate.Core.PE.PeImage::Load", view: "il");

        Assert.Contains("Load", cs, StringComparison.Ordinal);
        Assert.DoesNotContain(".maxstack", cs, StringComparison.Ordinal);
        Assert.Contains(".maxstack", il, StringComparison.Ordinal);
    }

    [Fact]
    public void ANamespaceQualifiedMemberResolvesWithADotAsWellAsWithColons()
    {
        // C# writes Type.Member and this server's own rows write Type::Member. An agent will use
        // whichever it last saw, and both have to land on the same thing.
        var tools = Code();
        string colons = tools.ReadFunction("Spydate.Core.PE.PeImage::Load");
        string dot = tools.ReadFunction("Spydate.Core.PE.PeImage.Load");

        Assert.Equal(colons, dot);
    }

    [Fact]
    public void APagedReadNamesAContinuationThatResolvesBackToTheSameThing()
    {
        // The continuation is built from the resolved member, not from what the caller typed. If it
        // did not round-trip, paging through a long method would stop after its first page - and
        // would do it by returning something plausible about a different overload.
        var tools = Code();
        string first = tools.ReadFunction("Spydate.Core.PE.PeImage", maxLines: 20);

        int mark = first.IndexOf("read_function(target=\"", StringComparison.Ordinal);
        Assert.True(mark >= 0, first);
        string rest = first[(mark + "read_function(target=\"".Length)..];
        string quoted = rest[..rest.IndexOf('"', StringComparison.Ordinal)];

        string second = tools.ReadFunction(quoted, maxLines: 20, offset: 20);
        Assert.Contains("lines 21-40", second, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Saying no
    // ------------------------------------------------------------------

    [Fact]
    public void AnOverloadedNameIsRefusedWithTheOverloadsListed()
    {
        var index = Store().Current!.ManagedIndex!;
        var overloaded = index.Types
            .SelectMany(t => t.Members.GroupBy(m => m.Name).Where(g => g.Count() > 1).Select(g => (Type: t, g.First().Name)))
            .FirstOrDefault();

        Assert.False(overloaded == default, "Spydate.Core has no overloaded member to test with");

        string text = Code().ReadFunction($"{overloaded.Type.FullName}::{overloaded.Name}");

        // Picking one silently is the failure that cannot be detected downstream: the answer looks
        // exactly like a correct read of the overload the agent meant.
        Assert.Contains("overloaded", text, StringComparison.Ordinal);
        Assert.Contains(overloaded.Name, text, StringComparison.Ordinal);
    }

    [Fact]
    public void NamingAnOverloadBySignatureReadsThatOne()
    {
        var index = Store().Current!.ManagedIndex!;
        var type = index.Types.First(t => t.Members.GroupBy(m => m.Name).Any(g => g.Count() > 1));
        var member = type.Members.First(m => type.Members.Count(o => o.Name == m.Name) > 1);

        // A constructor's signature has no " : Return" tail, so this is not a search that can assume
        // one — and the resolver must accept both forms for the same reason.
        int tail = member.Signature.LastIndexOf(" : ", StringComparison.Ordinal);
        string signature = tail < 0 ? member.Signature : member.Signature[..tail];

        string text = Code().ReadFunction($"{type.FullName}::{signature}");

        Assert.DoesNotContain("overloaded", text, StringComparison.Ordinal);
        Assert.Contains(signature, text, StringComparison.Ordinal);
    }

    [Fact]
    public void AConstructorResolvesUnderTheNameTheListingsPrintForIt()
    {
        // Metadata calls it ".ctor"; every row this server prints calls it by the type's name,
        // because that is what is written at the call site. Only the second form was ever visible
        // to an agent, so only the second form matters — and it has to work.
        var index = Store().Current!.ManagedIndex!;
        var type = index.Types.First(t => t.Members.Any(m => m.Kind == ManagedMemberKind.Constructor));
        var ctor = type.Members.First(m => m.Kind == ManagedMemberKind.Constructor);

        string text = Code().ReadFunction($"{type.FullName}::{ctor.Signature}");

        Assert.DoesNotContain("has no member", text, StringComparison.Ordinal);
        Assert.Contains(type.FullName, text, StringComparison.Ordinal);
    }

    [Fact]
    public void AskingForANativeViewOfManagedCodeIsRefusedRatherThanSubstituted()
    {
        string text = Code().ReadFunction("Spydate.Core.PE.PeImage", view: "asm");

        Assert.Contains("managed code", text, StringComparison.Ordinal);
        Assert.Contains("csharp", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMisspeltTypeSuggestsWhatWasProbablyMeant()
    {
        string text = Code().ReadFunction("Spydate.Core.PE.PeImagg");

        Assert.Contains("PeImage", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberThatIsNotThereSaysSoAgainstTheTypeItLookedIn()
    {
        string text = Code().ReadFunction("Spydate.Core.PE.PeImage::NoSuchThing");

        Assert.Contains("Spydate.Core.PE.PeImage", text, StringComparison.Ordinal);
        Assert.Contains("NoSuchThing", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Finding
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    // Following references
    // ------------------------------------------------------------------

    [Fact]
    public void WhatAMethodCallsIsReadOutOfItsOwnIl()
    {
        string text = Nav().Xrefs("Spydate.Core.PE.PeImage::Load", direction: "from");

        Assert.Contains("System.IO.File::ReadAllBytes", text, StringComparison.Ordinal);
        Assert.Contains("IL_", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WhoCallsAMemberOfAnotherAssemblyIsAFairQuestion()
    {
        // The managed "who calls CreateFileW". A .NET assembly's import directory holds one entry,
        // so nothing in the PE tables can answer this - only the IL can.
        string text = Nav().Xrefs("System.IO.File::ReadAllBytes");

        Assert.Contains("not defined in this assembly", text, StringComparison.Ordinal);
        Assert.Contains("Spydate.Core.PE.PeImage::Load", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryXrefRowNamesSomethingReadFunctionAccepts()
    {
        // A row that has to be translated before it can be used is a row that gets translated
        // wrongly. The site column is the same form read_function takes, and this proves it rather
        // than asserting it in a comment.
        var code = Code();
        string text = Nav().Xrefs("System.IO.File::ReadAllBytes");

        var sites = text.Split('\n')
            .Where(l => l.Contains("::", StringComparison.Ordinal) && l.StartsWith("Spydate", StringComparison.Ordinal))
            .Select(l => l.Split("  ", StringSplitOptions.RemoveEmptyEntries)[0].Trim())
            .ToList();

        Assert.NotEmpty(sites);
        foreach (string site in sites)
        {
            Assert.DoesNotContain("is not a type or member", code.ReadFunction(site), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AskingATypeWhatItRefersToSaysWhyThatIsNotAQuestion()
    {
        var nav = Nav();

        Assert.Contains("Name a method", nav.Xrefs("Spydate.Core.PE.PeImage", direction: "from"), StringComparison.Ordinal);

        // And the inward direction on a type is empty for a reason worth stating: a type is named by
        // its members' signatures far more often than by an instruction, and signatures are not IL.
        Assert.Contains("members are referred to individually", nav.Xrefs("Spydate.Core.PE.PeImage"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheRealImportListIsWhatTheCodeCallsElsewhere()
    {
        string text = Nav().ListImports(module: "System.IO");

        Assert.Contains("System.IO", text, StringComparison.Ordinal);
        Assert.Contains("xrefs", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0x", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Literals
    // ------------------------------------------------------------------

    [Fact]
    public void LiteralsComeFromTheIlAndNameTheMethodThatLoadsThem()
    {
        string text = new StringTools(Store()).FindStrings("MSF");

        Assert.Contains("MsfFile", text, StringComparison.Ordinal);
        Assert.Contains("Not an MSF 7.00", text, StringComparison.Ordinal);
        Assert.Contains("treat them as data", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFilterThatCannotDoAnythingSaysSoRatherThanLookingApplied()
    {
        // referenced_only has nothing to remove here, because a literal is in the metadata only if
        // an instruction loads it. Ignoring it silently would hand back an unfiltered list that
        // looks filtered, and nothing in the answer would show it.
        var tools = new StringTools(Store());

        Assert.Contains("referenced_only did nothing", tools.FindStrings("MSF", referencedOnly: true), StringComparison.Ordinal);
        Assert.DoesNotContain("referenced_only did nothing", tools.FindStrings("MSF"), StringComparison.Ordinal);
    }

    [Fact]
    public void NoAnswerTellsTheAgentToCallSomethingThatIsNotThere()
    {
        // Every managed answer here ends by naming what to call next, and those suggestions were
        // written against a list_types tool that did not survive the manifest budget. An agent
        // following one gets an error from its own client, not from this server, and has no way to
        // learn what it should have called instead. So the suggestions are checked against the tools
        // that actually exist rather than against what was true when the sentence was written.
        var real = typeof(McpOptions).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods())
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(n => n is not null)
            .ToHashSet(StringComparer.Ordinal);

        var nav = Nav();
        var code = Code();
        string[] answers =
        [
            new SessionTools(Store(), McpOptions.Default).GetOverview(),
            nav.FindSymbol("PeParseException", limit: 6),
            nav.ListFunctions(limit: 3),
            Header(code.ReadFunction("Spydate.Core.PE.PeParseException")),
            Header(code.ReadFunction("Spydate.Core.PE.PeImage::Load", view: "il", maxLines: 4)),
        ];

        foreach (string answer in answers)
        {
            // Only names with an underscore: that is every tool this suggests, and it keeps the
            // check clear of the ordinary parentheses in decompiled text.
            foreach (Match match in Regex.Matches(answer, @"\b([a-z]+_[a-z_]+)\("))
            {
                Assert.Contains(match.Groups[1].Value, real);
            }
        }
    }

    /// <summary>An answer's own lines, without the decompiled body underneath them.</summary>
    private static string Header(string answer)
    {
        int body = answer.IndexOf("--- lines", StringComparison.Ordinal);
        return body < 0 ? answer : answer[..body];
    }

    [Fact]
    public void TheNativeFunctionListSaysThatItIsNotTheProgram()
    {
        // Discovery still runs — a header bit is not something untrusted input gets to be believed
        // about — so the list is real and is junk, and only the label can tell an agent which.
        string text = Nav().ListFunctions(limit: 5);

        Assert.Contains("IL-only", text, StringComparison.Ordinal);
        Assert.Contains("read_function", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FindSymbolSearchesMetadataRatherThanTheStubsSymbolTable()
    {
        string text = Nav().FindSymbol("PeParseException");

        Assert.Contains("Spydate.Core.PE.PeParseException", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0x", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AQueryNamingATypeReturnsItsMembersToo()
    {
        // There is no list_types, so this is how a type's contents are listed. A query that matched
        // only member names would answer "PeImage" with whatever methods happen to be called that.
        string text = Nav().FindSymbol("PeParseException", limit: 50);

        Assert.Contains("::", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRowCarriesEnoughToBeReadBack()
    {
        var nav = Nav();
        var code = Code();
        string listed = nav.FindSymbol("PeParseException", limit: 20);

        foreach (string row in listed.Split('\n').Where(l => l.Contains("::", StringComparison.Ordinal)))
        {
            string name = row[(row.IndexOf("  ", StringComparison.Ordinal) + 2)..].Trim();
            string read = code.ReadFunction(name);
            Assert.DoesNotContain("is not a type or member", read, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AManagedMethodIsAnnotatedByItsNameAtWhereItsIlBegins()
    {
        var store = Store();
        var session = store.Current!;
        var index = session.ManagedIndex!;

        // An unambiguous method with a body — one overload of its name in its type — so it resolves
        // without a signature and has an address to carry the note.
        var (type, method) = index.Types
            .SelectMany(t => t.Members.Where(m => m.Kind == ManagedMemberKind.Method).Select(m => (t, m)))
            .First(tm => tm.t.Members.Count(x => x.Name == tm.m.Name) == 1
                         && session.Bodies!.Of(tm.m.Handle) is not null);

        ulong expected = session.Image.ImageBase + session.Bodies!.Of(method.Handle)!.RvaOf(0);

        // Setting a name echoes the address it landed on, which is where the method's IL begins.
        string result = new AnnotationTools(store, McpOptions.Default)
            .Annotate($"{type.FullName}::{method.Name}", name: "GateChecked", comment: "gate check");

        Assert.Contains($"0x{expected:X}", result, StringComparison.Ordinal);
        Assert.Contains("GateChecked", result, StringComparison.Ordinal);
        Assert.Contains("comment: gate check", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AnnotatingAManagedTypeByNameSaysItHasNoSingleAddress()
    {
        var store = Store();
        string typeName = store.Current!.ManagedIndex!.Types.First(t => t.FullName.Contains('.')).FullName;

        string result = new AnnotationTools(store, McpOptions.Default).Annotate(typeName, comment: "x");

        Assert.Contains("no single address", result, StringComparison.Ordinal);
    }
}
