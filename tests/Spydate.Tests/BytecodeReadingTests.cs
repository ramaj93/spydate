using Spydate.Core.PE;
using Spydate.Core.Readings;
using Spydate.Decompiler.Managed;
using Spydate.Mcp;
using Spydate.Mcp.Session;
using Spydate.Mcp.Tools;

namespace Spydate.Tests;

/// <summary>
/// The seam between "a bytecode reading" and ".NET". The .NET reading seen through
/// <see cref="IBytecodeReading"/> must say exactly what the assembly says itself — the browsing tools now read
/// the interface, so a disagreement would be a behaviour change hiding behind a refactor. And a second
/// reading, which is not .NET, must be browsable and readable through the same tools without any of them
/// naming its format: that is what the seam is for.
/// </summary>
public sealed class BytecodeReadingTests
{
    private static string CoreAssembly => typeof(PeImage).Assembly.Location;

    private static readonly Lazy<ManagedAssembly> Core = new(() => ManagedAssembly.Load(CoreAssembly));

    // --- .NET through the interface ---------------------------------------

    [Fact]
    public void DotNetSeenThroughTheSeamSaysWhatTheAssemblySays()
    {
        var assembly = Core.Value;
        IBytecodeReading reading = new DotNetReading(assembly);

        Assert.Equal(BytecodeKind.DotNet, reading.Kind);
        Assert.Equal(assembly.FullName, reading.FullName);
        Assert.Equal(assembly.TargetFramework, reading.Platform);
        Assert.Equal(assembly.RuntimeVersion, reading.FormatVersion);
        Assert.Equal(assembly.AssemblyReferences, reading.Requires);
        Assert.Equal(["csharp", "il"], reading.Views);
        Assert.Equal("assembly", reading.Noun);

        Assert.Equal(assembly.Namespaces.Count, reading.Namespaces.Count);
        for (int i = 0; i < assembly.Namespaces.Count; i++)
        {
            Assert.Equal(assembly.Namespaces[i].Name, reading.Namespaces[i].Name);
            Assert.Equal(assembly.Namespaces[i].Types.Select(t => t.FullName), reading.Namespaces[i].Types.Select(t => t.FullName));
        }
    }

    [Fact]
    public void KindsNamesAndMembersAreTheAssemblysOwn()
    {
        foreach (var type in Core.Value.Namespaces.SelectMany(n => n.Types))
        {
            IBytecodeType seen = type;

            // The display word is the one the explorer and find_symbol always printed.
            Assert.Equal(type.Kind.ToString().ToLowerInvariant(), seen.KindName);
            Assert.Contains(type.Definition.ReflectionName, seen.OtherNames);
            Assert.Equal(type.Members.Count, seen.Members.Count);
            for (int i = 0; i < type.Members.Count; i++)
            {
                Assert.Same(type.Members[i], seen.Members[i]);
                Assert.Equal(type.Members[i].Kind.ToString(), seen.Members[i].Kind.ToString());
            }
        }
    }

    [Fact]
    public void RenderingIsTheDecompilersOwnOutput()
    {
        var assembly = Core.Value;
        var reading = new DotNetReading(assembly);
        var type = assembly.Namespaces.SelectMany(n => n.Types).First(t => t.Name == "SpanReader");
        var method = type.Members.First(m => m.Kind == ManagedMemberKind.Method);

        Assert.Equal(assembly.Decompiler.DecompileType(type), reading.Render(type, null, "csharp"));
        Assert.Equal(assembly.Decompiler.DisassembleMember(method), reading.Render(type, method, "il"));
        Assert.Throws<ArgumentException>(() => reading.Render(type, null, "bytecode"));
    }

    [Fact]
    public void TheIndexResolvesEveryNameItAlwaysDid()
    {
        var index = new BytecodeIndex(new DotNetReading(Core.Value));

        Assert.True(BytecodeTargets.Resolve(index, "Spydate.Core.PE.PeImage").Found);
        Assert.True(BytecodeTargets.Resolve(index, "PeImage").Found);
        Assert.Equal("Load", BytecodeTargets.Resolve(index, "Spydate.Core.PE.PeImage::Load").Member?.Name);

        // The metadata's form, with + for nesting, reaches the same type as the C# form.
        var nested = index.Types.First(t => t.OtherNames[0].Contains('+', StringComparison.Ordinal));
        Assert.Same(nested, BytecodeTargets.Resolve(index, nested.OtherNames[0]).Type);
        Assert.Contains("in this assembly", BytecodeTargets.Resolve(index, "Spydate.Core.PE.PeImagg::Load").Problem, StringComparison.Ordinal);
    }

    // --- a second reading --------------------------------------------------

    /// <summary>A small JVM-shaped reading held in memory: two packages, a class with a nested one, an entry point.</summary>
    private sealed class JvmLike : IBytecodeReading
    {
        public static readonly Member Greet = new("greet", "greet(String) : String", BytecodeMemberKind.Method);
        public static readonly Member Main = new("main", "main(String[]) : void", BytecodeMemberKind.Method);
        public static readonly Member Ctor = new("<init>", "Greeter()", BytecodeMemberKind.Constructor);
        public static readonly Member Name = new("name", "name : String", BytecodeMemberKind.Field);

        public static readonly TypeRow Inner = new("Inner", "com.example.Greeter.Inner", ["com/example/Greeter$Inner"], BytecodeTypeKind.Class, [], []);
        public static readonly TypeRow Greeter = new("Greeter", "com.example.Greeter", ["com/example/Greeter"], BytecodeTypeKind.Class, [Inner], [Ctor, Greet, Main, Name]);
        public static readonly TypeRow Shape = new("Shape", "com.example.shapes.Shape", ["com/example/shapes/Shape"], BytecodeTypeKind.Interface, [], []);

        public BytecodeKind Kind => BytecodeKind.Jvm;

        string IBytecodeReading.Name => "greeter.jar";

        public string FullName => "greeter.jar";

        public string Platform => "Java 17";

        public string FormatVersion => "61.0";

        public string Noun => "archive";

        public IReadOnlyList<IBytecodeNamespace> Namespaces { get; } =
        [
            new Package("com.example", [Greeter]),
            new Package("com.example.shapes", [Shape]),
        ];

        public IReadOnlyList<string> Requires => ["commons-lang3-3.14.jar"];

        public IBytecodeMember? EntryPoint => Main;

        public IReadOnlyList<string> Views => ["bytecode"];

        public string Render(IBytecodeType type, IBytecodeMember? member, string view, CancellationToken cancellationToken = default)
            => view == "bytecode"
                ? member is null ? $"class {type.FullName}\n  ...\n" : $"{member.Signature}\n  0: aload_1\n  1: areturn\n"
                : throw new ArgumentException($"not a view: {view}", nameof(view));

        internal sealed record Member(string Name, string Signature, BytecodeMemberKind Kind) : IBytecodeMember;

        internal sealed record TypeRow(string Name, string FullName, IReadOnlyList<string> OtherNames, BytecodeTypeKind Kind, IReadOnlyList<IBytecodeType> NestedTypes, IReadOnlyList<IBytecodeMember> Members) : IBytecodeType
        {
            public string KindName => Kind.ToString().ToLowerInvariant();
        }

        private sealed record Package(string Name, IReadOnlyList<IBytecodeType> Types) : IBytecodeNamespace
        {
            public string DisplayName => Name;
        }
    }

    /// <summary>A session whose file has only this reading: no native analysis, no CLR header.</summary>
    private static SessionStore StoreWithJvmReading()
    {
        var store = new SessionStore();
        store.Set(new BinarySession("greeter.jar", SyntheticPe.WithSectionData(new byte[0x40]), null, null, DiscoveryState.None, bytecode: new JvmLike()));
        return store;
    }

    [Fact]
    public void ASecondReadingIsTheProgramAndNotDotNet()
    {
        var session = StoreWithJvmReading().Current!;

        Assert.True(session.BytecodeIsTheProgram);
        Assert.Null(session.Managed);
        Assert.NotNull(session.BytecodeIndex);
    }

    [Fact]
    public void FindSymbolSearchesASecondReading()
    {
        string text = new NavigationTools(StoreWithJvmReading()).FindSymbol("greet");

        Assert.Contains("com.example.Greeter::greet(String) : String", text, StringComparison.Ordinal);
        Assert.Contains("method", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadFunctionRendersASecondReadingInItsOwnView()
    {
        var code = new CodeTools(StoreWithJvmReading());

        string member = code.ReadFunction("com.example.Greeter::greet");
        Assert.Contains("com.example.Greeter::greet(String) : String   method", member, StringComparison.Ordinal);
        Assert.Contains("0: aload_1", member, StringComparison.Ordinal);

        // Its internal name resolves to the same type, the way a .NET metadata name does.
        Assert.Contains("class com.example.Greeter", code.ReadFunction("com/example/Greeter"), StringComparison.Ordinal);

        // Asking for a view it does not have names the ones it does.
        Assert.Contains("view=\"bytecode\"", code.ReadFunction("com.example.Greeter", view: "csharp"), StringComparison.Ordinal);
        Assert.Contains("view=\"bytecode\"", code.ReadFunction("com.example.Greeter", view: "asm"), StringComparison.Ordinal);
    }

    [Fact]
    public void AMissSaysWhatTheReadingIsCalled()
    {
        string text = new CodeTools(StoreWithJvmReading()).ReadFunction("com.example.Greter");

        Assert.Contains("in this archive", text, StringComparison.Ordinal);
        Assert.Contains("com.example.Greeter", text, StringComparison.Ordinal);   // the near miss is offered
    }

    [Fact]
    public void TheOverviewDescribesASecondReading()
    {
        string text = new SessionTools(StoreWithJvmReading(), McpOptions.Default).GetOverview();

        Assert.Contains("greeter.jar, Java 17, format 61.0", text, StringComparison.Ordinal);
        Assert.Contains("3 in 2 namespaces, 4 members", text, StringComparison.Ordinal);   // the nested type counts
        Assert.Contains("main(String[]) : void", text, StringComparison.Ordinal);
        Assert.Contains("commons-lang3-3.14.jar", text, StringComparison.Ordinal);
        Assert.Contains("view=\"bytecode\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void XrefsSaysPlainlyWhatASecondReadingCannotAnswerYet()
    {
        string text = new NavigationTools(StoreWithJvmReading()).Xrefs("com.example.Greeter::greet");

        Assert.Contains("Jvm reading", text, StringComparison.Ordinal);
        Assert.Contains(".NET IL only", text, StringComparison.Ordinal);
    }
}
