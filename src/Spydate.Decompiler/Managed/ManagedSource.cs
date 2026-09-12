using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.IL;

namespace Spydate.Decompiler.Managed;

/// <summary>
/// One line of decompiled C# and the IL it was made from.
///
/// <paramref name="Offset"/> is where in <paramref name="MethodToken"/>'s body the statement on that
/// line begins. Both are needed: a type's worth of C# is many methods, and an offset without the
/// method it is into names nothing.
/// </summary>
public readonly record struct SourceLine(int Line, uint MethodToken, int Offset);

/// <summary>
/// One statement's IL, from its first instruction to the first of the next.
///
/// This is the unit a debugger moves in and the unit a breakpoint goes at. The boundaries are the
/// decompiler's, not a guess made from the bytes: a statement is whatever it decided to write on one
/// line, and only it knows that the ternary in the middle of a `for` header is part of the same
/// statement as the assignment before it.
/// </summary>
public readonly record struct SourceStatement(uint MethodToken, int From, int To)
{
    /// <summary>Whether an offset in a given method falls inside this statement.</summary>
    public bool Covers(uint token, int offset) => token == MethodToken && offset >= From && offset < To;
}

/// <summary>
/// Decompiled C# with the IL behind each line of it.
///
/// The point of the pairing is that a line of C# here is not a line of anybody's source file. It was
/// invented by the decompiler from the IL a moment ago, so the only thing that can say what it is
/// about is the decompiler itself — a PDB, if there even is one, maps IL offsets onto lines of a
/// source file this program has never seen.
/// </summary>
public sealed record ManagedSource(string Text, IReadOnlyList<SourceLine> Lines, IReadOnlyList<SourceStatement> Statements)
{
    public static ManagedSource Empty { get; } = new(string.Empty, Array.Empty<SourceLine>(), Array.Empty<SourceStatement>());
}

/// <summary>
/// Pairing decompiled C# with the IL each line came from.
///
/// Half of ILSpy's <c>CreateSequencePoints</c> works from outside the ILSpy application and half
/// does not. Each point carries an IL range and a line number; the ranges are computed from the IL
/// and are right, and the lines are all zero, because they are read from node positions that a
/// decompiled tree does not have — nothing parsed it from a file, and whatever fills them in lives
/// in the app rather than the package.
///
/// So each half comes from where it is sound. The ranges are asked for directly, and they are the
/// statements: where one begins, where it ends, which method it is in. The lines come from watching
/// the text being written — ILSpy annotates every syntax node with the IL it was made from, and a
/// token writer that knows its own line can record them as it goes. Then each line is moved to the
/// start of the statement it landed in, which is the offset a breakpoint can actually bind at.
/// </summary>
internal static class SequencePoints
{
    /// <summary>Writes the tree and pairs each line with the IL the statement on it came from.</summary>
    internal static ManagedSource Of(CSharpDecompiler decompiler, SyntaxTree tree, ICSharpCode.Decompiler.DecompilerSettings settings)
    {
        var output = new StringWriter();
        var recorder = new Recorder(output);
        tree.AcceptVisitor(new CSharpOutputVisitor(recorder, settings.CSharpFormattingOptions));

        var statements = Statements(decompiler, tree);

        // Each line moved to the start of the statement it is in. What the recorder caught is the
        // offset of an expression — routinely one instruction past the statement, after the ldarg
        // that pushed the receiver — and the runtime binds a breakpoint only at a statement. A line
        // whose IL is in a hidden range is dropped: those are the compiler's own, a loop's jump back
        // or a switch's dispatch, and they are not places a reader means to stop at.
        var lines = new List<SourceLine>();
        foreach (var line in recorder.Lines.Values.OrderBy(l => l.Line))
        {
            if (Containing(statements, line.MethodToken, line.Offset) is { } statement)
            {
                lines.Add(line with { Offset = statement.From });
            }
        }

        return new ManagedSource(output.ToString(), lines, statements);
    }

    /// <summary>
    /// Where each statement of a decompiled tree begins and ends.
    ///
    /// ILSpy's own sequence points, which is the one part of <c>CreateSequencePoints</c> that works
    /// outside the ILSpy application: the ranges are computed from the IL and are right, and only the
    /// line numbers are missing because a decompiled tree has no positions until something writes it.
    ///
    /// Worth having rather than working out from the bytes. A statement boundary is where the
    /// evaluation stack is empty, and reading that off a linear walk of the IL is wrong the first
    /// time a ternary appears: the instruction after the <c>br</c> is a branch target, not a
    /// continuation, so the depth carried into it belongs to a path that jumped over it. One
    /// `(a ? b : null)` left every later offset in `McpOptions.Parse` counted one too high, which
    /// read as a single 0x100-byte statement covering the rest of the method.
    /// </summary>
    internal static IReadOnlyList<SourceStatement> Statements(CSharpDecompiler decompiler, SyntaxTree tree)
    {
        var found = new List<SourceStatement>();

        foreach (var (function, points) in decompiler.CreateSequencePoints(tree))
        {
            // The state machine's MoveNext when there is one: an async method's own body is a stub
            // that starts the machine, and these offsets are into the code the reader is looking at.
            if ((function.MoveNextMethod ?? function.Method) is not { } method || method.MetadataToken.IsNil)
            {
                continue;
            }

            uint token = (uint)MetadataTokens.GetToken(method.MetadataToken);
            foreach (var point in points)
            {
                if (!point.IsHidden && point.EndOffset > point.Offset)
                {
                    found.Add(new SourceStatement(token, point.Offset, point.EndOffset));
                }
            }
        }

        found.Sort((a, b) => a.MethodToken != b.MethodToken
            ? a.MethodToken.CompareTo(b.MethodToken)
            : a.From.CompareTo(b.From));

        return found;
    }

    /// <summary>The statement an offset is inside, or null when it is in none of them.</summary>
    internal static SourceStatement? Containing(IReadOnlyList<SourceStatement> statements, uint token, int offset)
    {
        foreach (var statement in statements)
        {
            if (statement.Covers(token, offset))
            {
                return statement;
            }
        }

        return null;
    }

    /// <summary>
    /// A writer that remembers which IL each line of its output came from.
    ///
    /// <c>StartNode</c> is called immediately before a node's first token is written, so the writer's
    /// own line counter is the line that node begins on. The rest is the rule ILSpy uses for the same
    /// job: an instruction is usable if it has an IL range of its own and is not one of the synthetic
    /// containers, and the method it belongs to is the nearest enclosing function.
    /// </summary>
    private sealed class Recorder : TextWriterTokenWriter
    {
        internal Recorder(TextWriter writer) : base(writer)
        {
        }

        internal Dictionary<int, SourceLine> Lines { get; } = new();

        public override void StartNode(AstNode node)
        {
            base.StartNode(node);

            foreach (var instruction in node.Annotations.OfType<ILInstruction>())
            {
                if (instruction.ILRangeIsEmpty || instruction.Parent is null
                    || instruction is Block or BlockContainer)
                {
                    continue;
                }

                if (instruction.Ancestors.OfType<ILFunction>().FirstOrDefault() is not { } function)
                {
                    continue;
                }

                // The state machine's MoveNext, when there is one, and this is not a detail.
                // An async method's own body is a stub that starts the machine and returns; the
                // code the reader is looking at lives in MoveNext, and that is where these offsets
                // are. Naming the method the reader sees puts the breakpoint at some unrelated
                // place inside the stub — the runtime refused it outright here, which was lucky,
                // because the alternative is a breakpoint that sits somewhere wrong and fires.
                if ((function.MoveNextMethod ?? function.Method) is not { } method || method.MetadataToken.IsNil)
                {
                    continue;
                }

                Claim(Location.Line, (uint)MetadataTokens.GetToken(method.MetadataToken), instruction.StartILOffset);
            }
        }

        /// <summary>
        /// Keeps the earliest instruction on each line.
        ///
        /// Several nodes start on one line — a call inside an if, an argument inside a call — and
        /// each carries its own offset. Breaking on a line means breaking when control first reaches
        /// it, so the lowest offset is the one that means what the reader means. A second method on
        /// the same line, which a lambda can do, keeps the first: a line has one breakpoint.
        /// </summary>
        private void Claim(int line, uint token, int offset)
        {
            if (line <= 0)
            {
                return;
            }

            if (!Lines.TryGetValue(line, out var already))
            {
                Lines[line] = new SourceLine(line, token, offset);
            }
            else if (already.MethodToken == token && offset < already.Offset)
            {
                Lines[line] = already with { Offset = offset };
            }
        }
    }

    /// <summary>
    /// Puts the file address of each line's IL in a trailing comment.
    ///
    /// The same thing pseudo-C does, and for the same reason: everything downstream of the text —
    /// the breakpoint margin, the caret's address, the execution arrow — reads the address back out
    /// of the line rather than being told it separately. Writing it here means the C# view gets all
    /// of that without a single one of them learning what a sequence point is.
    /// </summary>
    internal static string Addressed(ManagedSource source, ManagedBodies bodies, ulong imageBase)
    {
        if (source.Lines.Count == 0)
        {
            return source.Text;
        }

        var addresses = new Dictionary<int, ulong>();
        var taken = new HashSet<ulong>();

        foreach (var line in source.Lines)
        {
            var handle = MetadataTokens.MethodDefinitionHandle((int)(line.MethodToken & 0x00FFFFFF));
            if (bodies.Of(handle) is not { } body || line.Offset < 0 || line.Offset >= body.Il.Length)
            {
                continue;
            }

            // At the start of an instruction, not merely inside the body. An offset that lands in
            // the middle of one names a byte the runtime will not break on and a patch must not
            // touch, and an offset from the wrong method usually looks exactly like this. Silence
            // is the right answer: the line carries no address and so cannot be broken on.
            if (!Il.TryCovering(body.Il, line.Offset, out var covering) || covering.Offset != line.Offset)
            {
                continue;
            }

            ulong va = imageBase + body.RvaOf(line.Offset);

            // One line per place. A switch's case labels are all attributed to the switch
            // instruction — nothing runs when a label is reached — and several expressions on
            // different lines can share a statement, so eighteen lines came back carrying one
            // address and a click beside any of them set a breakpoint on the `switch` several lines
            // above. The first line to claim a place keeps it; for the rest there is nowhere the
            // program can be, and saying so by writing nothing is the honest answer.
            if (!taken.Add(va))
            {
                continue;
            }

            addresses[line.Line] = va;
        }

        var sb = new StringBuilder();
        var text = source.Text.Split('\n');
        for (int i = 0; i < text.Length; i++)
        {
            string line = text[i].TrimEnd('\r');
            if (addresses.TryGetValue(i + 1, out ulong va) && line.TrimEnd().Length > 0)
            {
                // Padded so the comments form a column rather than following the code around. The
                // width is counted as the editor draws it, not in characters: ILSpy indents with
                // tabs, so measuring them as one column each puts every nested line's comment in a
                // different place.
                sb.Append(line)
                  .Append(' ', Math.Max(1, CommentColumn - Width(line)))
                  .Append("// ")
                  .Append(va.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(line);
            }

            if (i < text.Length - 1)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>How wide a line is once tabs are expanded, which is what the reader sees.</summary>
    private static int Width(string line)
    {
        int width = 0;
        foreach (char c in line)
        {
            width = c == '\t' ? (width / TabStop + 1) * TabStop : width + 1;
        }

        return width;
    }

    /// <summary>The editor's tab stop.</summary>
    private const int TabStop = 4;

    /// <summary>Where the address comments line up. Wide enough for ordinary statements.</summary>
    private const int CommentColumn = 72;
}
