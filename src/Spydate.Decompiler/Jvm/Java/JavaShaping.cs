using System.Collections.Immutable;
using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Structuring;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// Turns the <see cref="JavaStructurer"/>'s literal output into the Java a person writes. The structurer gives
/// every join a labelled block and every edge an explicit jump; here the jumps that only say "carry on" go, a
/// jump that leaves the innermost loop or switch becomes a plain <c>break</c> or <c>continue</c>, blocks nothing
/// breaks out of dissolve, an <c>if</c> whose one arm never falls through loses its <c>else</c>, a loop whose first
/// or last statement is its test becomes <c>while</c> or <c>do … while</c>, and one ending in its counter's update
/// becomes a <c>for</c>. Each rewrite preserves where every path goes; none of them guesses.
/// </summary>
internal static class JavaShaping
{
    private const int Rounds = 4;

    /// <summary>A folded conditional is kept below this height, like every tree the lifter builds.</summary>
    private const int MaxConditionalDepth = 48;

    public static CStmt Run(CStmt body, Func<string, bool> untabled)
    {
        for (int round = 0; round < Rounds; round++)
        {
            body = Jumps(body);
            body = JavaTree.Rewrite(body, Shape);
        }

        // Temporaries fold back once the structure has put them next to their uses; what that leaves may shape further.
        body = JavaInliner.RunOnTree(body, untabled);
        body = Jumps(body);
        body = JavaTree.Rewrite(body, Shape);
        return Jumps(body);
    }

    // --- jumps ------------------------------------------------------------------------------------

    /// <summary>A jump as a key: <c>break L</c> is 2L, <c>continue L</c> is 2L + 1.</summary>
    private static long BreakKey(int label) => 2L * label;

    private static long ContinueKey(int label) => (2L * label) + 1;

    /// <summary>
    /// Where the code being walked sits: the jumps that do the same as falling off the end of it, the innermost
    /// loop and switch, and for each label in scope every jump that goes where <c>break label</c> goes.
    /// </summary>
    private sealed record Place(ImmutableHashSet<long> Fall, int Breakable, int Loop, ImmutableDictionary<int, ImmutableHashSet<long>> Classes)
    {
        public ImmutableHashSet<long> ClassOf(CStmt jump) => jump switch
        {
            JBreak b => Classes.TryGetValue(b.Label, out var c) ? c : [BreakKey(b.Label)],
            JContinue c => [ContinueKey(c.Label)],
            _ => [],
        };
    }

    private static CStmt Jumps(CStmt body)
        => Jumps(body, new Place([], 0, 0, ImmutableDictionary<int, ImmutableHashSet<long>>.Empty));

    private static CStmt Jumps(CStmt statement, Place place)
    {
        switch (statement)
        {
            case JBreak or JContinue:
            {
                var keys = place.ClassOf(statement);
                if (keys.Overlaps(place.Fall))
                {
                    return CSeq.Empty;
                }

                if (place.Breakable != 0 && place.Classes.TryGetValue(place.Breakable, out var inner) && inner.Overlaps(keys))
                {
                    return new JBreak(place.Breakable);
                }

                if (place.Loop != 0 && keys.Contains(ContinueKey(place.Loop)))
                {
                    return new JContinue(place.Loop);
                }

                return statement;
            }

            case CSeq seq:
            {
                var items = seq.Items;
                var result = new List<CStmt>(items.Count);
                for (int i = 0; i < items.Count; i++)
                {
                    int next = i + 1;
                    while (next < items.Count && JavaTree.IsEmpty(items[next]))
                    {
                        next++;
                    }

                    var fall = next == items.Count ? place.Fall
                        : items[next] is JBreak or JContinue ? place.ClassOf(items[next]).Union(Fall(items, next, place)) : [];
                    result.Add(Jumps(items[i], place with { Fall = fall }));
                }

                return JavaTree.Sequence(result);
            }

            case CIf conditional:
                return conditional with { Then = Jumps(conditional.Then, place), Else = conditional.Else is null ? null : Jumps(conditional.Else, place) };

            case JBlock block:
            {
                var keys = place.Fall.Add(BreakKey(block.Label));
                var inner = place with { Fall = keys, Classes = place.Classes.SetItem(block.Label, keys) };
                var body = Jumps(block.Body, inner);
                return JavaTree.JumpTargets(body).Contains(block.Label) ? block with { Body = body } : body;
            }

            case JLoop loop:
            {
                var keys = place.Fall.Add(BreakKey(loop.Label));
                var inner = new Place([ContinueKey(loop.Label)], loop.Label, loop.Label, place.Classes.SetItem(loop.Label, keys));
                return loop with { Body = Jumps(loop.Body, inner) };
            }

            case JSwitch dispatch:
            {
                var keys = place.Fall.Add(BreakKey(dispatch.Label));
                var classes = place.Classes.SetItem(dispatch.Label, keys);
                var cases = new List<CCase>(dispatch.Cases.Count);
                for (int i = 0; i < dispatch.Cases.Count; i++)
                {
                    // An arm that runs off its end falls into the next one; only the last one leaves the switch.
                    var fall = i == dispatch.Cases.Count - 1 ? keys : ImmutableHashSet<long>.Empty;
                    cases.Add(dispatch.Cases[i] with { Body = Jumps(dispatch.Cases[i].Body, new Place(fall, dispatch.Label, place.Loop, classes)) });
                }

                return dispatch with { Cases = cases };
            }

            case JTry attempt:
                return attempt with
                {
                    Body = Jumps(attempt.Body, place),
                    Catches = attempt.Catches.Select(c => c with { Body = Jumps(c.Body, place) }).ToList(),
                    Finally = attempt.Finally is null ? null : Jumps(attempt.Finally, place with { Fall = [] }),
                };

            case JSynchronized locked:
                return locked with { Body = Jumps(locked.Body, place) };

            default:
                return statement;
        }
    }

    /// <summary>What falling off the jump at <paramref name="index"/> would mean, were it removed: the same as where the sequence goes next.</summary>
    private static ImmutableHashSet<long> Fall(IReadOnlyList<CStmt> items, int index, Place place)
    {
        for (int i = index + 1; i < items.Count; i++)
        {
            if (!JavaTree.IsEmpty(items[i]))
            {
                return [];
            }
        }

        return place.Fall;
    }

    // --- shapes -----------------------------------------------------------------------------------

    /// <summary>One statement, its children already shaped.</summary>
    private static CStmt Shape(CStmt statement) => statement switch
    {
        CSeq seq => Sequence(seq),
        CIf conditional => Sequence(new CSeq([conditional])),
        JLoop loop => Loop(loop),
        JSwitch dispatch => Switch(dispatch),
        _ => statement,
    };

    /// <summary>
    /// The arm holding <c>default</c> goes when it does nothing: last and empty, or only a <c>break</c> that no arm
    /// before it falls into. Any value it caught then matches no arm, which does the same nothing.
    /// </summary>
    private static CStmt Switch(JSwitch dispatch)
    {
        int defaultIndex = dispatch.Cases.SelectMany(c => c.Labels).DefaultIfEmpty(-1).Max();
        var cases = dispatch.Cases.ToList();
        for (int k = cases.Count - 1; k >= 0; k--)
        {
            if (!cases[k].Labels.Contains(defaultIndex))
            {
                continue;
            }

            bool empty = JavaTree.IsEmpty(cases[k].Body) && k == cases.Count - 1;
            bool breaks = cases[k].Body is JBreak b && b.Label == dispatch.Label && (k == 0 || JavaTree.NeverFallsThrough(cases[k - 1].Body));
            if (empty || breaks)
            {
                cases.RemoveAt(k);
            }
        }

        return cases.Count == 0 && !HasEffect(dispatch.Value) ? CSeq.Empty : dispatch with { Cases = cases };
    }

    private static CStmt Sequence(CSeq seq)
    {
        var items = new List<CStmt>(seq.Items.Count);
        foreach (var item in seq.Items)
        {
            if (item is CIf conditional)
            {
                items.AddRange(If(conditional));
            }
            else
            {
                items.Add(item);
            }
        }

        JavaSugar.Synchronized(items);
        JavaSugar.Finally(items);
        Conditionals(items);
        ReturnsInPlace(items);
        ForInitialisers(items);
        return JavaTree.Sequence(items);
    }

    /// <summary>
    /// <c>if (c) { s = a; } else { s = b; }</c>, where <c>s</c> is a value the bytecode kept on the stack across a
    /// branch, is <c>s = c ? a : b</c> — what the source wrote. A local of the source's own keeps its <c>if</c>.
    /// </summary>
    private static void Conditionals(List<CStmt> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is CIf
                {
                    Then: CRaw { Statement: IrAssign { Dst: JLocal { Kind: JLocalKind.Stack or JLocalKind.Temp } first, Src: JExpr a } },
                    Else: CRaw { Statement: IrAssign { Dst: JLocal second, Src: JExpr b } },
                } conditional
                && first.Name == second.Name)
            {
                var value = new JConditional(conditional.Condition, a, b);
                if (value.Depth <= MaxConditionalDepth)
                {
                    items[i] = new CRaw(new IrAssign(first, value) { Va = conditional.Va });
                }
            }
        }
    }

    /// <summary>
    /// A block followed by <c>return x</c> — javac's one return, shared by the paths that end the same way — is left
    /// by that return itself: each <c>break</c> out of the block becomes it. What remains of the block then falls
    /// through to the same return, or dissolves.
    /// </summary>
    private static void ReturnsInPlace(List<CStmt> items)
    {
        for (int i = 0; i + 1 < items.Count; i++)
        {
            if (items[i] is JBlock block && items[i + 1] is CRaw { Statement: IrReturn { Value: null or JLocal or JConst } ret })
            {
                items[i] = block with { Body = JavaTree.Rewrite(block.Body, s => s is JBreak b && b.Label == block.Label ? new CRaw(ret) : s) };
            }
        }
    }

    /// <summary>
    /// <c>if (c) {} else { e }</c> is <c>if (!c) { e }</c>; an arm that never falls through makes the other one the
    /// code after the <c>if</c>, and when it is the <c>else</c>, the test is inverted so the exit comes first.
    /// </summary>
    private static IEnumerable<CStmt> If(CIf conditional)
    {
        bool thenEmpty = JavaTree.IsEmpty(conditional.Then);
        bool elseEmpty = JavaTree.IsEmpty(conditional.Else);
        if (thenEmpty && elseEmpty)
        {
            return HasEffect(conditional.Condition) ? [conditional with { Else = null }] : [];
        }

        if (thenEmpty)
        {
            return [new CIf(Not(conditional.Condition), conditional.Else!, null, conditional.Va)];
        }

        if (elseEmpty)
        {
            return [conditional with { Else = null }];
        }

        if (JavaTree.NeverFallsThrough(conditional.Then))
        {
            return [conditional with { Else = null }, .. JavaTree.Items(conditional.Else)];
        }

        if (JavaTree.NeverFallsThrough(conditional.Else!))
        {
            return [new CIf(Not(conditional.Condition), conditional.Else!, null, conditional.Va), .. JavaTree.Items(conditional.Then)];
        }

        return [conditional];
    }

    public static IrExpr Not(IrExpr condition) => JLogical.Not(condition);

    private static bool HasEffect(IrExpr expression)
        => JavaRewrite.PostOrder(expression).Any(e => e is JCall or JNew or JDynamic);

    /// <summary>
    /// <c>while (true) { if (c) break; … }</c> is <c>while (!c) { … }</c>; <c>while (true) { …; if (c) break; }</c>
    /// is <c>do { … } while (!c)</c> when nothing in it continues the loop (a <c>continue</c> in a do-loop goes to the
    /// test, not the top). A while loop ending in an update of a variable its test reads is a <c>for</c>, and so is
    /// one whose body is a block its <c>continue</c>s break out of, followed by the update.
    /// </summary>
    private static CStmt Loop(JLoop loop)
    {
        if (loop.Kind == CLoopKind.Forever)
        {
            var items = JavaTree.Items(loop.Body);
            if (items.Count > 0 && items[0] is CIf { Then: JBreak { } first, Else: null } head && first.Label == loop.Label)
            {
                loop = loop with { Kind = CLoopKind.While, Condition = Not(head.Condition), Body = JavaTree.Sequence(items.Skip(1)), Va = head.Va };
            }
            else if (items.Count > 0 && items[^1] is CIf { Then: JBreak { } last, Else: null } tail && last.Label == loop.Label
                     && !JavaTree.Descendants(loop.Body).Any(d => d is JContinue c && c.Label == loop.Label))
            {
                return loop with { Kind = CLoopKind.DoWhile, Condition = Not(tail.Condition), Body = JavaTree.Sequence(items.Take(items.Count - 1)), Va = tail.Va };
            }
        }

        if (loop is { Kind: CLoopKind.While, Update: null, Condition: { } condition })
        {
            var items = JavaTree.Items(loop.Body);
            if (items.Count >= 1 && items[^1] is CRaw { Statement: IrAssign { Dst: JLocal counter } update } && Mentions(condition, counter.Name)
                && Mentions(update.Src, counter.Name))
            {
                var rest = items.Take(items.Count - 1).ToList();

                // for (…; …; i++) { … continue; … }: the continues reached the update by breaking out of a block around the rest.
                if (rest is [JBlock landing])
                {
                    var body = JavaTree.Rewrite(landing.Body, s => s is JBreak b && b.Label == landing.Label ? new JContinue(loop.Label) : s);
                    if (!JavaTree.Descendants(landing.Body).Any(d => d is JContinue c && c.Label == loop.Label))
                    {
                        return loop with { Body = body, Update = update };
                    }
                }
                else if (!rest.SelectMany(JavaTree.Descendants).Any(d => d is JContinue c && c.Label == loop.Label))
                {
                    return loop with { Body = JavaTree.Sequence(rest), Update = update };
                }
            }
        }

        return loop;
    }

    /// <summary>The assignment just before a <c>for</c> to the variable it updates is its initialiser.</summary>
    private static void ForInitialisers(List<CStmt> items)
    {
        for (int i = 1; i < items.Count; i++)
        {
            if (items[i] is JLoop { Update: IrAssign { Dst: JLocal counter }, Init: null } loop
                && items[i - 1] is CRaw { Statement: IrAssign { Dst: JLocal initialised } init } && initialised.Name == counter.Name)
            {
                items[i] = loop with { Init = init };
                items.RemoveAt(i - 1);
                i--;
            }
        }
    }

    private static bool Mentions(IrExpr expression, string name)
        => JavaRewrite.PostOrder(expression).Any(e => e is JLocal l && l.Name == name);
}
