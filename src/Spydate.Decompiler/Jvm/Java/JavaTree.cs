using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Structuring;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>Walking and rebuilding structured Java: every statement kind that holds others, in one place.</summary>
internal static class JavaTree
{
    /// <summary>Statements in order as one statement: nested sequences flattened, a single item unwrapped.</summary>
    public static CStmt Sequence(IEnumerable<CStmt> items)
    {
        var flat = new List<CStmt>();
        foreach (var item in items)
        {
            switch (item)
            {
                case CSeq seq:
                    flat.AddRange(seq.Items);
                    break;
                default:
                    flat.Add(item);
                    break;
            }
        }

        return flat.Count == 1 ? flat[0] : new CSeq(flat);
    }

    /// <summary>The statements a sequence holds, or the statement itself.</summary>
    public static IReadOnlyList<CStmt> Items(CStmt? statement) => statement switch
    {
        null => [],
        CSeq seq => seq.Items,
        _ => [statement],
    };

    /// <summary>True when the statement prints nothing.</summary>
    public static bool IsEmpty(CStmt? statement) => statement switch
    {
        null => true,
        CSeq seq => seq.Items.All(IsEmpty),
        CLabel => true,
        CRaw { Statement: IrNop } => true,
        _ => false,
    };

    /// <summary>The statements directly inside this one.</summary>
    public static IEnumerable<CStmt> Children(CStmt statement)
    {
        switch (statement)
        {
            case CSeq seq:
                return seq.Items;
            case CIf conditional:
                return conditional.Else is null ? [conditional.Then] : [conditional.Then, conditional.Else];
            case CLoop loop:
                return [loop.Body];
            case CSwitch dispatch:
                return dispatch.Cases.Select(c => c.Body);
            case JTry attempt:
                var parts = new List<CStmt> { attempt.Body };
                parts.AddRange(attempt.Catches.Select(c => c.Body));
                if (attempt.Finally is not null)
                {
                    parts.Add(attempt.Finally);
                }

                return parts;
            case CRaw { Statement: JRegion region }:
                return [region.Body];

            // The arms of a switch expression are statements too, inside the expression.
            case CRaw { Statement: var held } when JavaRewrite.Evaluated(held).SelectMany(JavaRewrite.PostOrder).OfType<JSwitchExpr>().ToList() is { Count: > 0 } switches:
                return switches.SelectMany(e => e.Arms.Select(a => (CStmt)a.Body));
            case JBlock block:
                return [block.Body];
            case JLoop loop:
                return [loop.Body];
            case JSwitch dispatch:
                return dispatch.Cases.Select(c => c.Body);
            case JSynchronized locked:
                return [locked.Body];
            default:
                return [];
        }
    }

    /// <summary>Every statement in the tree, parents first, iteratively.</summary>
    public static IEnumerable<CStmt> Descendants(CStmt root)
    {
        var stack = new Stack<CStmt>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in Children(node).Reverse())
            {
                stack.Push(child);
            }
        }
    }

    /// <summary>The tree rebuilt bottom-up: children first, then <paramref name="rewrite"/> on the statement holding them.</summary>
    public static CStmt Rewrite(CStmt statement, Func<CStmt, CStmt> rewrite)
    {
        var rebuilt = statement switch
        {
            CSeq seq => Sequence(seq.Items.Select(i => Rewrite(i, rewrite))),
            CIf conditional => conditional with { Then = Rewrite(conditional.Then, rewrite), Else = conditional.Else is null ? null : Rewrite(conditional.Else, rewrite) },
            CLoop loop => loop with { Body = Rewrite(loop.Body, rewrite) },
            CSwitch dispatch => dispatch with { Cases = dispatch.Cases.Select(c => c with { Body = Rewrite(c.Body, rewrite) }).ToList() },
            JTry attempt => attempt with
            {
                Body = Rewrite(attempt.Body, rewrite),
                Catches = attempt.Catches.Select(c => c with { Body = Rewrite(c.Body, rewrite) }).ToList(),
                Finally = attempt.Finally is null ? null : Rewrite(attempt.Finally, rewrite),
            },
            CRaw { Statement: JRegion region } raw => raw with { Statement = region with { Body = Rewrite(region.Body, rewrite) } },
            JBlock block => block with { Body = Rewrite(block.Body, rewrite) },
            JLoop loop => loop with { Body = Rewrite(loop.Body, rewrite) },
            JSwitch dispatch => dispatch with { Cases = dispatch.Cases.Select(c => c with { Body = Rewrite(c.Body, rewrite) }).ToList() },
            JSynchronized locked => locked with { Body = Rewrite(locked.Body, rewrite) },
            _ => statement,
        };

        return rewrite(rebuilt);
    }

    /// <summary>Whether control can never run on past the statement: it ends in a jump, a return or a throw on every path.</summary>
    public static bool NeverFallsThrough(CStmt statement) => statement switch
    {
        CRaw { Statement: IrReturn or JThrow or JNoMatch } => true,
        CRaw { Statement: JRegion region } => NeverFallsThrough(region.Body),
        CGoto or CBreak or CContinue or JBreak or JContinue or JExit => true,
        CSeq seq => seq.Items.LastOrDefault(i => !IsEmpty(i)) is { } last && NeverFallsThrough(last),
        CIf { Else: { } otherwise } conditional => NeverFallsThrough(conditional.Then) && NeverFallsThrough(otherwise),
        JTry attempt => (attempt.Finally is { } fin && NeverFallsThrough(fin))
                        || (NeverFallsThrough(attempt.Body) && attempt.Catches.All(c => NeverFallsThrough(c.Body))),
        JSynchronized locked => NeverFallsThrough(locked.Body),
        JLoop { Kind: CLoopKind.Forever } loop => !Descendants(loop.Body).Any(d => d is JBreak b && b.Label == loop.Label),

        // A switch whose default an arm still takes, every arm of which returns, throws or jumps out, and nothing breaks out of.
        JSwitch { Patterns: null, DefaultLabel: >= 0 } dispatch => dispatch.Cases.Any(c => c.Labels.Contains(dispatch.DefaultLabel))
            && dispatch.Cases.All(c => NeverFallsThrough(c.Body))
            && !Descendants(dispatch).Any(d => d is JBreak b && b.Label == dispatch.Label),
        JBlock block => NeverFallsThrough(block.Body) && !Descendants(block.Body).Any(d => d is JBreak b && b.Label == block.Label),
        _ => false,
    };

    /// <summary>The tree with locals swapped wherever an expression reads them: in statements, tests, loop headers and locks.</summary>
    public static CStmt ReplaceLocals(CStmt root, Func<JLocal, JExpr?> replace) => MapExpressions(root, e => e is JLocal local ? replace(local) : null);

    /// <summary>The tree with every expression mapped (<see cref="JavaRewrite.Map(IrExpr, Func{IrExpr, IrExpr?})"/>): in statements, tests, loop headers and locks.</summary>
    public static CStmt MapExpressions(CStmt root, Func<IrExpr, IrExpr?> map) => Rewrite(root, statement => statement switch
    {
        CRaw { Statement: JRegion } => statement,
        CRaw raw => raw with { Statement = JavaRewrite.Map(raw.Statement, map) },
        CIf conditional => conditional with { Condition = JavaRewrite.Map(conditional.Condition, map) },
        JLoop loop => loop with
        {
            Condition = loop.Condition is null ? null : JavaRewrite.Map(loop.Condition, map),
            Init = loop.Init is null ? null : JavaRewrite.Map(loop.Init, map),
            Update = loop.Update is null ? null : JavaRewrite.Map(loop.Update, map),
            ForEach = loop.ForEach is { } each ? (each.Variable, JavaRewrite.Map(each.Source, map)) : null,
        },
        JTry { Resources.Count: > 0 } attempt => attempt with { Resources = attempt.Resources.Select(r => (r.Variable, JavaRewrite.Map(r.Init, map))).ToList() },
        JAssert assertion => assertion with { Condition = JavaRewrite.Map(assertion.Condition, map), Message = assertion.Message is null ? null : (JExpr)JavaRewrite.Map(assertion.Message, map) },
        JSwitch dispatch => dispatch with
        {
            Value = JavaRewrite.Map(dispatch.Value, map),
            Patterns = dispatch.Patterns?.ToDictionary(p => p.Key, p => p.Value.Guard is { } guard ? p.Value with { Guard = JavaRewrite.Map(guard, map) } : p.Value),
        },
        JSynchronized locked => locked with { Lock = (JExpr)JavaRewrite.Map(locked.Lock, map) },
        _ => statement,
    });

    /// <summary>The expressions a statement itself holds — not those of the statements inside it — stores' targets included.</summary>
    public static IEnumerable<IrExpr> Expressions(CStmt statement) => statement switch
    {
        CRaw { Statement: IrAssign a } => [a.Dst, a.Src],
        CRaw { Statement: JRegion } => [],
        CRaw { Statement: var s } => JavaRewrite.Evaluated(s),
        CIf i => [i.Condition],
        CLoop { Condition: { } c } => [c],
        CSwitch s => [s.Value],
        JLoop l => new IrExpr?[]
        {
            l.Condition, (l.Init as IrAssign)?.Dst, (l.Init as IrAssign)?.Src, (l.Update as IrAssign)?.Dst, (l.Update as IrAssign)?.Src,
            l.ForEach?.Variable, l.ForEach?.Source,
        }.OfType<IrExpr>(),
        JSwitch s => Guards(s.Patterns).Prepend(s.Value),
        JSynchronized l => [l.Lock],
        JTry t => t.Resources.SelectMany(r => new IrExpr[] { r.Variable, r.Init }),
        JAssert a => a.Message is null ? [a.Condition] : [a.Condition, a.Message],
        _ => [],
    };

    /// <summary>A pattern switch's <c>when</c> guards, which are expressions of the switch itself.</summary>
    public static IEnumerable<IrExpr> Guards(IReadOnlyDictionary<int, JCaseLabel>? patterns)
        => patterns?.Values.Select(p => p.Guard).OfType<IrExpr>() ?? [];

    /// <summary>How many times each local is mentioned anywhere in the tree, read or written.</summary>
    public static Dictionary<string, int> CountLocals(CStmt root)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in Descendants(root))
        {
            foreach (var expression in Expressions(node))
            {
                foreach (var e in JavaRewrite.PostOrder(expression))
                {
                    string? name = e switch
                    {
                        JLocal l => l.Name,
                        JAssignExpr { Target: JLocal target } => target.Name,
                        _ => null,
                    };

                    if (name is not null)
                    {
                        counts[name] = counts.GetValueOrDefault(name) + 1;
                    }
                }
            }
        }

        return counts;
    }

    /// <summary>Every <c>break</c> and <c>continue</c> in the tree, by label.</summary>
    public static HashSet<int> JumpTargets(CStmt root)
    {
        var targets = new HashSet<int>();
        foreach (var node in Descendants(root))
        {
            switch (node)
            {
                case JBreak b:
                    targets.Add(b.Label);
                    break;
                case JContinue c:
                    targets.Add(c.Label);
                    break;
            }
        }

        return targets;
    }
}
