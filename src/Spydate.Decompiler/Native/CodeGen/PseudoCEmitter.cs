using System.Globalization;
using System.Text;
using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Structuring;

namespace Spydate.Decompiler.Native.CodeGen;

/// <summary>
/// Emits an <see cref="IrFunction"/> as pseudo-C. The body comes from the <see cref="Structurer"/>, so it
/// is written with <c>if</c> / <c>else</c> / loops; the gotos that survive are the edges no structure
/// covered, and only those keep a label.
/// </summary>
public sealed class PseudoCEmitter
{
    private const string Indent = "    ";

    public bool IncludeAddressComments { get; init; } = true;

    public string Emit(IrFunction fn)
    {
        ArgumentNullException.ThrowIfNull(fn);

        var body = Structurer.Structure(fn);
        var labels = CollectLabels(body);
        var sb = new StringBuilder();
        string retType = fn.ReturnsValue ? IrTypes.NameFor(fn.Bitness) : "void";

        sb.Append("// Function ").Append(fn.Name)
          .Append(" @ 0x").Append(fn.EntryVa.ToString("X", CultureInfo.InvariantCulture))
          .Append(" (").Append(fn.Blocks.Count).Append(" blocks)").AppendLine();
        if (labels.Count > 0)
        {
            sb.Append("// ").Append(labels.Count).Append(labels.Count == 1 ? " edge" : " edges")
              .Append(" could not be structured and kept a goto.").AppendLine();
        }

        if (fn.Warnings.Count > 0)
        {
            sb.Append("// ").Append(fn.Warnings.Count).Append(" lifter warning(s); see analysis notes.").AppendLine();
        }

        // Only slots that are still referenced after the passes are declared (consumed pushes and elided
        // register spills leave stale entries in Locals).
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stmt in fn.AllStatements)
        {
            foreach (var e in IrRewriter.Reads(stmt))
            {
                switch (e)
                {
                    case IrLocal l: referenced.Add(l.Name); break;
                    case IrAddressOf { Target: IrLocal l2 }: referenced.Add(l2.Name); break;
                }
            }

            if (IrRewriter.Destination(stmt) is IrLocal dl)
            {
                referenced.Add(dl.Name);
            }
        }

        // Stack slots above the return address are incoming arguments → parameters; the rest are locals.
        var parameters = fn.Locals.Values.Where(l => l.FrameOffset > 0 && l.Name.StartsWith("arg_", StringComparison.Ordinal) && referenced.Contains(l.Name)).OrderBy(l => l.FrameOffset).ToList();
        var locals = fn.Locals.Values.Where(l => !parameters.Contains(l) && l.Name != "return_address" && referenced.Contains(l.Name)).OrderByDescending(l => l.FrameOffset).ToList();

        sb.Append(retType).Append(' ').Append(SanitizeIdentifier(fn.Name)).Append('(');
        sb.Append(parameters.Count == 0 ? "void" : string.Join(", ", parameters.Select(p => $"{IrTypes.NameFor(p.Bits)} {p.Name}")));
        sb.Append(')').AppendLine();
        sb.AppendLine("{");

        foreach (var local in locals)
        {
            sb.Append(Indent).Append(IrTypes.NameFor(local.Bits)).Append(' ').Append(local.Name).Append(';');
            PadTo(sb, 56);
            sb.Append("// [sp").Append(local.FrameOffset >= 0 ? "+" : "-")
              .Append("0x").Append(Math.Abs(local.FrameOffset).ToString("X", CultureInfo.InvariantCulture)).Append(']').AppendLine();
        }

        if (locals.Count > 0)
        {
            sb.AppendLine();
        }

        Write(sb, body, 1, labels);

        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>VAs a goto still targets — the only blocks that need a label.</summary>
    private static HashSet<ulong> CollectLabels(CStmt body)
        => CStmts.Descendants(body).OfType<CGoto>().Where(g => !g.External).Select(g => g.Va).ToHashSet();

    private void Write(StringBuilder sb, CStmt stmt, int depth, HashSet<ulong> labels)
    {
        switch (stmt)
        {
            case CSeq seq:
                foreach (var item in seq.Items)
                {
                    Write(sb, item, depth, labels);
                }

                break;

            case CLabel label when labels.Contains(label.Va):
                // Labels sit one level out, like a case label, so the code they head stays aligned.
                sb.Append(' ', Math.Max(0, (depth - 1) * Indent.Length))
                  .Append("loc_").Append(label.Va.ToString("X", CultureInfo.InvariantCulture)).Append(':').AppendLine();
                break;

            case CLabel:
                break;

            case CRaw raw:
                WriteLine(sb, depth, Format(raw.Statement), raw.Statement);
                break;

            case CGoto g:
                WriteLine(sb, depth, g.External
                    ? $"goto loc_{g.Va:X};   // outside this function"
                    : $"goto loc_{g.Va:X};", null);
                break;

            case CBreak:
                WriteLine(sb, depth, "break;", null);
                break;

            case CContinue:
                WriteLine(sb, depth, "continue;", null);
                break;

            case CIf branch:
                WriteIf(sb, branch, depth, labels);
                break;

            case CLoop loop:
                WriteLoop(sb, loop, depth, labels);
                break;

            case CSwitch dispatch:
                WriteSwitch(sb, dispatch, depth, labels);
                break;
        }
    }

    private void WriteIf(StringBuilder sb, CIf branch, int depth, HashSet<ulong> labels)
    {
        WriteLine(sb, depth, $"if ({IrPrinter.Print(branch.Condition)})", null);
        WriteBlock(sb, branch.Then, depth, labels);

        if (branch.Else is not null)
        {
            WriteElse(sb, branch.Else, depth, labels);
        }
    }

    /// <summary>A lone <c>if</c> in the else arm chains, so a ladder does not march off the right margin.</summary>
    private void WriteElse(StringBuilder sb, CStmt @else, int depth, HashSet<ulong> labels)
    {
        if (Unwrap(@else, labels) is CIf chained)
        {
            WriteLine(sb, depth, $"else if ({IrPrinter.Print(chained.Condition)})", null);
            WriteBlock(sb, chained.Then, depth, labels);
            if (chained.Else is not null)
            {
                WriteElse(sb, chained.Else, depth, labels);
            }

            return;
        }

        WriteLine(sb, depth, "else", null);
        WriteBlock(sb, @else, depth, labels);
    }

    /// <summary>A sequence holding one real statement is that statement, for `else if` chaining.</summary>
    private static CStmt? Unwrap(CStmt stmt, HashSet<ulong> labels)
    {
        if (stmt is not CSeq seq)
        {
            return stmt;
        }

        CStmt? only = null;
        foreach (var item in seq.Items)
        {
            switch (item)
            {
                case CLabel label when !labels.Contains(label.Va):
                case CSeq { Items.Count: 0 }:
                    continue;   // prints nothing
                case CLabel:
                    return null; // a printed label must stay reachable, so keep the braces
            }

            if (only is not null)
            {
                return null;
            }

            only = item;
        }

        return only;
    }

    private void WriteLoop(StringBuilder sb, CLoop loop, int depth, HashSet<ulong> labels)
    {
        switch (loop.Kind)
        {
            case CLoopKind.While:
                WriteLine(sb, depth, $"while ({IrPrinter.Print(loop.Condition!)})", null);
                WriteBlock(sb, loop.Body, depth, labels);
                break;

            case CLoopKind.DoWhile:
                WriteLine(sb, depth, "do", null);
                WriteBlock(sb, loop.Body, depth, labels);
                WriteLine(sb, depth, $"while ({IrPrinter.Print(loop.Condition!)});", null);
                break;

            default:
                WriteLine(sb, depth, "while (true)", null);
                WriteBlock(sb, loop.Body, depth, labels);
                break;
        }
    }

    private void WriteSwitch(StringBuilder sb, CSwitch dispatch, int depth, HashSet<ulong> labels)
    {
        WriteLine(sb, depth, $"switch ({IrPrinter.Print(dispatch.Value)})", null);
        WriteLine(sb, depth, "{", null);
        foreach (var arm in dispatch.Cases)
        {
            foreach (int label in arm.Labels)
            {
                WriteLine(sb, depth + 1, $"case {label.ToString(CultureInfo.InvariantCulture)}:", null);
            }

            Write(sb, arm.Body, depth + 2, labels);
        }

        WriteLine(sb, depth, "}", null);
    }

    private void WriteBlock(StringBuilder sb, CStmt body, int depth, HashSet<ulong> labels)
    {
        WriteLine(sb, depth, "{", null);
        Write(sb, body, depth + 1, labels);
        WriteLine(sb, depth, "}", null);
    }

    private void WriteLine(StringBuilder sb, int depth, string text, IrStmt? source)
    {
        if (text.Length == 0)
        {
            return;
        }

        for (int i = 0; i < depth; i++)
        {
            sb.Append(Indent);
        }

        sb.Append(text);
        if (IncludeAddressComments && source is { Va: not 0 } and not IrComment)
        {
            PadTo(sb, 56);
            sb.Append("// ").Append(source.Va.ToString("X", CultureInfo.InvariantCulture));
        }

        sb.AppendLine();
    }

    private static string Format(IrStmt stmt)
    {
        switch (stmt)
        {
            case IrAssign a:
                return $"{IrPrinter.Print(a.Dst)} = {IrPrinter.Print(a.Src)};";
            case IrStore s:
                return $"*({IrTypes.NameFor(s.Bits)}*){WrapForDeref(s.Address)} = {IrPrinter.Print(s.Value)};";
            case IrCallStmt c:
                return c.Result is null
                    ? $"{IrPrinter.Print(c.Call)};"
                    : $"{IrPrinter.Print(c.Result)} = {IrPrinter.Print(c.Call)};";
            case IrReturn r:
                return r.Value is null ? "return;" : $"return {IrPrinter.Print(r.Value)};";
            case IrGoto g:
                return $"goto loc_{g.TargetVa:X};";
            case IrBranch b:
                return $"if ({IrPrinter.Print(b.Condition)}) goto loc_{b.TargetVa:X};";
            case IrSwitch s:
                return $"switch ({IrPrinter.Print(s.Value)});   // {s.Targets.Count} case(s)";
            case IrLabel l:
                return $"loc_{l.LabelVa:X}:";
            case IrAsm asm:
                return $"__asm {{ {asm.Text} }}";
            case IrComment c:
                return $"// {c.Text}";
            case IrNop:
                return string.Empty;
            default:
                return stmt.ToString() ?? string.Empty;
        }
    }

    private static string WrapForDeref(IrExpr address)
    {
        string text = IrPrinter.Print(address);
        return address is IrReg or IrTemp or IrLocal or IrSymbol or IrConst ? text : $"({text})";
    }

    private static void PadTo(StringBuilder sb, int column)
    {
        int lineStart = sb.Length;
        while (lineStart > 0 && sb[lineStart - 1] != '\n')
        {
            lineStart--;
        }

        int col = sb.Length - lineStart;
        sb.Append(' ', Math.Max(1, column - col));
    }

    private static string SanitizeIdentifier(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char ch in name)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        if (sb.Length == 0 || char.IsDigit(sb[0]))
        {
            sb.Insert(0, '_');
        }

        return sb.ToString();
    }
}
