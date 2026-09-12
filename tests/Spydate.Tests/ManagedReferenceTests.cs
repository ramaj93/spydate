using System.Reflection.Metadata;
using Spydate.Core.PE;
using Spydate.Decompiler.Managed;

namespace Spydate.Tests;

/// <summary>
/// Reading who refers to what out of the IL.
///
/// Tested against Spydate's own <c>Spydate.Core.dll</c>, which is beside the test assembly whenever
/// the suite runs, and is a fair sample: generics, nested types, iterators, records and several
/// thousand call sites.
/// </summary>
public class ManagedReferenceTests
{
    private static readonly Lazy<ManagedAssembly> Loaded =
        new(() => ManagedAssembly.Load(typeof(PeImage).Assembly.Location), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<ManagedReferences> Scanned =
        new(() => ManagedReferences.Build(Loaded.Value), LazyThreadSafetyMode.ExecutionAndPublication);

    private static ManagedAssembly Assembly => Loaded.Value;

    private static ManagedReferences References => Scanned.Value;

    private static ManagedMember Member(string type, string name)
    {
        var found = Assembly.Namespaces
            .SelectMany(n => n.Types)
            .First(t => t.FullName == type);

        return found.Members.First(m => m.Name == name);
    }

    // ------------------------------------------------------------------
    // The walk itself
    // ------------------------------------------------------------------

    [Fact]
    public void EveryBodyInACleanAssemblyIsReadable()
    {
        // The canary for the opcode walk. Instruction boundaries are found by walking, so one
        // operand length that is wrong throws off every instruction after it in that method — and
        // the symptom is a body that stops decoding early, not an exception. A build of our own
        // making has no excuse for a single unreadable body.
        Assert.Equal(0, References.Unreadable);
        Assert.True(References.Count > 1000, $"only {References.Count} references found");
    }

    [Fact]
    public void AMethodsOwnCallsAreFoundInTheOrderItMakesThem()
    {
        var load = Member("Spydate.Core.PE.PeImage", "Load");
        var edges = References.From((MethodDefinitionHandle)load.Handle);

        var names = edges.Select(e => References.NameOf(e.Target)).ToList();

        // PeImage.Load reads the file, wraps the failure, and constructs the image. All three are
        // in its IL and nothing else has to be true for this to be a fair check.
        Assert.Contains(names, n => n.Contains("File::ReadAllBytes", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("PeParseException", StringComparison.Ordinal));
        Assert.Equal(edges.OrderBy(e => e.Site.Offset).Select(e => e.Site.Offset), edges.Select(e => e.Site.Offset));
    }

    [Fact]
    public void ACalledMethodKnowsWhoCallsIt()
    {
        var parse = Member("Spydate.Core.PE.PeImage", "Load");
        var sites = References.To(parse.Handle);

        // Load is called from inside the assembly's own tests-facing surface; whether or not it is,
        // the index must at least be symmetric with what the forward walk recorded.
        foreach (var site in sites)
        {
            Assert.Contains(
                References.From(site.From),
                e => e.Site.Offset == site.Offset && e.Target == parse.Handle);
        }
    }

    [Fact]
    public void EverySiteIsSymmetricBetweenTheTwoDirections()
    {
        // "To" and "From" are two views of one walk, and a bug that filled only one of them would
        // look like a real answer from whichever side was asked first.
        int checked_ = 0;
        foreach (var type in Assembly.Namespaces.SelectMany(n => n.Types).Take(40))
        {
            foreach (var member in type.Members.Where(m => m.Handle.Kind == HandleKind.MethodDefinition))
            {
                foreach (var (target, site) in References.From((MethodDefinitionHandle)member.Handle))
                {
                    Assert.Contains(References.To(target), s => s == site);
                    checked_++;
                }
            }
        }

        Assert.True(checked_ > 100, $"only {checked_} edges were checked");
    }

    // ------------------------------------------------------------------
    // Strings
    // ------------------------------------------------------------------

    [Fact]
    public void LiteralsAreFoundWithTheCodeThatLoadsThem()
    {
        var found = References.Strings.FirstOrDefault(s => s.Text.StartsWith("Not an MSF 7.00", StringComparison.Ordinal));

        Assert.NotNull(found);
        Assert.NotEmpty(found!.Sites);
        Assert.All(found.Sites, s => Assert.Equal(ManagedRefKind.String, s.Kind));

        string where = References.NameOf(found.Sites[0].From);
        Assert.Contains("MsfFile", where, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameLiteralInTwoPlacesIsOneRowWithTwoSites()
    {
        // The metadata interns literals, so two methods loading the same text load the same token.
        // Counting rows rather than uses would report a string used ten times as ten strings.
        var repeated = References.Strings.Where(s => s.Count > 1).ToList();

        Assert.NotEmpty(repeated);
        Assert.All(repeated, s => Assert.Equal(s.Count, s.Sites.Count));
        Assert.Equal(References.Strings.Select(s => s.Text).Distinct().Count(), References.Strings.Count);
    }

    [Fact]
    public void StringsComeBackMostUsedFirst()
        => Assert.Equal(References.Strings.Select(s => s.Count).OrderByDescending(c => c), References.Strings.Select(s => s.Count));

    // ------------------------------------------------------------------
    // What it uses from elsewhere
    // ------------------------------------------------------------------

    [Fact]
    public void WhatItCallsInOtherAssembliesIsItsRealImportTable()
    {
        // A .NET assembly's import directory holds one entry, for the loader. What it actually uses
        // is in the MemberRef table and is only visible by reading the code.
        var read = References.Import("System.IO.File::ReadAllBytes");

        Assert.NotNull(read);
        Assert.NotEmpty(read!.Sites);
        Assert.Contains("System", read.Assembly, StringComparison.Ordinal);
        Assert.Equal("System.IO.File", read.Type);
    }

    [Fact]
    public void AnImportResolvesUnderTheNameTheSourceWouldUse()
    {
        // Source says File.ReadAllBytes, because a using directive took the namespace off. An agent
        // reading decompiled C# has only that form to hand.
        Assert.Equal(
            References.Import("System.IO.File::ReadAllBytes")?.FullName,
            References.Import("File::ReadAllBytes")?.FullName);
    }

    [Fact]
    public void ImportsAreGroupedByTheAssemblyTheyComeFrom()
    {
        var named = References.Imports.Where(i => i.Assembly.Length > 0).ToList();

        Assert.NotEmpty(named);
        Assert.Contains(named, i => i.Assembly == "System.Runtime");
        Assert.Equal(References.Imports.Select(i => i.Count).OrderByDescending(c => c), References.Imports.Select(i => i.Count));
    }

    [Fact]
    public void AGenericCallIsCountedAgainstTheMethodAndNotItsInstantiation()
    {
        // A member of a generic type hangs off a TypeSpec, whose signature has to be decoded to
        // reach the name. Unread, every one of them collapsed into a single "(generic type)" row:
        // List.Add and HashSet.Add became the same fictional member with ninety calls against it.
        var list = References.Import("System.Collections.Generic.List::Add");

        Assert.NotNull(list);
        Assert.True(list!.Count > 1, $"only {list.Count} calls to List.Add");
        Assert.Equal("System.Collections", list.Assembly);
        Assert.DoesNotContain(References.Imports, i => i.Type.Contains("generic type", StringComparison.Ordinal));
    }

    [Fact]
    public void AlmostEveryImportKnowsWhichAssemblyItIsFrom()
    {
        // The assembly is found by walking a reference's resolution scope, and a member of a generic
        // type hangs off a TypeSpec rather than a TypeRef — which left every List, Span and Func
        // call with a blank column until the spec was followed through to its head.
        int named = References.Imports.Count(i => i.Assembly.Length > 0);

        Assert.True(named > References.Imports.Count * 9 / 10,
            $"only {named} of {References.Imports.Count} imports resolved to an assembly");
    }
}
