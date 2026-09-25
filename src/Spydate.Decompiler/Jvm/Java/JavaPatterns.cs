using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Structuring;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// Java 21's pattern switches put back from what javac makes of them. <c>switch (o) { case Integer i when i &gt; 3 -&gt; …
/// case String s -&gt; … default -&gt; … }</c> compiles to a loop around <c>typeSwitch(o, restart)</c> — an
/// <c>invokedynamic</c> whose static arguments are the labels, returning the index of the first label from
/// <c>restart</c> on that matches, -1 for null — and a <c>tableswitch</c> on that index. Each arm casts the value to
/// its pattern's variable; a guard that fails sets <c>restart</c> to the next label and goes round again. An enum
/// switch with patterns is the same through <c>enumSwitch</c>. D8 desugars the call into a synthetic
/// <c>switchDispatch</c> helper and the table into an <c>if</c> chain; the Dalvik lifter reads the helper back into
/// the same call site, and <see cref="Prepare"/> the chain back into the switch.
///
/// Two steps. Before structuring (<see cref="Prepare"/>), each retry — <c>restart = k; goto dispatch</c> — becomes
/// a <see cref="JNoMatch"/> that ends its path, so the loop is gone and the switch structures as any other, a
/// guard now <c>if (!guard) { no match }</c> at the top of its arm. After (<see cref="Rebuild"/>), each arm's leading
/// cast becomes its pattern's variable and that <c>if</c> its <c>when</c>; the switch is on the value again, with
/// <c>case null</c>, the arms in label order and <c>default</c> last, and without javac's <c>MatchException</c>
/// default, <c>requireNonNull</c> and bookkeeping variables. A switch any part of which is not in that shape is left
/// as it was, its retries printed as what they are.
/// </summary>
internal static class JavaPatterns
{
    public const string TypeSwitch = "java/lang/runtime/SwitchBootstraps.typeSwitch";
    public const string EnumSwitch = "java/lang/runtime/SwitchBootstraps.enumSwitch";

    /// <summary>The key, among a switch's patterns, of the pattern its default arm is: the last, unconditional one.</summary>
    public const int TotalKey = int.MinValue;

    /// <summary>The key, among a switch's patterns, that says its default arm takes null too: <c>case null, default</c>.</summary>
    public const int NullDefaultKey = int.MinValue + 1;

    // --- before structuring -------------------------------------------------------------------------

    public static void Prepare(LiftedMethod lifted)
    {
        var function = lifted.Function;
        MarkCovered(lifted);
        foreach (var head in function.Blocks.ToList())
        {
            ChainToSwitch(lifted, head);
            Fold(function, head);
            if (head.Statements.LastOrDefault() is not IrSwitch { Value: JDynamic { IsPatternDispatch: true, Args: [_, JLocal restart] } })
            {
                continue;
            }

            foreach (var block in function.Blocks)
            {
                if (!ReferenceEquals(block, head) && block.Successors is [var only] && only == head.StartVa
                    && Real(block.Statements) is [IrAssign { Dst: JLocal assigned, Src: JConst { Value: int next } }, .. var tail]
                    && assigned.Name == restart.Name && tail is [] or [IrGoto]

                    // A retry goes on from the label after one; the index's first value, 0, falls into the dispatch from before it.
                    && next >= 1 && block.StartVa != function.EntryVa)
                {
                    block.Statements.Clear();
                    block.Statements.Add(new JNoMatch(next) { Dispatch = head.Statements[^1].Va });
                    block.Successors.Clear();
                    head.Predecessors.Remove(block.StartVa);
                }
            }
        }
    }

    /// <summary>
    /// What javac's record-pattern handlers cover: every accessor call of a record pattern is in a range whose handler
    /// throws a new <c>MatchException</c> of what it caught (D8: a <c>RuntimeException(t.toString(), t)</c>). The
    /// ranges' edges are block boundaries, so each block that starts inside one is, statements and all.
    /// </summary>
    private static void MarkCovered(LiftedMethod lifted)
    {
        var byVa = lifted.Function.Blocks.ToDictionary(b => b.StartVa);
        foreach (var handler in lifted.Handlers)
        {
            if (!byVa.TryGetValue((ulong)handler.HandlerPc, out var catcher) || !WrapsInMatchException(catcher.Statements))
            {
                continue;
            }

            foreach (var block in lifted.Function.Blocks.Where(b => b.StartVa >= (ulong)handler.StartPc && b.StartVa < (ulong)handler.EndPc))
            {
                lifted.MatchCovered.UnionWith(block.Statements.Select(s => s.Va));
            }
        }
    }

    /// <summary>A handler that throws a new <c>MatchException</c>, or a <c>RuntimeException</c> of a message made by <c>toString()</c> — directly or through variables.</summary>
    private static bool WrapsInMatchException(List<IrStmt> statements)
    {
        IrExpr Resolve(IrExpr e) => e is JLocal l && statements.OfType<IrAssign>().LastOrDefault(a => a.Dst is JLocal d && d.Name == l.Name) is { } stored ? Resolve(stored.Src) : e;
        return statements.OfType<JThrow>().FirstOrDefault() is { } thrown && Resolve(thrown.Exception) switch
        {
            JNew { Owner: "java/lang/MatchException" } => true,
            JNew { Owner: "java/lang/RuntimeException", Args: [var message, _] } => Resolve(message) is JCall { Name: "toString", Args.Count: 0 },
            _ => false,
        };
    }

    /// <summary>
    /// D8's dispatch: <c>r = typeSwitch(…); if (r == 0) goto A; if (r == 1) goto B; … default</c>, each test in a block of its
    /// own that only the one before reaches, is the switch javac had — made one again when <c>r</c> is read nowhere else.
    /// D8 loads constants the arms use between the tests; a constant whose variable is written nowhere else moves ahead
    /// of the dispatch, where it means the same.
    /// </summary>
    private static void ChainToSwitch(LiftedMethod lifted, IrBlock head)
    {
        var function = lifted.Function;
        var statements = Real(head.Statements);
        if (statements is not [.., IrBranch first])
        {
            return;
        }

        var writes = function.Blocks.SelectMany(b => b.Statements).OfType<IrAssign>().Select(a => a.Dst).OfType<JLocal>()
            .GroupBy(l => l.Name).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        bool Hoistable(IrStmt statement) => statement is IrAssign { Dst: JLocal target, Src: JConst } && writes.GetValueOrDefault(target.Name) == 1;

        // In the dispatch's own block a constant after it simply goes before it; from the test blocks, only one written once.
        int at0 = statements.Count - 2;
        var hoisted = new List<IrStmt>();
        while (at0 >= 0 && statements[at0] is IrAssign { Dst: JLocal, Src: JConst })
        {
            hoisted.Insert(0, statements[at0--]);
        }

        if (at0 < 0 || statements[at0] is not IrAssign { Dst: JLocal result, Src: JDynamic { IsPatternDispatch: true } dispatch } assign)
        {
            return;
        }

        var arguments = dispatch.Args.SelectMany(JavaRewrite.PostOrder).OfType<JLocal>().Select(l => l.Name).ToHashSet(StringComparer.Ordinal);
        if (hoisted.Any(h => h is IrAssign { Dst: JLocal t } && (arguments.Contains(t.Name) || t.Name == result.Name)))
        {
            return;
        }

        var byVa = function.Blocks.ToDictionary(b => b.StartVa);
        var targets = new List<ulong>();
        var values = new List<int?>();
        var chain = new List<IrBlock>();
        var branch = first;
        var at = head;
        while (true)
        {
            if (Tested(branch, result) is not var (value, match, next) || values.Contains(value))
            {
                return;
            }

            targets.Add(match);
            values.Add(value);
            if (byVa.TryGetValue(next, out var following) && following.Predecessors is [var only] && only == at.StartVa
                && Real(following.Statements) is [.. var loads, IrBranch another] && Tested(another, result) is not null
                && loads.All(l => Hoistable(l) && l is IrAssign { Dst: JLocal t } && !arguments.Contains(t.Name)))
            {
                hoisted.AddRange(loads);
                chain.Add(following);
                at = following;
                branch = another;
                continue;
            }

            targets.Add(next);
            values.Add(null);
            break;
        }

        // The result is read by the tests and nothing else.
        int reads = function.Blocks.SelectMany(b => b.Statements).SelectMany(JavaRewrite.Evaluated).SelectMany(JavaRewrite.PostOrder)
            .Count(e => e is JLocal l && l.Name == result.Name);
        if (reads != chain.Count + 1)
        {
            return;
        }

        var kept = head.Statements.Where(x => !ReferenceEquals(x, assign) && !ReferenceEquals(x, first) && !hoisted.Contains(x)).ToList();
        head.Statements.Clear();
        head.Statements.AddRange(kept);
        head.Statements.AddRange(hoisted);
        head.Statements.Add(new IrSwitch(dispatch, targets) { Va = first.Va });
        lifted.SwitchValues[first.Va] = [.. values];

        var removed = chain.Select(b => b.StartVa).ToHashSet();
        function.Blocks.RemoveAll(b => removed.Contains(b.StartVa));
        head.Successors.Clear();
        head.Successors.AddRange(targets.Distinct());
        foreach (ulong target in head.Successors)
        {
            var block = byVa[target];
            block.Predecessors.RemoveAll(removed.Contains);
            if (!block.Predecessors.Contains(head.StartVa))
            {
                block.Predecessors.Add(head.StartVa);
            }
        }
    }

    /// <summary>
    /// <c>r = typeSwitch(…); switch (r)</c>, as a register machine writes it, is <c>switch (typeSwitch(…))</c> when the
    /// switch is all that reads <c>r</c>. Constants D8 loads between the two go ahead of the call, which does not read them.
    /// </summary>
    private static void Fold(IrFunction function, IrBlock head)
    {
        var statements = head.Statements;
        int last = statements.FindLastIndex(s => s is not (IrNop or IrComment or IrLabel));
        if (last < 1 || statements[last] is not IrSwitch { Value: JLocal switched } dispatch)
        {
            return;
        }

        var loads = new List<int>();
        int at = last - 1;
        for (; at >= 0; at--)
        {
            if (statements[at] is IrNop or IrComment or IrLabel)
            {
                continue;
            }

            if (statements[at] is IrAssign { Dst: JLocal, Src: JConst })
            {
                loads.Add(at);
                continue;
            }

            break;
        }

        if (at < 0 || statements[at] is not IrAssign { Dst: JLocal result, Src: JDynamic { IsPatternDispatch: true } call } || result.Name != switched.Name)
        {
            return;
        }

        var arguments = call.Args.SelectMany(JavaRewrite.PostOrder).OfType<JLocal>().Select(l => l.Name).ToHashSet(StringComparer.Ordinal);
        if (loads.Any(i => statements[i] is IrAssign { Dst: JLocal t } && (arguments.Contains(t.Name) || t.Name == result.Name)))
        {
            return;
        }

        int reads = function.Blocks.SelectMany(b => b.Statements).SelectMany(JavaRewrite.Evaluated).SelectMany(JavaRewrite.PostOrder)
            .Count(e => e is JLocal l && l.Name == result.Name);
        if (reads != 1)
        {
            return;
        }

        var moved = loads.OrderBy(i => i).Select(i => statements[i]).ToList();
        statements[last] = dispatch with { Value = call };
        foreach (int i in loads.OrderByDescending(i => i))
        {
            statements.RemoveAt(i);
        }

        statements.InsertRange(at, moved);
        statements.RemoveAt(at + moved.Count);
    }

    /// <summary><c>if (r == k)</c> or <c>if (r != k)</c> on the dispatch's result: the label, where it goes, where the rest goes.</summary>
    private static (int Value, ulong Match, ulong Next)? Tested(IrBranch branch, JLocal result) => branch.Condition switch
    {
        IrCondition { Cc: IrCondCode.Equal, Left: JLocal l, Right: JConst { Value: int k } } when l.Name == result.Name => (k, branch.TargetVa, branch.FallthroughVa),
        IrCondition { Cc: IrCondCode.NotEqual, Left: JLocal l, Right: JConst { Value: int k } } when l.Name == result.Name => (k, branch.FallthroughVa, branch.TargetVa),
        _ => null,
    };

    private static List<IrStmt> Real(List<IrStmt> statements) => statements.Where(s => s is not (IrNop or IrComment or IrLabel)).ToList();

    // --- after structuring --------------------------------------------------------------------------

    /// <summary>Whether a structured method still has a pattern switch that did not read back: a retry, or a dispatch, left in it.</summary>
    public static bool Unfinished(CStmt body) => JavaTree.Descendants(body).Any(node => node switch
    {
        CRaw { Statement: JNoMatch } => true,
        JSwitch { Value: JDynamic { IsPatternDispatch: true } } => true,
        CRaw raw => JavaRewrite.Evaluated(raw.Statement).SelectMany(JavaRewrite.PostOrder).Any(e => e is JDynamic { IsPatternDispatch: true }),
        CIf conditional => JavaRewrite.PostOrder(conditional.Condition).Any(e => e is JDynamic { IsPatternDispatch: true }),
        _ => false,
    });

    /// <summary>What the rebuild needs of the method and the program: each switch's case values, and which classes are sealed.</summary>
    /// <param name="Covered">The statements a <c>MatchException</c> handler covers — record accessor calls — by address.</param>
    public sealed record Context(IReadOnlyDictionary<ulong, int?[]> SwitchValues, Func<string, bool> IsSealed, Func<string, int?> RecordArity, IReadOnlySet<ulong> Covered)
    {
        /// <summary>No switch values: nothing is rebuilt.</summary>
        public static Context None { get; } = new(new Dictionary<ulong, int?[]>(), _ => false, _ => null, new HashSet<ulong>());
    }

    /// <summary>Every pattern switch among a sequence's statements rebuilt, with the statements javac put before it for its own use gone.</summary>
    public static void Rebuild(List<CStmt> items, IReadOnlyDictionary<string, int> counts, Func<string, bool> compilerMade, Context context)
    {
        for (int k = 0; k < items.Count; k++)
        {
            // Rebuilt here, or — alone in a block — already, with what it read kept for this step.
            JSwitch rebuilt;
            PatternOrigin origin;
            if (items[k] is JSwitch { Value: JDynamic { IsPatternDispatch: true } dispatch } node
                && context.SwitchValues.GetValueOrDefault(node.Va) is { } found
                && Rebuilt(node, dispatch, found, counts, context, null) is var (made, madeBindings))
            {
                origin = new PatternOrigin(dispatch.Args[0], dispatch.Args[1], madeBindings, found);
                rebuilt = made with { Value = origin.Selector, Origin = origin, Patterns = NullDefault(made, found, nullChecked: false) };
                items[k] = rebuilt;
            }
            else if (items[k] is JSwitch { Origin: { } kept } already)
            {
                rebuilt = already;
                origin = kept;
            }
            else
            {
                continue;
            }

            // What javac put before it: the selector's copy, the restart index, and the null check a switch without
            // case null makes.
            var selector = origin.Selector;
            int bindings = origin.Bindings;
            var values = origin.Values;
            int first = k;
            if (origin.Restart is JLocal restart && first > 0
                && items[first - 1] is CRaw { Statement: IrAssign { Dst: JLocal r, Src: JConst { Value: 0 } } } && r.Name == restart.Name
                && counts.GetValueOrDefault(restart.Name) == 2)
            {
                first--;
            }

            IrExpr value = selector;
            // javac's own variable for it, unnamed or — in a lambda, where it records one — selectorN$temp.
            if (selector is JLocal copy && (compilerMade(copy.Name) || copy.Name.Contains('$')) && first > 0
                && items[first - 1] is CRaw { Statement: IrAssign { Dst: JLocal s, Src: JExpr source } } && s.Name == copy.Name
                && counts.GetValueOrDefault(copy.Name) == 2 + bindings
                && !rebuilt.Cases.Any(c => JavaTree.CountLocals(c.Body).ContainsKey(copy.Name)))
            {
                value = source;
                first--;
            }

            // The null check a switch without case null makes: just before it, or before the constants D8 loaded and
            // the copies of the value made in between.
            bool nullChecked = false;
            var aliases = new List<IrExpr> { value, selector };
            for (int j = first - 1; j >= 0; j--)
            {
                if (items[j] is CRaw { Statement: JExprStmt { Expression: JCall { Owner: "java/util/Objects", Name: "requireNonNull", Args: [var checkedValue] } } }
                    && aliases.Any(a => JavaEquality.Same(checkedValue, a)))
                {
                    nullChecked = true;
                    items.RemoveAt(j);
                    first--;
                    k--;
                    break;
                }

                if (items[j] is CRaw { Statement: IrAssign { Dst: JLocal copied, Src: JLocal from } } && aliases.Any(a => a is JLocal l && l.Name == copied.Name))
                {
                    aliases.Add(from);
                    continue;
                }

                if (items[j] is not CRaw { Statement: IrAssign { Dst: JLocal, Src: JConst } })
                {
                    break;
                }
            }

            // Nothing before it said anything: kept as it is, in case a block around it does.
            if (first == k && !nullChecked)
            {
                continue;
            }

            items[k] = rebuilt with { Value = value, Origin = null, Patterns = NullDefault(rebuilt, values, nullChecked) };
            items.RemoveRange(first, k - first);
            k = first;
        }
    }

    /// <summary>
    /// The switch's patterns, with its default arm taking null when nothing else does: neither checked for null first
    /// nor given a case for it, null goes where the dispatch's -1 does — D8 leaves that to the default arm, which in
    /// Java has to say so.
    /// </summary>
    private static IReadOnlyDictionary<int, JCaseLabel> NullDefault(JSwitch rebuilt, int?[] values, bool nullChecked)
    {
        var patterns = new Dictionary<int, JCaseLabel>(rebuilt.Patterns!);
        patterns.Remove(NullDefaultKey);
        var cases = rebuilt.Cases.SelectMany(c => c.Labels).Select(i => i < values.Length ? values[i] : i).ToList();
        if (!nullChecked && !cases.Contains(-1) && cases.Contains(null) && !patterns.ContainsKey(TotalKey))
        {
            patterns[NullDefaultKey] = new JCaseLabel();
        }

        return patterns;
    }

    /// <summary>The switch with its labels as patterns, and how many binding casts of the selector went; null when any arm is not in javac's shape.</summary>
    /// <param name="outer">
    /// For a switch javac nested in an arm of another — on one component of the record that arm deconstructs — the
    /// no-match its default is: matching going on in the outer switch, which is no arm of this one.
    /// </param>
    private static (JSwitch Switch, int Bindings)? Rebuilt(JSwitch node, JDynamic dispatch, int?[] values, IReadOnlyDictionary<string, int> counts, Context context, (int Next, ulong Dispatch)? outer)
    {
        var isSealed = context.IsSealed;
        var labels = dispatch.BootstrapArguments!;
        var selector = dispatch.Args[0];
        bool enumSwitch = dispatch.Bootstrap == EnumSwitch;
        var patterns = new Dictionary<int, JCaseLabel>();
        var arms = new List<(long Order, CCase Case)>();
        int bindings = 0;
        int merged = 0;
        var taken = new HashSet<string>(counts.Keys, StringComparer.Ordinal);

        var used = node.Cases.SelectMany(c => c.Labels).Select(i => i < values.Length ? values[i] : i).OfType<int>().ToHashSet();
        foreach (var arm in node.Cases)
        {
            var armValues = arm.Labels.Select(i => i < values.Length ? values[i] : i).ToList();
            var body = JavaTree.Items(arm.Body).Where(x => !JavaTree.IsEmpty(x)).ToList();
            bool isDefault = armValues.Contains(null);
            var numbered = armValues.OfType<int>().Where(v => v >= 0).OrderBy(v => v).ToList();
            if (numbered.Any(v => v >= labels.Count) || (numbered.Count > 1 && numbered.Any(v => labels[v] is JConst { Value: ClassLiteral })))
            {
                return null;
            }

            // javac's default for a switch that is exhaustive without one goes, when what makes it exhaustive can be seen:
            // an enum's constants, or a sealed type's permitted subclasses. D8 drops the latter; there it stays, as legal
            // Java that does what the bytecode does.
            if (isDefault && numbered.Count == 0 && !armValues.Contains(-1)
                && ThrowsMatchException(body)
                && (enumSwitch || (selector.Type is ['L', .. var named, ';'] && isSealed(named))))
            {
                continue;
            }

            // A nested switch's default that sends matching on in the switch around it — null with it, a component no
            // pattern here takes — is none of its arms.
            if (isDefault && numbered.Count == 0 && outer is var (outerNext, outerDispatch)
                && body is [CRaw { Statement: JNoMatch { Next: var n, Dispatch: var d } }] && n == outerNext && d == outerDispatch)
            {
                continue;
            }

            bool expanded = false;

            foreach (int v in numbered)
            {
                switch (labels[v])
                {
                    case JConst { Value: ClassLiteral type }:
                    {
                        var binding = Binding(body, selector);
                        if (binding is not null)
                        {
                            body.RemoveAt(0);
                            bindings++;
                        }
                        else if (CastsOnly(body, selector, type.Descriptor) is { Count: > 0 } casts)
                        {
                            // D8 casts the selector's own register: the arm reads it as (T) value throughout. Those
                            // casts are the pattern's variable.
                            binding = Fresh(type.Descriptor, taken);
                            var variable = binding;
                            body = body.Select(item => JavaTree.MapExpressions(item, e => e is JCast c && casts.Contains(c) ? variable : null)).ToList();
                            bindings += casts.Count;
                        }

                        // What follows the cast: the record deconstructed, nested patterns tested, and the guard.
                        // A record deconstructed if the steps say so; failing that, the type pattern they are without it.
                        var armScope = body.ToList();
                        var full = Deconstruct(body, binding, type.Descriptor, v + 1, node.Va, taken, counts, context.RecordArity, context.Covered);

                        // javac's merged cases: consecutive record patterns of one type share the deconstruction, and a
                        // switch on one component inside tells them apart. Each of its arms is a case of its own, and
                        // finishes the pattern the arm began — which may so far be only the components it switches on.
                        var begun = full is { Label.Components: not null } ? full
                            : Deconstruct(body, binding, type.Descriptor, v + 1, node.Va, taken, counts, context.RecordArity, context.Covered, partial: true);
                        if (numbered.Count == 1 && !isDefault && begun is { Root.Components: not null, Label.Guard: null }
                            && Merged(begun, v + 1, node, armScope, counts, context, taken) is { } group)
                        {
                            foreach (var (part, partBody) in group)
                            {
                                int key = SyntheticKey + merged++;
                                patterns[key] = part;
                                arms.Add((((long)v * 1024) + merged, new CCase([key], JavaTree.Sequence(partBody))));
                            }

                            expanded = true;
                            break;
                        }

                        if ((full ?? Deconstruct(body, binding, type.Descriptor, v + 1, node.Va, taken, counts, null, context.Covered)) is not var (label, rest, _, _))
                        {
                            return null;
                        }

                        patterns[v] = label;
                        body = rest;
                        break;
                    }

                    case JConst { Value: string or int } constant:
                        patterns[v] = new JCaseLabel { Constant = constant, EnumConstant = enumSwitch && constant.Value is string };
                        break;
                    default:
                        return null;
                }
            }

            // The last label unconditional — `case Color x` after the others — is javac's default arm, the value taken as it is.
            if (isDefault && labels.Count > 0 && labels[^1] is JConst { Value: ClassLiteral total } && !used.Contains(labels.Count - 1)
                && body.Count > 0 && body[0] is CRaw { Statement: IrAssign { Dst: JLocal whole, Src: var same } } && JavaEquality.Same(same, selector))
            {
                patterns[TotalKey] = new JCaseLabel { Type = total.Descriptor, Binding = whole };
                body.RemoveAt(0);
                bindings++;
            }

            if (expanded)
            {
                continue;
            }

            if (body.SelectMany(JavaTree.Descendants).Any(d => d is CRaw { Statement: JNoMatch }))
            {
                return null;
            }

            long order = isDefault ? long.MaxValue : (long)armValues.OfType<int>().Min() * 1024;
            arms.Add((order, arm with { Body = JavaTree.Sequence(body) }));
        }

        // Arms run in label order, so none may fall into the next: a pattern's variable exists only in its own. The last
        // in address order may run off the end of the switch; out of that place it breaks.
        if (arms.Count == 0 || arms.SkipLast(1).Any(a => !JavaTree.NeverFallsThrough(a.Case.Body)))
        {
            return null;
        }

        if (!JavaTree.NeverFallsThrough(arms[^1].Case.Body))
        {
            arms[^1] = (arms[^1].Order, arms[^1].Case with { Body = JavaTree.Sequence([arms[^1].Case.Body, new JBreak(node.Label)]) });
        }

        var ordered = arms.OrderBy(a => a.Order).Select(a => a.Case).ToList();

        // A pattern's variable is declared by its label and lives in its arm. One whose name the method uses elsewhere
        // too — the slot's name for something else — takes a name of its own, in its label, guard and arm.
        foreach (var group in patterns.Where(p => p.Value.Binding is not null && p.Value.Components is null).GroupBy(p => p.Value.Binding!.Name).ToList())
        {
            string name = group.Key;
            int inside = ordered.Sum(c => JavaTree.CountLocals(c.Body).GetValueOrDefault(name))
                         + group.Count()
                         + group.Sum(p => p.Value.Guard is { } g ? JavaRewrite.PostOrder(g).Count(e => e is JLocal l && l.Name == name) : 0);
            if (counts.GetValueOrDefault(name) <= inside)
            {
                continue;
            }

            var renamed = Fresh(name, taken);
            JExpr? Swap(JLocal l) => l.Name == name ? renamed with { LocalType = l.LocalType } : null;
            foreach (var (key, pattern) in group.ToList())
            {
                patterns[key] = pattern with
                {
                    Binding = pattern.Binding! with { Name = renamed.Name },
                    Guard = pattern.Guard is { } g ? JavaRewrite.Replace(g, Swap) : null,
                };
            }

            ordered = ordered.Select(c => c with { Body = Renamed(c.Body, name, renamed.Name, Swap) }).ToList();
        }

        return (node with { Cases = ordered, Patterns = patterns }, bindings);
    }

    /// <summary>Case keys for the arms a merged case becomes: past any label index, and past any case value a switch has.</summary>
    private const int SyntheticKey = 1 << 24;

    /// <summary>
    /// An arm that deconstructs a record and then switches on one of its components — <c>restart = 0; switch
    /// (typeSwitch(component, restart))</c>, whose default is a no-match of the outer switch — read as the cases javac
    /// merged. Each arm of the inner switch goes on from the pattern the outer arm built, the component now of the arm's
    /// type: it may deconstruct that component, or the record's other components, and test and guard as any arm does.
    /// </summary>
    private static List<(JCaseLabel Label, List<CStmt> Body)>? Merged(Matched outerArm, int next, JSwitch outer, IReadOnlyList<CStmt> scope, IReadOnlyDictionary<string, int> counts, Context context, HashSet<string> taken)
    {
        var items = outerArm.Remaining.Where(x => !JavaTree.IsEmpty(x)).ToList();
        if (items.Count > 0 && items[^1] is JBreak end && end.Label == outer.Label)
        {
            items.RemoveAt(items.Count - 1);
        }

        if (items.Count == 0 || items[^1] is not JSwitch { Value: JDynamic { IsPatternDispatch: true } dispatch } inner
            || context.SwitchValues.GetValueOrDefault(inner.Va) is not { } values)
        {
            return null;
        }

        // Before it, only what javac or D8 put there for it: the restart index at 0, and the value switched on held in a
        // variable of its own. Each is read by the dispatch and nothing else.
        IrExpr switchedOn = dispatch.Args[0];
        foreach (var item in items.SkipLast(1))
        {
            if (item is not CRaw { Statement: IrAssign { Dst: JLocal target, Src: var value } } || counts.GetValueOrDefault(target.Name) != 2)
            {
                return null;
            }

            if (dispatch.Args is [_, JLocal restart] && restart.Name == target.Name && value is JConst { Value: 0 })
            {
                continue;
            }

            if (switchedOn is JLocal copied && copied.Name == target.Name)
            {
                switchedOn = value;
                continue;
            }

            return null;
        }

        // The component switched on: its variable, or that boxed.
        var switched = switchedOn is JCall { Receiver: null, Name: "valueOf", Args: [JLocal boxed] } ? boxed : switchedOn as JLocal;
        if (switched is null || !outerArm.Held.TryGetValue(switched.Name, out var component) || ReferenceEquals(component, outerArm.Root))
        {
            return null;
        }

        var labels = dispatch.BootstrapArguments!;
        var parts = new List<(long Order, JCaseLabel Label, List<CStmt> Body)>();
        foreach (var arm in inner.Cases)
        {
            bool last = ReferenceEquals(arm, inner.Cases[^1]);
            var keys = arm.Labels.Select(i => i < values.Length ? values[i] : i).ToList();
            var numbered = keys.OfType<int>().Where(v => v >= 0).ToList();
            var body = JavaTree.Items(arm.Body).Where(x => !JavaTree.IsEmpty(x)).ToList();

            // Matching going on outside: none of the cases here.
            if (numbered.Count == 0)
            {
                if (body is [CRaw { Statement: JNoMatch { Next: var n, Dispatch: var d } }] && n == next && d == outer.Va)
                {
                    continue;
                }

                return null;
            }

            // One type per arm; null goes to the arm whose type is the component's own — total, it takes null too.
            if (numbered is not [int u] || labels[u] is not JConst { Value: ClassLiteral type }
                || (keys.Contains(-1) && type.Descriptor != component.Static))
            {
                return null;
            }

            var copies = new Dictionary<Node, Node>(ReferenceEqualityComparer.Instance);
            var root = outerArm.Root.Clone(copies);
            var held = outerArm.Held.ToDictionary(h => h.Key, h => copies[h.Value], StringComparer.Ordinal);
            var here = copies[component];

            // Its type, unless that is the component's own — or the box of the primitive the component is.
            if (type.Descriptor != here.Static && !(here.Static is [not ('L' or '[')] && Boxes(here.Static, type.Descriptor)))
            {
                here.Tested = type.Descriptor;
            }

            // The arm's own copy of the value switched on is another name for the component.
            if (switchedOn is JCall && dispatch.Args[0] is JLocal boxedCopy)
            {
                here.Variables.Add(boxedCopy.Name);
                held[boxedCopy.Name] = here;
            }

            if (Deconstruct(body, null, root.Type!, u + 1, inner.Va, taken, counts, context.RecordArity, context.Covered, scope, (root, held)) is not { } matched)
            {
                return null;
            }

            // Leaving the inner switch is leaving the arm, which is leaving the outer switch — running off its end too.
            var rest = JavaTree.Rewrite(JavaTree.Sequence(matched.Remaining), s => s is JBreak b && b.Label == inner.Label ? new JBreak(outer.Label) : s);
            if (!JavaTree.NeverFallsThrough(rest))
            {
                if (!last)
                {
                    return null;
                }

                rest = JavaTree.Sequence([rest, new JBreak(outer.Label)]);
            }

            parts.Add((u, matched.Label, [.. JavaTree.Items(rest)]));
        }

        return parts.Count == 0 ? null : parts.OrderBy(p => p.Order).Select(p => (p.Label, p.Body)).ToList();
    }

    /// <summary>Whether a class is the box of a primitive type: <c>Ljava/lang/Integer;</c> of <c>I</c>.</summary>
    private static bool Boxes(string primitive, string box) => (primitive, box) switch
    {
        ("I", "Ljava/lang/Integer;") or ("J", "Ljava/lang/Long;") or ("Z", "Ljava/lang/Boolean;") or ("C", "Ljava/lang/Character;")
            or ("B", "Ljava/lang/Byte;") or ("S", "Ljava/lang/Short;") or ("F", "Ljava/lang/Float;") or ("D", "Ljava/lang/Double;") => true,
        _ => false,
    };

    /// <summary>One step of what an arm does before its body: a store, or a test whose failure is a no-match.</summary>
    private sealed record Step(JLocal? Target, IrExpr Value, bool InTry, CStmt? Source);

    /// <summary>A pattern as it is read back: a type, the variables that hold it, and — for a record pattern — its components.</summary>
    private sealed class Node
    {
        public string? Tested { get; set; }

        public string? Static { get; init; }

        public List<Node>? Components { get; set; }

        public List<string> Variables { get; } = [];

        /// <summary>Which of its variables are the lifter's stack values: temporaries that a name is reused for, method-wide.</summary>
        public HashSet<string> Stack { get; } = new(StringComparer.Ordinal);

        public string? Type => Tested ?? Static;

        /// <summary>Whether its accessor was called inside javac's try — which says it is a component of a record pattern.</summary>
        public bool InTry { get; init; }

        /// <summary>A copy of the tree from here down, each copy recorded against its original.</summary>
        public Node Clone(Dictionary<Node, Node> copies)
        {
            var copy = new Node { Tested = Tested, Static = Static, InTry = InTry };
            copy.Variables.AddRange(Variables);
            copy.Stack.UnionWith(Stack);
            copy.Components = Components?.Select(c => c.Clone(copies)).ToList();
            copies[this] = copy;
            return copy;
        }
    }

    /// <summary>An arm read back: its label and body, and the pattern as built — what a case javac merged goes on from.</summary>
    private sealed record Matched(JCaseLabel Label, List<CStmt> Remaining, Node Root, Dictionary<string, Node> Held);

    /// <summary>
    /// A type pattern's arm read back into its label and body. javac follows the cast with the record's accessors — in
    /// a try that turns what they throw into a <c>MatchException</c> — copies of what they return, and for a nested
    /// pattern an <c>instanceof</c> whose failure is a no-match and a cast; then the guard's tests, each a no-match when
    /// it fails. Replayed, those steps build the pattern: <c>Pair(Leaf(int a), Node r)</c>, each component's variable the
    /// one of its copies the body reads. What the guard tests is <c>when</c>; the rest is the body. Null when the steps
    /// say anything a pattern cannot.
    /// </summary>
    /// <param name="arity">How many components a record class has, by internal name; null reads no record pattern at all.</param>
    /// <param name="scope">The statements a pattern's variables live in, when more than the body: a merged case's whole arm.</param>
    /// <param name="start">The pattern as far as the arm around this one built it, for a case javac merged.</param>
    /// <param name="partial">Only begin the pattern: the steps taken, nothing checked that the whole pattern must satisfy.</param>
    private static Matched? Deconstruct(
        List<CStmt> body,
        JLocal? binding,
        string type,
        int next,
        ulong dispatch,
        HashSet<string> taken,
        IReadOnlyDictionary<string, int> counts,
        Func<string, int?>? arity,
        IReadOnlySet<ulong> covered,
        IReadOnlyList<CStmt>? scope = null,
        (Node Root, Dictionary<string, Node> Held)? start = null,
        bool partial = false)
    {
        var fresh = new HashSet<string>(taken, StringComparer.Ordinal);
        if (Flatten(body, next, dispatch, covered) is not var (steps, rest))
        {
            return null;
        }

        Node root;
        Dictionary<string, Node> held;
        if (start is var (startRoot, startHeld))
        {
            root = startRoot;
            held = startHeld;
        }
        else
        {
            root = new Node { Tested = type };
            held = new Dictionary<string, Node>(StringComparer.Ordinal);
            if (binding is not null)
            {
                root.Variables.Add(binding.Name);
                held[binding.Name] = root;
            }
        }

        // The pattern's steps first; from the first test that is not one, the guard; the stores after the last test are the body's.
        int lastTest = steps.FindLastIndex(t => t.Target is null);
        var guards = new List<IrExpr>();
        int k = 0;
        var tests = new Dictionary<string, (string Operand, string Type)>(StringComparer.Ordinal);
        for (; k < steps.Count; k++)
        {
            var step = steps[k];
            if (!PatternStep(step, held, arity is not null, tests))
            {
                break;
            }
        }

        for (; k <= lastTest; k++)
        {
            if (steps[k].Target is not null)
            {
                return null;
            }

            guards.Add(steps[k].Value);
        }

        // The body's own stores, the structurer's try around them or not: they run after the pattern matched.
        if (steps.Skip(k).Any(t => t.Source is null))
        {
            return null;
        }

        rest = [.. steps.Skip(k).Select(t => t.Source!), .. rest];

        var guard = guards.Count == 0 ? null : guards.Skip(1).Aggregate(guards[0], (all, one) => new JLogical(true, all, one));

        // Begun only, for a merged case to finish: the pattern as built so far, which says nothing yet on its own.
        if (partial)
        {
            return guard is null ? new Matched(new JCaseLabel { Type = type }, rest, root, held) : null;
        }

        // Each pattern's variable is the one of its copies read after the steps; a deconstructed record's are not read.
        var read = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in rest)
        {
            foreach (var (name, n) in JavaTree.CountLocals(item))
            {
                read[name] = read.GetValueOrDefault(name) + n;
            }
        }

        if (guard is not null)
        {
            foreach (var local in JavaRewrite.PostOrder(guard).OfType<JLocal>())
            {
                read[local.Name] = read.GetValueOrDefault(local.Name) + 1;
            }
        }

        // A component's variables live in the arm: one the method mentions outside it is a variable of its own.
        var inArm = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in scope ?? body)
        {
            foreach (var (name, n) in JavaTree.CountLocals(item))
            {
                inArm[name] = inArm.GetValueOrDefault(name) + n;
            }
        }

        JCaseLabel? Label(Node node)
        {
            // Inside javac's try the stack values an accessor's result passes through are its own, whatever else the
            // method calls by those names.
            if (!ReferenceEquals(node, root)
                && node.Variables.Any(name => !(node.InTry && node.Stack.Contains(name)) && counts.GetValueOrDefault(name) > inArm.GetValueOrDefault(name)))
            {
                return null;
            }

            var readers = node.Variables.Where(read.ContainsKey).Distinct().ToList();
            if (node.Type is null || readers.Count > 1 || (node.Components is not null && readers.Count > 0))
            {
                return null;
            }

            if (node.Components is { } parts)
            {
                // Every component, or it is not a record pattern: accessors called outside javac's try say only that
                // when they are all of them.
                if (node.Type is not ['L', .. var named, ';'] || arity?.Invoke(named) is not { } count
                    ? parts.Any(p => !p.InTry)
                    : count != parts.Count)
                {
                    return null;
                }

                var labels = parts.Select(Label).ToList();
                return labels.Any(l => l is null) ? null : new JCaseLabel { Type = node.Type, Components = labels! };
            }

            // The variable the body reads; none read, javac's own for the pattern, or one made up.
            var variable = readers is [var one] ? new JLocal(one, node.Type)
                : ReferenceEquals(node, root) && binding is not null ? binding with { LocalType = node.Type }
                : Fresh(node.Type, fresh);
            return new JCaseLabel { Type = node.Type, Binding = variable, Inferred = !ReferenceEquals(node, root) && node.Tested is null && node.Static is ['L' or '[', ..] };
        }

        if (Label(root) is not { } label)
        {
            return null;
        }

        taken.UnionWith(fresh);

        return new Matched(label with { Guard = guard }, rest, root, held);
    }

    /// <summary>A step that builds the pattern: an accessor call on a record's variable, a copy, a cast after its test, a nested type test.</summary>
    /// <param name="tests">An <c>instanceof</c> a register machine stored before it branched on it, by the boolean's name.</param>
    private static bool PatternStep(Step step, Dictionary<string, Node> held, bool records, Dictionary<string, (string Operand, string Type)> tests)
    {
        switch (step)
        {
            case { Target: { } flag, Value: JInstanceOf { Operand: JLocal tested, TestedType: var checkedType } } when held.ContainsKey(tested.Name):
                tests[flag.Name] = (tested.Name, checkedType);
                return true;
            case { Target: { } to, Value: JCall { Receiver: JLocal owner, Args.Count: 0, Descriptor: ['(', ')', .. var returned] } call }
                when records && returned != "V" && held.TryGetValue(owner.Name, out var record) && record.Type is ['L', .. var named, ';'] && named == call.Owner:
            {
                var component = new Node { Static = returned, InTry = step.InTry };
                component.Variables.Add(to.Name);
                if (to.Kind == JLocalKind.Stack)
                {
                    component.Stack.Add(to.Name);
                }

                (record.Components ??= []).Add(component);
                held[to.Name] = component;
                return true;
            }

            // A copy into a variable — not into the stack value that carries the switch's result out of the arm.
            case { Target: { Kind: not JLocalKind.Stack } to, Value: JLocal from } when held.TryGetValue(from.Name, out var same):
                same.Variables.Add(to.Name);
                held[to.Name] = same;
                return true;
            case { Target: { } to, Value: JCast { Operand: JLocal from, CastType: var cast } } when held.TryGetValue(from.Name, out var tested) && tested.Tested == cast:
                tested.Variables.Add(to.Name);
                held[to.Name] = tested;
                return true;
            case { Target: null, Value: var test } when (InstanceTest(test) ?? StoredTest(test, tests)) is var (operand, testedType) && held.TryGetValue(operand, out var node)
                                                    && node.Tested is null && node.Components is null:
                node.Tested = testedType;
                return true;
            default:
                return false;
        }
    }

    /// <summary>A branch on a boolean that holds an <c>instanceof</c>: <c>flag</c>, or <c>flag != 0</c>.</summary>
    private static (string Operand, string Type)? StoredTest(IrExpr condition, Dictionary<string, (string Operand, string Type)> tests) => condition switch
    {
        JLocal flag when tests.TryGetValue(flag.Name, out var test) => test,
        IrCondition { Cc: IrCondCode.NotEqual, Left: JLocal flag, Right: JConst { Value: 0 or false } } when tests.TryGetValue(flag.Name, out var test) => test,
        _ => null,
    };

    /// <summary><c>x instanceof T</c> as a condition, however the lifter spelled it.</summary>
    private static (string Operand, string Type)? InstanceTest(IrExpr condition) => condition switch
    {
        JInstanceOf { Operand: JLocal x, TestedType: var t } => (x.Name, t),
        IrCondition { Cc: IrCondCode.NotEqual, Left: JInstanceOf { Operand: JLocal x, TestedType: var t }, Right: JConst { Value: 0 or false } } => (x.Name, t),
        _ => null,
    };

    /// <summary>
    /// An arm's leading stores and no-match tests as a list, and what follows them. A no-match is an <c>if</c> with it on
    /// one side, or after an <c>if</c> the arm's code runs inside of; the accessor try is opened, its stores marked as in it.
    /// </summary>
    private static (List<Step> Steps, List<CStmt> Remaining)? Flatten(List<CStmt> body, int next, ulong dispatch, IReadOnlySet<ulong> covered)
    {
        var steps = new List<Step>();
        var queue = new List<(CStmt Item, bool InTry)>(body.Where(x => !JavaTree.IsEmpty(x)).Select(x => (x, false)));

        // Whether running off the end of what is queued is a no-match: inside an if whose failure was one.
        bool endIsNoMatch = false;
        while (queue.Count > 0)
        {
            var (item, inTry) = queue[0];
            List<CStmt>? inner = null;
            bool innerTry = inTry;
            switch (item)
            {
                // A register handed to itself says nothing.
                case CRaw { Statement: IrAssign { Dst: JLocal self, Src: JLocal same } } when self.Name == same.Name:
                    break;
                case CRaw { Statement: IrAssign { Dst: JLocal to, Src: var value } assign }:
                    steps.Add(new Step(to, value, inTry || covered.Contains(assign.Va), item));
                    break;
                case JTry { Finally: null, Resources.Count: 0, Catches: [{ CatchType: null or "java/lang/Throwable", Body: var handler }] } attempt
                    when ThrowsMatchException(JavaTree.Items(handler).Where(x => !JavaTree.IsEmpty(x)).ToList()):
                    inner = [.. JavaTree.Items(attempt.Body)];
                    innerTry = true;
                    break;
                case CIf { Condition: var test, Then: var then, Else: null } when IsNoMatch(then, next, dispatch):
                    steps.Add(new Step(null, JavaShaping.Not(test), inTry, null));
                    break;
                case CIf { Condition: var test, Then: var then, Else: { } otherwise } when IsNoMatch(then, next, dispatch):
                    steps.Add(new Step(null, JavaShaping.Not(test), inTry, null));
                    inner = [.. JavaTree.Items(otherwise)];
                    break;
                case CIf { Condition: var test, Then: var then, Else: { } otherwise } when IsNoMatch(otherwise, next, dispatch):
                    steps.Add(new Step(null, test, inTry, null));
                    inner = [.. JavaTree.Items(then)];
                    break;
                case CIf { Condition: var test, Then: var then, Else: null } when (queue.Count == 2 && IsNoMatch(queue[1].Item, next, dispatch)) || (queue.Count == 1 && endIsNoMatch):
                    steps.Add(new Step(null, test, inTry, null));
                    if (queue.Count == 2)
                    {
                        queue.RemoveAt(1);
                    }

                    endIsNoMatch = true;
                    inner = [.. JavaTree.Items(then)];
                    break;
                default:
                    return (steps, queue.Select(q => q.Item).ToList());
            }

            queue.RemoveAt(0);
            if (inner is not null)
            {
                queue.InsertRange(0, inner.Where(x => !JavaTree.IsEmpty(x)).Select(x => (x, innerTry)));
            }
        }

        return (steps, []);
    }

    /// <summary>
    /// Code that only throws a new failed-match exception: <c>throw new MatchException(…)</c>, or — as a register machine
    /// writes it before the temporaries fold — the exception, its message, then the throw, each in a variable.
    /// </summary>
    private static bool ThrowsMatchException(List<CStmt> body)
    {
        if (body.Count == 0 || body[^1] is not CRaw { Statement: JThrow { Exception: var thrown } }
            || body.SkipLast(1).Any(x => x is not CRaw { Statement: IrAssign { Dst: JLocal } }))
        {
            return false;
        }

        var created = thrown as JNew;
        if (thrown is JLocal held)
        {
            created = body.SkipLast(1).Select(x => ((CRaw)x).Statement).OfType<IrAssign>().LastOrDefault(a => a.Dst is JLocal l && l.Name == held.Name)?.Src as JNew;
        }

        return created is not null && IsMatchException(created.Owner);
    }

    /// <summary>What a failed match throws: <c>MatchException</c>, or the <c>RuntimeException</c> D8 writes for it below Android 14.</summary>
    private static bool IsMatchException(string type) => type is "java/lang/MatchException" or "java/lang/RuntimeException";

    /// <summary>A variable renamed where it is read and where it is written.</summary>
    private static CStmt Renamed(CStmt body, string from, string to, Func<JLocal, JExpr?> swap)
        => JavaTree.Rewrite(JavaTree.ReplaceLocals(body, swap), statement => statement is CRaw { Statement: IrAssign { Dst: JLocal target } assign } raw && target.Name == from
            ? raw with { Statement = assign with { Dst = target with { Name = to } } }
            : statement);

    /// <summary>The casts of the selector to the pattern's type, when they are every mention of it in the arm; null otherwise.</summary>
    private static HashSet<JCast>? CastsOnly(List<CStmt> body, IrExpr selector, string type)
    {
        if (selector is not JLocal value)
        {
            return null;
        }

        var casts = new HashSet<JCast>(ReferenceEqualityComparer.Instance);
        int mentions = 0;
        foreach (var node in body.SelectMany(JavaTree.Descendants))
        {
            foreach (var e in JavaTree.Expressions(node).SelectMany(JavaRewrite.PostOrder))
            {
                if (e is JCast { Operand: JLocal operand } cast && operand.Name == value.Name && cast.CastType == type)
                {
                    casts.Add(cast);
                }
                else if (e is JLocal l && l.Name == value.Name)
                {
                    mentions++;
                }
            }
        }

        return mentions == casts.Count ? casts : null;
    }

    /// <summary>The arm's first statement when it gives the selector to a variable — cast to the pattern's type or, when it already is one, as it is.</summary>
    private static JLocal? Binding(List<CStmt> body, IrExpr selector)
    {
        if (body.Count == 0 || body[0] is not CRaw { Statement: IrAssign { Dst: JLocal variable, Src: var value } })
        {
            return null;
        }

        var given = value is JCast cast ? cast.Operand : value;
        return JavaEquality.Same(given, selector) || (selector is JCall { Receiver: null, Name: "valueOf", Args: [var unboxed] } && JavaEquality.Same(given, unboxed))
            ? variable
            : null;
    }

    private static bool IsNoMatch(CStmt statement, int next, ulong dispatch)
        => JavaTree.Items(statement).Where(x => !JavaTree.IsEmpty(x)).ToList() is [CRaw { Statement: JNoMatch { Next: var n, Dispatch: var d } }] && n == next && d == dispatch;

    /// <summary>
    /// A name clear of every other: for a pattern whose variable the arm never reads, its type's, lower-cased; for a
    /// variable renamed, its own with a number.
    /// </summary>
    private static JLocal Fresh(string descriptorOrName, HashSet<string> taken)
    {
        string simple = descriptorOrName.TrimStart('[');
        if (simple.StartsWith('L') && simple.EndsWith(';'))
        {
            simple = simple[1..^1];
        }

        simple = simple[(Math.Max(simple.LastIndexOf('/'), simple.LastIndexOf('$')) + 1)..];
        string stem = simple.Length == 0 ? "value" : char.ToLowerInvariant(simple[0]) + simple[1..];
        string name = stem;
        for (int n = 2; taken.Contains(name) || JavaNames.IsKeyword(name); n++)
        {
            name = stem + n;
        }

        taken.Add(name);
        return new JLocal(name, descriptorOrName.EndsWith(';') ? descriptorOrName : null, JLocalKind.Local);
    }
}
