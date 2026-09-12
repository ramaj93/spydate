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
/// Decompiled C# with the IL behind each line of it.
///
/// The point of the pairing is that a line of C# here is not a line of anybody's source file. It was
/// invented by the decompiler from the IL a moment ago, so the only thing that can say what it is
/// about is the decompiler itself — a PDB, if there even is one, maps IL offsets onto lines of a
/// source file this program has never seen.
/// </summary>
public sealed record ManagedSource(string Text, IReadOnlyList<SourceLine> Lines)
{
    public static ManagedSource Empty { get; } = new(string.Empty, Array.Empty<SourceLine>());
}

/// <summary>
/// Pairing decompiled C# with the IL each line came from, by watching the text being written.
///
/// ILSpy has <c>CreateSequencePoints</c>, and it does not work from outside the ILSpy application:
/// it reads each node's <c>StartLocation</c>, and a decompiled syntax tree has no locations in it —
/// the nodes were built out of IL, not parsed from a file. Whatever fills those in lives in the app
/// rather than the package, so every point it hands back says line zero. The ranges in them are
/// right; only the lines are missing.
///
/// What is actually needed is simpler than a sequence point anyway. ILSpy annotates each syntax node
/// with the IL instructions it was made from — the same annotations its own builder reads — so a
/// writer that knows which line it is on can record them as it goes. That is this: one pass, no
/// second guess at where a node ended up in text, and it answers the only question the debugger
/// asks, which is what IL offset a line of this C# stands for.
/// </summary>
internal static class SequencePoints
{
    /// <summary>Writes the tree and pairs each line with the IL the statement on it came from.</summary>
    internal static ManagedSource Of(CSharpDecompiler decompiler, SyntaxTree tree, ICSharpCode.Decompiler.DecompilerSettings settings)
    {
        var output = new StringWriter();
        var recorder = new Recorder(output);
        tree.AcceptVisitor(new CSharpOutputVisitor(recorder, settings.CSharpFormattingOptions));

        var lines = recorder.Lines.Values.ToList();
        lines.Sort((a, b) => a.Line.CompareTo(b.Line));
        return new ManagedSource(output.ToString(), lines);
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

            if (Statement(body, bodies.Metadata, line.Offset) is not { } offset)
            {
                continue;
            }

            addresses[line.Line] = imageBase + body.RvaOf(offset);
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

    /// <summary>
    /// Where the statement containing an offset begins: the nearest point at or before it at which
    /// the evaluation stack is empty.
    ///
    /// This is the difference between a breakpoint and a breakpoint that is quietly refused. The
    /// runtime will only bind one where the stack is empty, because that is where the JIT's
    /// IL-to-native map has entries, and what ILSpy hands back is the offset of the expression a
    /// line was made from rather than of the statement holding it. So
    /// <c>builder.Logging.ClearProviders();</c> came back as the <c>ldfld</c> that loads
    /// <c>Logging</c> — one instruction after a <c>ldarg.0</c> that had already pushed <c>this</c> —
    /// and the runtime answered with <c>BreakpointSetError</c>, which arrives asynchronously long
    /// after the call that created it said yes. Walking back to the empty stack lands on the
    /// <c>ldarg.0</c>, which is where the statement really starts.
    ///
    /// Null when the walk cannot be trusted that far — an unreadable signature means the depth after
    /// it is unknown, and a guess here is a breakpoint in the middle of an expression.
    /// </summary>
    private static int? Statement(ManagedBody body, MetadataReader metadata, int target)
    {
        var stack = new IlStack(metadata);
        int depth = 0;
        int empty = 0;

        foreach (var instruction in Il.Walk(body.Il))
        {
            if (instruction.Offset > target)
            {
                break;
            }

            if (depth == 0)
            {
                empty = instruction.Offset;
            }

            if (instruction.Offset == target)
            {
                return empty;
            }

            if (stack.Delta(instruction, body.Il, body.Method) is not { } delta)
            {
                return null;
            }

            // A depth that has gone negative means the walk has lost its place — a branch landed
            // somewhere this straight-line reading did not account for — and every offset after it
            // is a guess.
            depth += delta;
            if (depth < 0)
            {
                return null;
            }
        }

        return null;
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
