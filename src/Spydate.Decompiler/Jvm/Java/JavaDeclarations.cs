using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Structuring;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// Where each local is declared in a structured method: in the innermost block that holds every mention of it,
/// just before the first — folded into that statement when it is an assignment to the local (<c>int n = 0;</c>),
/// or into a <c>for</c>'s initialiser when the variable lives only in that loop, or in several sibling loops
/// that each start it afresh (<c>for (int i = 0; …)</c> twice).
///
/// Declaring inside a loop body gives each iteration a fresh variable, which is only right when no iteration
/// reads a value an earlier one left — and that holds for verified bytecode: a variable whose every mention is
/// inside the loop is unassigned where the loop is entered, so the verifier has already required every read to
/// follow a write in the same iteration. That is Java's definite assignment, so the result also compiles.
/// </summary>
internal sealed class JavaDeclarations
{
    /// <summary>How a local class's declaration is named among the variables to place.</summary>
    public const string LocalClassPrefix = "class:";

    private readonly Dictionary<CSeq, Dictionary<int, List<string>>> _before = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IrStmt> _folded = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<JLoop> _forDeclared = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<CSeq> _scopes = new(ReferenceEqualityComparer.Instance);

    private JavaDeclarations()
    {
    }

    /// <summary>Names declared on their own line just before the item at <paramref name="index"/> of <paramref name="seq"/>.</summary>
    public IReadOnlyList<string> Before(CSeq seq, int index)
        => _before.TryGetValue(seq, out var at) && at.TryGetValue(index, out var names) ? names : [];

    /// <summary>Whether this assignment prints with its local's declaration.</summary>
    public bool Declares(IrStmt assignment) => _folded.Contains(assignment);

    /// <summary>Whether this loop's initialiser declares the variable it assigns.</summary>
    public bool Declares(JLoop loop) => _forDeclared.Contains(loop);

    /// <summary>Whether a block declares anything directly — a switch arm that does needs braces of its own.</summary>
    public bool HasDeclarations(CSeq seq) => _scopes.Contains(seq);

    /// <summary>The tree with every body a sequence of its own, so every block can take a declaration.</summary>
    public static CStmt Blocked(CStmt statement) => statement switch
    {
        CSeq seq => new CSeq(seq.Items.Select(Blocked).ToList()),
        CIf conditional => conditional with { Then = Seq(conditional.Then), Else = conditional.Else is null ? null : Seq(conditional.Else) },
        JLoop loop => loop with { Body = Seq(loop.Body) },
        JBlock block => block with { Body = Seq(block.Body) },
        JSwitch dispatch => dispatch with { Cases = dispatch.Cases.Select(c => c with { Body = Seq(c.Body) }).ToList() },
        JTry attempt => attempt with
        {
            Body = Seq(attempt.Body),
            Catches = attempt.Catches.Select(c => c with { Body = Seq(c.Body) }).ToList(),
            Finally = attempt.Finally is null ? null : Seq(attempt.Finally),
        },
        JSynchronized locked => locked with { Body = Seq(locked.Body) },
        _ => statement,
    };

    private static CSeq Seq(CStmt statement) => Blocked(statement) switch
    {
        CSeq seq => seq,
        var single => new CSeq([single]),
    };

    /// <summary>Places the declaration of every name in <paramref name="names"/> that the body mentions.</summary>
    public static JavaDeclarations Place(CSeq body, IReadOnlySet<string> names)
    {
        var declarations = new JavaDeclarations();
        var mentions = new Dictionary<string, List<(CSeq Seq, int Index)[]>>(StringComparer.Ordinal);
        var path = new List<(CSeq Seq, int Index)>();

        void Mention(IEnumerable<string> found)
        {
            foreach (string name in found)
            {
                if (names.Contains(name))
                {
                    if (!mentions.TryGetValue(name, out var list))
                    {
                        list = [];
                        mentions[name] = list;
                    }

                    list.Add([.. path]);
                }
            }
        }

        void WalkSeq(CSeq seq)
        {
            for (int i = 0; i < seq.Items.Count; i++)
            {
                path.Add((seq, i));
                Walk(seq.Items[i]);
                path.RemoveAt(path.Count - 1);
            }
        }

        void Walk(CStmt statement)
        {
            Mention(Own(statement));
            foreach (var child in JavaTree.Children(statement))
            {
                if (child is CSeq seq)
                {
                    WalkSeq(seq);
                }
                else
                {
                    Walk(child);
                }
            }
        }

        WalkSeq(body);

        foreach (var (name, paths) in mentions)
        {
            if (ForLoops(name, paths) is { } loops)
            {
                foreach (var loop in loops.OfType<JLoop>())
                {
                    declarations._forDeclared.Add(loop);
                }

                continue;
            }

            // The deepest block every mention is inside, and the first item of it that mentions the name.
            int depth = 0;
            while (paths.All(p => p.Length > depth + 1 && ReferenceEquals(p[depth + 1].Seq, paths[0][depth + 1].Seq))
                   && paths.All(p => p[depth].Index == paths[0][depth].Index))
            {
                depth++;
            }

            var scope = paths[0][depth].Seq;
            int first = paths.Min(p => p[depth].Index);
            declarations._scopes.Add(scope);
            if (scope.Items[first] is CRaw { Statement: IrAssign { Dst: JLocal target, Src: var value } assign }
                && target.Name == name && !Mentions(value, name))
            {
                declarations._folded.Add(assign);
                continue;
            }

            if (!declarations._before.TryGetValue(scope, out var at))
            {
                at = [];
                declarations._before[scope] = at;
            }

            if (!at.TryGetValue(first, out var list))
            {
                list = [];
                at[first] = list;
            }

            list.Add(name);
        }

        return declarations;
    }

    /// <summary>
    /// The loops whose initialisers can declare the name: when every mention is inside, or in the header of, a
    /// <c>for</c> that assigns the name first thing.
    /// </summary>
    private static List<CStmt>? ForLoops(string name, List<(CSeq Seq, int Index)[]> paths)
    {
        var loops = new List<CStmt>();
        foreach (var path in paths)
        {
            CStmt? owner = null;
            foreach (var (seq, index) in path)
            {
                if (seq.Items[index] is JLoop loop
                    && ((loop.Init is IrAssign { Dst: JLocal counter } && counter.Name == name) || loop.ForEach?.Variable.Name == name))
                {
                    owner = loop;
                    break;
                }

                // A try's resource is declared by the try, and lives only in it.
                if (seq.Items[index] is JTry attempt && attempt.Resources.Any(r => r.Variable.Name == name))
                {
                    owner = attempt;
                    break;
                }
            }

            if (owner is null)
            {
                return null;
            }

            if (!loops.Contains(owner))
            {
                loops.Add(owner);
            }
        }

        return loops;
    }

    /// <summary>The names a statement mentions itself, not in the statements it holds.</summary>
    private static IEnumerable<string> Own(CStmt statement) => statement switch
    {
        CRaw { Statement: IrAssign a } => Names(a.Dst).Concat(Names(a.Src)),
        CRaw { Statement: var s } => JavaRewrite.Evaluated(s).SelectMany(Names),
        CIf i => Names(i.Condition),
        CLoop { Condition: { } c } => Names(c),
        CSwitch s => Names(s.Value),
        JLoop { ForEach: { } each } l => Names(each.Source).Append(each.Variable.Name),
        JTry { Resources.Count: > 0 } t => t.Resources.SelectMany(r => Names(r.Init).Append(r.Variable.Name)),
        JAssert a => Names(a.Condition).Concat(a.Message is null ? [] : Names(a.Message)),
        JLoop l => (l.Condition is null ? [] : Names(l.Condition))
            .Concat(l.Init is IrAssign init ? Names(init.Dst).Concat(Names(init.Src)) : [])
            .Concat(l.Update is IrAssign update ? Names(update.Dst).Concat(Names(update.Src)) : []),
        JSwitch s => Names(s.Value),
        JSynchronized l => Names(l.Lock),
        _ => [],
    };

    /// <summary>
    /// Locals by name, and — as <see cref="LocalClassPrefix"/> and the class's internal name — every class created,
    /// so a local class is declared, like a variable, just before the first statement that uses it.
    /// </summary>
    private static IEnumerable<string> Names(IrExpr expression) => JavaRewrite.PostOrder(expression)
        .Select(e => e switch
        {
            JLocal l => l.Name,
            JAssignExpr { Target: JLocal target } => target.Name,
            JNew n => LocalClassPrefix + n.Owner,
            _ => null,
        })
        .OfType<string>()
        .Distinct();

    private static bool Mentions(IrExpr expression, string name) => Names(expression).Contains(name);
}
