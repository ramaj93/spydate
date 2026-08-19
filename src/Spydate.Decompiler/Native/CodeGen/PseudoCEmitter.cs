using System.Globalization;
using System.Text;
using Spydate.Decompiler.Native.IR;

namespace Spydate.Decompiler.Native.CodeGen;

/// <summary>Emits an <see cref="IrFunction"/> as goto-based pseudo-C.</summary>
public sealed class PseudoCEmitter
{
    private const string Indent = "    ";

    public bool IncludeAddressComments { get; init; } = true;

    public string Emit(IrFunction fn)
    {
        var sb = new StringBuilder();
        string retType = IrTypes.NameFor(fn.Bitness);

        sb.Append("// Function ").Append(fn.Name)
          .Append(" @ 0x").Append(fn.EntryVa.ToString("X", CultureInfo.InvariantCulture))
          .Append(" (").Append(fn.Blocks.Count).Append(" blocks)").AppendLine();
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
                    case IrAddressOf a: referenced.Add(a.Local.Name); break;
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

        var layout = Layout(fn);
        for (int bi = 0; bi < layout.Count; bi++)
        {
            var block = layout[bi];
            ulong? nextStart = bi + 1 < layout.Count ? layout[bi + 1].StartVa : null;
            ulong? prevStart = bi > 0 ? layout[bi - 1].StartVa : null;

            // A label is needed when the block is an explicit jump target, or when control reaches it from
            // somewhere other than the block printed immediately before it.
            bool needsLabel = fn.LabelTargets.Contains(block.StartVa)
                              || (bi > 0 && block.Predecessors.Any(p => p != prevStart))
                              || (bi > 0 && block.Predecessors.Count == 0);
            if (needsLabel)
            {
                sb.Append("loc_").Append(block.StartVa.ToString("X", CultureInfo.InvariantCulture)).Append(':').AppendLine();
            }

            for (int si = 0; si < block.Statements.Count; si++)
            {
                var stmt = block.Statements[si];
                if (stmt is IrNop)
                {
                    continue;
                }

                // Skip a trailing goto that just falls into the next block.
                if (stmt is IrGoto g && si == block.Statements.Count - 1 && nextStart == g.TargetVa)
                {
                    continue;
                }

                string text = Format(stmt, nextStart);
                if (text.Length == 0)
                {
                    continue;
                }

                sb.Append(Indent).Append(text);
                if (IncludeAddressComments && stmt.Va != 0 && stmt is not IrComment)
                {
                    PadTo(sb, 56);
                    sb.Append("// ").Append(stmt.Va.ToString("X", CultureInfo.InvariantCulture));
                }

                sb.AppendLine();
            }
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Entry block first, then the remaining blocks in address order.</summary>
    private static List<IrBlock> Layout(IrFunction fn)
    {
        var ordered = new List<IrBlock>(fn.Blocks.Count);
        var entry = fn.Blocks.FirstOrDefault(b => b.StartVa == fn.EntryVa);
        if (entry is not null)
        {
            ordered.Add(entry);
        }

        foreach (var b in fn.Blocks.OrderBy(b => b.StartVa))
        {
            if (!ReferenceEquals(b, entry))
            {
                ordered.Add(b);
            }
        }

        return ordered;
    }

    private static string Format(IrStmt stmt, ulong? nextBlockStart)
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
                {
                    // Prefer "if (!cond) goto target" when the target is the fallthrough — reads more naturally.
                    var cond = b.Condition;
                    if (nextBlockStart == b.TargetVa && cond is IrCondition ic)
                    {
                        return $"if ({IrPrinter.Print(ic with { Cc = IrTypes.Invert(ic.Cc) })}) goto loc_{b.FallthroughVa:X};";
                    }

                    return $"if ({IrPrinter.Print(cond)}) goto loc_{b.TargetVa:X};";
                }
            case IrLabel l:
                return $"loc_{l.LabelVa:X}:";
            case IrAsm asm:
                return $"__asm {{ {asm.Text} }}";
            case IrComment c:
                return $"// {c.Text}";
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
