using System.Globalization;
using System.Text;
using Spydate.Core.Jvm;
using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Structuring;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// Prints a structured method body — and a whole class around such bodies — as Java-shaped pseudo-code: real
/// Java syntax for everything the structure expressed, <c>goto L0012;</c> and a label for the edges it did not,
/// and the compiler's own shapes (a <c>finally</c> copied onto each exit, a switch on a string's hash) left as
/// they are rather than guessed back into sugar.
/// </summary>
internal sealed partial class JavaEmitter
{
    private const string Indent = "    ";

    /// <summary>A tree deeper than this prints an ellipsis; the lifter keeps real ones far shallower.</summary>
    private const int MaxPrintDepth = 200;

    private readonly JavaNaming _naming;
    private readonly StringBuilder _sb = new();
    private readonly HashSet<ulong> _targets = [];
    private readonly HashSet<ulong> _printedLabels = [];
    private readonly Dictionary<int, string> _labelNames = [];
    private JavaDeclarations? _declarations;
    private int _breakable;

    /// <summary>The indentation level of the statement being printed: where a lambda's or anonymous class's lines go.</summary>
    private int _level;
    private int _loop;
    private LiftedMethod? _lifted;
    private JvmMethod? _method;
    private HashSet<string> _foldedDeclarations = [];
    private string? _returnType;
    private int _printDepth;

    public JavaEmitter(JavaNaming naming) => _naming = naming;

    public StringBuilder Output => _sb;

    /// <summary>Writes a method's body at <paramref name="level"/>, up to its closing brace; the header line carries the opening one.</summary>
    /// <param name="structured">
    /// True for a body the Java structurer built — no gotos — whose locals are declared where they are used;
    /// false for the native structurer's, where a goto may jump past any scope and every local is declared at the top.
    /// </param>
    public void Body(JvmMethod method, LiftedMethod lifted, CStmt body, int level, IReadOnlyDictionary<ulong, IrReturn>? returns = null, bool structured = false)
    {
        _method = method;
        _lifted = lifted;
        _returnType = JCall.ReturnType(method.Descriptor);
        if (returns is { Count: > 0 })
        {
            body = InlineReturns(body, returns);
        }

        _targets.Clear();
        _printedLabels.Clear();
        foreach (var jump in Descendants(body).OfType<CGoto>())
        {
            _targets.Add(jump.Va);
        }

        body = DropUnreachable(body);

        body = Tidy(body, method);
        NameLabels(body);

        _declarations = null;
        if (structured)
        {
            // Each local where it is used: see JavaDeclarations.
            body = CatchVariables(body, lifted);
            body = JavaDeclarations.Blocked(body);
            var names = lifted.Locals.Declared.Keys.Where(n => !lifted.Locals.IsCaught(n)).ToHashSet(StringComparer.Ordinal);

            // A pattern's variable is declared by its case label.
            names.ExceptWith(PatternBindings(body));

            // A local class is declared like a variable: just before the first statement that creates it.
            foreach (var created in Descendants(body).OfType<CRaw>().SelectMany(r => JavaRewrite.Evaluated(r.Statement)).SelectMany(JavaRewrite.PostOrder).OfType<JNew>())
            {
                if (_naming.Scope?.IsLocalClass(created.Owner) == true)
                {
                    names.Add(JavaDeclarations.LocalClassPrefix + created.Owner);
                }
            }
            _declarations = JavaDeclarations.Place(body as CSeq ?? new CSeq([body]), names);
        }
        else
        {
            // Declarations: every local the body uses that is not a parameter, a catch variable, or declared at its
            // first assignment — which is folded in when that assignment is at the top level of the body and comes
            // before any other mention of the name.
            _foldedDeclarations = FoldableDeclarations(body, lifted);
            var mentioned = Mentioned(body);
            foreach (var (name, type) in lifted.Locals.Declared.OrderBy(d => d.Key, StringComparer.Ordinal))
            {
                if (!_foldedDeclarations.Contains(name) && !lifted.Locals.IsCaught(name) && mentioned.Contains(name))
                {
                    Line(level + 1, $"{_naming.Type(type)} {name};");
                }
            }
        }

        foreach (string warning in lifted.Function.Warnings.Distinct().Take(8))
        {
            Line(level + 1, $"// warning: {warning}");
        }

        Write(body, level + 1, topLevel: true);
        Line(level, "}");
    }

    // --- statements ---------------------------------------------------------------------

    private void Write(CStmt statement, int level, bool topLevel = false)
    {
        int outer = _level;
        _level = level;
        try
        {
            WriteAt(statement, level, topLevel);
        }
        finally
        {
            _level = outer;
        }
    }

    private void WriteAt(CStmt statement, int level, bool topLevel)
    {
        switch (statement)
        {
            case CSeq seq:
                for (int i = 0; i < seq.Items.Count; i++)
                {
                    if (_declarations is not null)
                    {
                        foreach (string name in _declarations.Before(seq, i))
                        {
                            if (name.StartsWith(JavaDeclarations.LocalClassPrefix, StringComparison.Ordinal))
                            {
                                if (_naming.Scope?.LocalClass(name[JavaDeclarations.LocalClassPrefix.Length..], level) is { } declaration)
                                {
                                    _sb.Append(declaration);
                                }

                                continue;
                            }

                            Line(level, $"{DeclaredType(name, null)} {name};");
                        }
                    }

                    Write(seq.Items[i], level, topLevel);
                }

                break;
            case CLabel label:
                // Once: a try that starts at a label repeats it inside its own region, at a deeper level.
                if (_targets.Contains(label.Va) && _printedLabels.Add(label.Va))
                {
                    Line(Math.Max(0, level - 1), $"{LabelName(label.Va)}:");
                }

                break;
            case CGoto jump:
                Line(level, $"goto {LabelName(jump.Va)};");
                break;
            case CBreak:
                Line(level, "break;");
                break;
            case CContinue:
                Line(level, "continue;");
                break;
            case CIf conditional:
                WriteIf(conditional, level, "if");
                break;
            case CLoop loop:
                WriteLoop(loop, level);
                break;
            case CSwitch dispatch:
                WriteSwitch(dispatch, level);
                break;
            case JTry attempt:
                WriteTry(attempt, level);
                break;
            case CRaw { Statement: JRegion region }:
                Write(region.Body, level);
                break;
            case CRaw raw:
                WriteStatement(raw.Statement, level, topLevel);
                break;
            case JBlock block:
                Line(level, $"{LabelPrefix(block.Label)}{{");
                Write(block.Body, level + 1);
                Line(level, "}");
                break;
            case JLoop loop:
                WriteLoop(loop, level);
                break;
            case JSwitch dispatch:
                WriteSwitch(dispatch, level);
                break;
            case JBreak jump:
                Line(level, jump.Label == _breakable ? "break;" : $"break {LabelText(jump.Label)};");
                break;
            case JContinue jump:
                Line(level, jump.Label == _loop ? "continue;" : $"continue {LabelText(jump.Label)};");
                break;
            case JExit exit:
                Line(level, $"goto {LabelName(exit.Target)}; // leaves a region that could not be placed");
                break;
            case JAssert assertion:
                Line(level, assertion.Message is null
                    ? $"assert {Condition(assertion.Condition)};"
                    : $"assert {Condition(assertion.Condition)} : {Expr(assertion.Message)};");
                break;
            case JSynchronized locked:
                Line(level, $"synchronized ({Expr(locked.Lock)}) {{");
                Write(locked.Body, level + 1);
                Line(level, "}");
                break;
        }
    }

    /// <summary>
    /// Gives a name to every label some jump has to spell out — a <c>break</c> that does not leave the innermost
    /// loop or switch, a <c>continue</c> of an outer loop — in the order the labelled statements come.
    /// </summary>
    private void NameLabels(CStmt body)
    {
        _labelNames.Clear();
        var needed = new HashSet<int>();
        Collect(body, 0, 0);
        foreach (var node in JavaTree.Descendants(body))
        {
            int label = node switch
            {
                JBlock b => b.Label,
                JLoop l => l.Label,
                JSwitch s => s.Label,
                _ => 0,
            };

            if (label != 0 && needed.Contains(label))
            {
                _labelNames[label] = $"label{_labelNames.Count + 1}";
            }
        }

        void Collect(CStmt statement, int breakable, int loop)
        {
            switch (statement)
            {
                case JBreak b when b.Label != breakable:
                    needed.Add(b.Label);
                    return;
                case JContinue c when c.Label != loop:
                    needed.Add(c.Label);
                    return;
                case JLoop l:
                    Collect(l.Body, l.Label, l.Label);
                    return;
                case JSwitch s:
                    foreach (var arm in s.Cases)
                    {
                        Collect(arm.Body, s.Label, loop);
                    }

                    return;
                default:
                    foreach (var child in JavaTree.Children(statement))
                    {
                        Collect(child, breakable, loop);
                    }

                    return;
            }
        }
    }

    private string LabelPrefix(int label) => _labelNames.TryGetValue(label, out var name) ? $"{name}: " : string.Empty;

    private string LabelText(int label) => _labelNames.TryGetValue(label, out var name) ? name : $"label{label}";

    private void WriteLoop(JLoop loop, int level)
    {
        var (breakable, inner) = (_breakable, _loop);
        (_breakable, _loop) = (loop.Label, loop.Label);
        string prefix = LabelPrefix(loop.Label);
        switch (loop.Kind)
        {
            case CLoopKind.While when loop.ForEach is { } each:
                Line(level, $"{prefix}for ({ForEachVariable(loop, each.Variable)} : {Expr(each.Source)}) {{");
                Write(loop.Body, level + 1);
                Line(level, "}");
                break;
            case CLoopKind.While when loop.Update is not null:
                string init = loop.Init is IrAssign assign ? ForInit(loop, assign) : string.Empty;
                string update = loop.Update is IrAssign step ? Assignment(step, topLevel: false) : string.Empty;
                Line(level, $"{prefix}for ({init}; {Condition(loop.Condition!)}; {update}) {{");
                Write(loop.Body, level + 1);
                Line(level, "}");
                break;
            case CLoopKind.While:
                Line(level, $"{prefix}while ({Condition(loop.Condition!)}) {{");
                Write(loop.Body, level + 1);
                Line(level, "}");
                break;
            case CLoopKind.DoWhile:
                Line(level, $"{prefix}do {{");
                Write(loop.Body, level + 1);
                Line(level, $"}} while ({Condition(loop.Condition!)});");
                break;
            default:
                Line(level, $"{prefix}while (true) {{");
                Write(loop.Body, level + 1);
                Line(level, "}");
                break;
        }

        (_breakable, _loop) = (breakable, inner);
    }

    /// <summary>
    /// A for-each loop's variable, declared by the loop — or, when the method uses the name outside the loop too, a
    /// variable of its own that the body copies into the method's one first.
    /// </summary>
    private string ForEachVariable(JLoop loop, JLocal variable)
    {
        string type = DeclaredType(variable.Name, variable.Type);
        return _declarations?.Declares(loop) != false ? $"{type} {variable.Name}" : $"{type} {variable.Name}";
    }

    /// <summary>A <c>for</c>'s initialiser, with the declaration when the variable lives only in the loop.</summary>
    private string ForInit(JLoop loop, IrAssign init)
    {
        if (init.Dst is JLocal local && _declarations?.Declares(loop) == true)
        {
            return $"{DeclaredType(local.Name, local.Type)} {local.Name} = {Value(init.Src, local)}";
        }

        return Assignment(init, topLevel: false);
    }

    /// <summary>The variables the pattern switches in a body declare in their labels.</summary>
    private static IEnumerable<string> PatternBindings(CStmt body)
    {
        foreach (var node in Descendants(body))
        {
            var patterns = node switch
            {
                JSwitch { Patterns: { } own } => own.Values,
                CRaw raw => JavaRewrite.Evaluated(raw.Statement).SelectMany(JavaRewrite.PostOrder).OfType<JSwitchExpr>().SelectMany(e => e.Patterns?.Values ?? []),
                _ => [],
            };

            foreach (var pattern in patterns)
            {
                foreach (var binding in Bound(pattern))
                {
                    yield return binding;
                }
            }
        }

        // Nested record patterns bind at every depth.
        static IEnumerable<string> Bound(JCaseLabel pattern)
        {
            if (pattern.Binding is { } binding)
            {
                yield return binding.Name;
            }

            foreach (var part in pattern.Components ?? [])
            {
                foreach (string name in Bound(part))
                {
                    yield return name;
                }
            }
        }
    }

    /// <summary>
    /// A pattern switch's arm label, without its colon or arrow: <c>case Integer i when i &gt; 3</c>, <c>case RED</c>,
    /// <c>case null, default</c>, or — for the default arm that is the last, unconditional pattern — that pattern.
    /// </summary>
    private string PatternArmLabel(IReadOnlyList<int> armLabels, int?[]? values, IReadOnlyDictionary<int, JCaseLabel> patterns)
    {
        var resolved = armLabels.Select(i => values is not null && i < values.Length ? values[i] : i).ToList();
        var parts = resolved.OfType<int>().Order()
            .Select(v => v == -1 ? "null" : patterns.TryGetValue(v, out var pattern) ? PatternText(pattern) : v.ToString(CultureInfo.InvariantCulture))
            .ToList();
        if (resolved.Contains(null))
        {
            if (patterns.ContainsKey(JavaPatterns.NullDefaultKey) && !parts.Contains("null"))
            {
                parts.Insert(0, "null");
            }

            if (patterns.TryGetValue(JavaPatterns.TotalKey, out var total))
            {
                parts.Add(PatternText(total));
            }
            else if (parts.Count == 0)
            {
                return "default";
            }
            else
            {
                parts.Add("default");
            }
        }

        return "case " + string.Join(", ", parts);
    }

    private string PatternText(JCaseLabel label) => label switch
    {
        { Constant.Value: string name, EnumConstant: true } => name,
        { Constant: { } constant } => Expr(constant),
        { Type: { } type, Components: { } parts } => $"{_naming.Type(type)}({string.Join(", ", parts.Select(PatternText))})" + Guard(label),
        { Inferred: true, Binding: { } inferred } => $"var {inferred.Name}" + Guard(label),
        { Type: { } type, Binding: { } binding } => $"{_naming.Type(type)} {binding.Name}" + Guard(label),
        _ => "?",
    };

    private string Guard(JCaseLabel label) => label.Guard is { } guard ? $" when {Condition(guard)}" : string.Empty;

    private void WriteSwitch(JSwitch dispatch, int level)
    {
        var breakable = _breakable;
        _breakable = dispatch.Label;
        var values = _lifted?.SwitchValues.GetValueOrDefault(dispatch.Va);
        var switched = dispatch.Value;
        var names = dispatch.Names;
        if (names is null && _naming.Scope?.EnumSwitch(dispatch.Value) is { } enumSwitch)
        {
            switched = enumSwitch.Value;
            names = enumSwitch.Names;
        }

        Line(level, $"{LabelPrefix(dispatch.Label)}switch ({Expr(switched)}) {{");
        foreach (var arm in dispatch.Cases)
        {
            var labels = dispatch.Patterns is { } patterns ? [PatternArmLabel(arm.Labels, values, patterns) + ":"] : CaseLabels(arm, values, names);
            foreach (string label in labels)
            {
                Line(level + 1, label);
            }

            // An arm that declares a local gets braces: a switch's arms share one scope, and two arms may each
            // declare a variable of the same name.
            if (arm.Body is CSeq scope && _declarations?.HasDeclarations(scope) == true)
            {
                Line(level + 1, "{");
                Write(arm.Body, level + 2);
                Line(level + 1, "}");
            }
            else
            {
                Write(arm.Body, level + 2);
            }
        }

        Line(level, "}");
        _breakable = breakable;
    }

    /// <summary>
    /// A switch expression, its arms at the statement's level + 1: <c>case 1, 2 -&gt; 10;</c> for an arm that is only its
    /// value, <c>default -&gt; throw …;</c> for one that only throws, a block ending in <c>yield</c> otherwise.
    /// </summary>
    private string SwitchExpressionText(JSwitchExpr expression)
    {
        int level = _level;
        var values = _lifted?.SwitchValues.GetValueOrDefault(expression.Va);
        var switched = expression.Value;
        var names = expression.Names;
        if (names is null && _naming.Scope?.EnumSwitch(expression.Value) is { } enumSwitch)
        {
            switched = enumSwitch.Value;
            names = enumSwitch.Names;
        }

        var sb = new StringBuilder();
        sb.Append("switch (").Append(Expr(switched)).Append(") {\n");
        foreach (var arm in expression.Arms)
        {
            string label;
            if (expression.Patterns is { } patterns)
            {
                label = PatternArmLabel(arm.Labels, values, patterns);
            }
            else
            {
                var labels = CaseLabels(new CCase(arm.Labels, CSeq.Empty), values, names).ToList();
                label = labels is ["default:"] ? "default" : "case " + string.Join(", ", labels.Select(l => l["case ".Length..^1]));
            }

            Pad(sb, level + 1).Append(label).Append(" -> ");
            var body = arm.Body.Items.Where(i => !JavaTree.IsEmpty(i)).ToList();
            if (body is [CRaw { Statement: JYield only }])
            {
                sb.Append(Expr(Fit(Typed(only.Value, expression.Type), expression.Type))).Append(";\n");
            }
            else if (body is [CRaw { Statement: JThrow thrown }])
            {
                sb.Append("throw ").Append(Expr(thrown.Exception)).Append(";\n");
            }
            else
            {
                sb.Append("{\n");
                sb.Append(Captured(() => Write(arm.Body, level + 2)));
                Pad(sb, level + 1).Append("}\n");
            }
        }

        Pad(sb, level).Append('}');
        return sb.ToString();
    }

    /// <summary>What writing does to the output, taken back out of it as text — for statements inside an expression.</summary>
    private string Captured(Action write)
    {
        int start = _sb.Length;
        int level = _level;
        write();
        _level = level;
        string text = _sb.ToString(start, _sb.Length - start);
        _sb.Length = start;
        return text;
    }

    private static StringBuilder Pad(StringBuilder sb, int level)
    {
        for (int i = 0; i < level; i++)
        {
            sb.Append(Indent);
        }

        return sb;
    }

    /// <summary>An arm's labels by value; the arm that has <c>default</c> needs no others — they go there anyway.</summary>
    private static IEnumerable<string> CaseLabels(CCase arm, int?[]? values, IReadOnlyDictionary<int, string>? names = null)
    {
        var resolved = arm.Labels.Select(i => values is not null && i < values.Length ? values[i] : i).ToList();
        if (values is not null && resolved.Contains(null))
        {
            return ["default:"];
        }

        // A case the names do not cover can never match what the switch is really on (a string's hash that no literal had).
        return resolved.OrderBy(v => v)
            .Where(v => names is null || names.ContainsKey(v!.Value))
            .Select(v => $"case {(names is not null ? names[v!.Value] : v!.Value.ToString(CultureInfo.InvariantCulture))}:");
    }

    private void WriteIf(CIf conditional, int level, string keyword)
    {
        Line(level, $"{keyword} ({Condition(conditional.Condition)}) {{");
        Write(conditional.Then, level + 1);
        switch (conditional.Else)
        {
            case null:
                Line(level, "}");
                break;
            case CIf elseIf:
                WriteIf(elseIf, level, "} else if");
                break;
            case CSeq { Items: [CIf only] }:
                WriteIf(only, level, "} else if");
                break;
            default:
                Line(level, "} else {");
                Write(conditional.Else, level + 1);
                Line(level, "}");
                break;
        }
    }

    private void WriteLoop(CLoop loop, int level)
    {
        switch (loop.Kind)
        {
            case CLoopKind.While:
                Line(level, $"while ({Condition(loop.Condition!)}) {{");
                Write(loop.Body, level + 1);
                Line(level, "}");
                break;
            case CLoopKind.DoWhile:
                Line(level, "do {");
                Write(loop.Body, level + 1);
                Line(level, $"}} while ({Condition(loop.Condition!)});");
                break;
            default:
                Line(level, "while (true) {");
                Write(loop.Body, level + 1);
                Line(level, "}");
                break;
        }
    }

    private void WriteSwitch(CSwitch dispatch, int level)
    {
        var values = _lifted?.SwitchValues.GetValueOrDefault(dispatch.Va);
        Line(level, $"switch ({Expr(dispatch.Value)}) {{");
        foreach (var arm in dispatch.Cases)
        {
            var labels = arm.Labels
                .Select(i => values is not null && i < values.Length ? values[i] : i)
                .OrderBy(v => v is null ? 1 : 0).ThenBy(v => v)
                .Select(v => v is null ? "default:" : $"case {v.Value.ToString(CultureInfo.InvariantCulture)}:");
            foreach (string label in labels)
            {
                Line(level + 1, label);
            }

            Write(arm.Body, level + 2);
        }

        Line(level, "}");
    }

    private void WriteTry(JTry attempt, int level)
    {
        if (attempt.Resources.Count > 0)
        {
            var resources = attempt.Resources.Select(r => $"{DeclaredType(r.Variable.Name, r.Variable.Type)} {r.Variable.Name} = {Value(r.Init, r.Variable)}");
            Line(level, $"try ({string.Join("; ", resources)}) {{");
        }
        else
        {
            Line(level, "try {");
        }

        Write(attempt.Body, level + 1);
        foreach (var handler in attempt.Catches)
        {
            string type = handler.CatchType is null ? "Throwable" : string.Join(" | ", handler.Alternatives.Prepend(handler.CatchType).Select(_naming.ClassName));
            string variable = handler.Variable ?? "ignored";
            string note = handler.CatchType is null ? " // any: a finally, or a synchronized block's release" : string.Empty;
            Line(level, $"}} catch ({type} {variable}) {{{note}");
            Write(handler.Body, level + 1);
        }

        if (attempt.Finally is { } always)
        {
            Line(level, "} finally {");
            Write(always, level + 1);
        }

        Line(level, "}");
    }

    private void WriteStatement(IrStmt statement, int level, bool topLevel)
    {
        switch (statement)
        {
            case IrNop:
                return;
            case IrComment comment:
                Line(level, $"// {comment.Text}");
                return;
            case IrAssign { Dst: JLocal to, Src: JLocal from } when to.Name == from.Name:
                return;   // a slot handed to itself where one name covers several of its values
            case IrAssign assign:
                Line(level, Assignment(assign, topLevel) + ";");
                return;
            case JExprStmt expression:
                Line(level, $"{ExpressionStatement(expression.Expression)};");
                return;
            case IrReturn { Value: null }:
                Line(level, "return;");
                return;
            case IrReturn { Value: { } value }:
                Line(level, $"return {ReturnValue(value)};");
                return;
            case JThrow thrown:
                Line(level, $"throw {Expr(Thrown(thrown.Exception))};");
                return;
            case JYield yielded:
                Line(level, $"yield {Expr(yielded.Value)};");
                return;
            case JNoMatch miss:
                // Only when the switch around it could not be rebuilt: say what the bytecode does here.
                Line(level, $"// the guard failed: matching goes on from case label {miss.Next.ToString(CultureInfo.InvariantCulture)}");
                return;
            case JMonitor monitor:
                Line(level, monitor.Enter
                    ? $"monitorenter({Expr(monitor.Lock)}); // synchronized ({Expr(monitor.Lock)}) begins"
                    : $"monitorexit({Expr(monitor.Lock)});");
                return;
            case IrGoto jump:
                Line(level, $"goto {LabelName(jump.TargetVa)};");
                return;
            case IrBranch branch:
                Line(level, $"if ({Condition(branch.Condition)}) goto {LabelName(branch.TargetVa)};");
                return;
            case IrSwitch dispatch:
                Line(level, $"switch ({Expr(dispatch.Value)}) // unstructured");
                return;
            default:
                Line(level, $"// {statement}");
                return;
        }
    }

    /// <summary><c>x = e</c>, <c>x += e</c>, <c>x++</c>; with the declaration folded in at a local's first assignment.</summary>
    private string Assignment(IrAssign assign, bool topLevel)
    {
        string target = Expr(assign.Dst);
        var value = Fit(Typed(assign.Src, (assign.Dst as JExpr)?.Type), (assign.Dst as JExpr)?.Type);
        if (assign.Dst is JLocal local && (_declarations is null ? topLevel && _foldedDeclarations.Remove(local.Name) : _declarations.Declares(assign)))
        {
            return $"{DeclaredType(local.Name, local.Type)} {target} = {Value(assign.Src, assign.Dst)}";
        }

        // x = x + 1 reads as x++; x = x op y as x op= y.
        if (value is JBinary { Left: var left } binary && left.Equals(assign.Dst) && binary.Op is "+" or "-" or "*" or "/" or "%" or "&" or "|" or "^" or "<<" or ">>" or ">>>")
        {
            if (binary.Op is "+" or "-" && binary.Right is JConst { Value: 1 or 1L })
            {
                return $"{target}{binary.Op}{binary.Op}";
            }

            if (binary.Op == "+" && binary.Right is JConst { Value: int negative } && negative < 0 && negative != int.MinValue)
            {
                return negative == -1 ? $"{target}--" : $"{target} -= {-negative}";
            }

            return $"{target} {binary.Op}= {Expr(Fit(Typed(binary.Right, left.Type), left.Type), Precedence.Assignment + 1)}";
        }

        return $"{target} = {Value(assign.Src, assign.Dst)}";
    }

    private string ReturnValue(IrExpr value)
    {
        var fitted = Fit(Typed(value, _returnType), _returnType);
        string? generic = _method?.Signature is { } signature ? JavaGenerics.ReturnSignature(signature) : null;
        fitted = Generic(fitted, generic);
        return fitted is JNew created ? Created(created, generic) : Expr(fitted);
    }

    /// <summary>A local's declared type: its generic type when the class file gave one, else its descriptor's.</summary>
    private string DeclaredType(string name, string? fallback)
    {
        if (_lifted!.Locals.Signatures.TryGetValue(name, out var signature) && Descriptors.FieldSignature(signature, _naming.ClassName) is { } generic)
        {
            return generic;
        }

        string? descriptor = _lifted.Locals.Declared.GetValueOrDefault(name) ?? fallback;

        // A variable holding an anonymous class is declared as what the class extends or implements.
        if (descriptor is ['L', .. var internalName, ';'] && _naming.Scope?.AnonymousType(internalName) is { } supertype)
        {
            return supertype;
        }

        return _naming.Type(descriptor);
    }

    /// <summary>
    /// A value as it is stored into <paramref name="target"/>: typed and fitted to it, and a <c>new</c> of a generic
    /// class written with <c>&lt;&gt;</c> when the target's declared type has type arguments.
    /// </summary>
    private string Value(IrExpr value, IrExpr target)
    {
        string? type = (target as JExpr)?.Type;
        var fitted = Generic(Fit(Typed(value, type), type), target is JExpr declared ? DeclaredGeneric(declared) : null);
        return fitted is JNew created && target is JExpr expression && GenericOf(expression) is { } generic ? Created(created, generic) : Expr(fitted);
    }

    /// <summary><c>new T(…)</c>, or <c>new T&lt;&gt;(…)</c> when the value goes somewhere of a parameterized type and T is generic.</summary>
    private string Created(JNew created, string? targetGeneric)
    {
        if (_naming.Scope?.Anonymous(created, a => Arguments(a.Args, a.Descriptor, a.Owner, "<init>"), _level) is { } anonymous)
        {
            return anonymous;
        }

        var (outer, args, descriptor) = _naming.Scope?.Constructed(created) ?? (null, created.Args, created.Descriptor);
        bool diamond = targetGeneric is not null && JavaGenerics.IsParameterized(targetGeneric) && JavaGenerics.IsGenericClass(created.Owner, _naming.FindClass);
        string prefix = outer is null or JLocal { Kind: JLocalKind.This } ? string.Empty : $"{Expr(outer, Precedence.Primary)}.";
        string type = outer is null or JLocal { Kind: JLocalKind.This } ? _naming.ClassName(created.Owner) : SimpleClassName(created.Owner);
        return $"{prefix}new {type}{(diamond ? "<>" : string.Empty)}({Arguments(args, descriptor, created.Owner, "<init>")})";
    }

    /// <summary>A member class's own simple name, as <c>outer.new Inner()</c> writes it.</summary>
    private string SimpleClassName(string internalName)
    {
        if (_naming.GivenClassName(internalName) is { } given)
        {
            return given.Split('.')[^1];
        }

        int dollar = internalName.LastIndexOf('$');
        int slash = internalName.LastIndexOf('/');
        return internalName[(Math.Max(dollar, slash) + 1)..];
    }

    /// <summary>
    /// A lambda for the class writer: the synthetic method's body with its captured parameters already replaced,
    /// written as <c>x -&gt; value</c> when all it does is return a value or evaluate one expression, and as
    /// <c>(a, b) -&gt; { … }</c> otherwise, its lines at <paramref name="level"/> + 1.
    /// </summary>
    public string Lambda(JvmMethod method, LiftedMethod lifted, CStmt body, int level, IReadOnlyList<string> parameters)
    {
        _method = method;
        _lifted = lifted;
        _returnType = JCall.ReturnType(method.Descriptor);
        _level = level;
        string head = parameters.Count == 1 ? parameters[0] : $"({string.Join(", ", parameters)})";
        var items = JavaTree.Items(body).Where(i => !JavaTree.IsEmpty(i) && i is not CRaw { Statement: IrReturn { Value: null } }).ToList();
        switch (items)
        {
            case [CRaw { Statement: IrReturn { Value: { } value } }]:
                return $"{head} -> {ReturnValue(value)}";
            case [CRaw { Statement: JExprStmt expression }] when _returnType == "V":
                return $"{head} -> {ExpressionStatement(expression.Expression)}";
        }

        Body(method, lifted, body, level, returns: null, structured: true);
        return $"{head} -> {{\n{_sb.ToString().TrimEnd('\n')}";
    }

    /// <summary>
    /// An expression as a statement at <paramref name="level"/> would print it, for the class writer — a field's
    /// initialiser, taken from the constructor or static initialiser it was compiled into.
    /// </summary>
    public string Initializer(JvmMethod method, LiftedMethod lifted, IrExpr value, string? type, string? generic, int level)
    {
        _method = method;
        _lifted = lifted;
        _level = level;
        var fitted = Generic(Fit(Typed(value, type), type), generic);
        return fitted is JNew created ? Created(created, generic) : Expr(fitted);
    }

    /// <summary>
    /// A value going where the source's type is generic — a method's generic return, a field or local of a type
    /// variable or a parameterized type — as the source had to write it. javac erases the type: a cast the source
    /// wrote to <c>T</c> leaves nothing, one it wrote to <c>T[]</c> leaves a cast to <c>Object[]</c>, and it casts
    /// a generic call's result to the erasure too. Printed as they are, those do not compile; so the erasure's cast
    /// is a cast to the generic type, and a value not known to have that type is cast to it — an unchecked cast,
    /// which is what the source did.
    /// </summary>
    private IrExpr Generic(IrExpr value, string? expected)
    {
        Witness(value, expected);
        if (expected is null || value is not JExpr expression || expression is JConst)
        {
            return value;
        }

        bool variable = TypeVariable().IsMatch(expected);
        bool bare = expected.TrimStart('[').StartsWith('T');
        if (!variable && !JavaGenerics.IsParameterized(expected))
        {
            return value;
        }

        switch (expression)
        {
            case JConditional conditional:
                return conditional with { Then = (JExpr)Generic(conditional.Then, expected), Else = (JExpr)Generic(conditional.Else, expected) };
            case JCast cast when cast.CastType is ['L' or '[', ..]:
                return GenericType(cast.Operand) == expected ? cast.Operand : variable ? new JCast(expected, cast.Operand) : value;
        }

        if (expression.Type is not ['L' or '[', ..] || GenericType(expression) is var known && known is not null && JavaGenerics.Assignable(known, expected))
        {
            return value;
        }

        // A call's result the target types: inferred from where it goes, as the source's was — unless it is a JDK
        // method's that returns a class type and whose receiver's type arguments are unknown. A new is only ever its
        // own class, which a type variable needs a cast to.
        if ((expression is JDynamic || (expression is JNew && !bare)) && known is null)
        {
            return value;
        }

        if (expression is JCall call && known is null
            && (!variable || call.Receiver is null || _naming.FindClass(call.Owner) is not null || JCall.ReturnType(call.Descriptor) is "Ljava/lang/Object;" or "[Ljava/lang/Object;"
                || (GenericType(call.Receiver) is { } receiverType && JavaGenerics.IsParameterized(receiverType))))
        {
            return value;
        }

        // A parameterized type unlike the expected one of the same class had a cast; one of another class may be a subtype.
        if (!variable && (known is null || JavaGenerics.ClassOf(known) != JavaGenerics.ClassOf(expected)))
        {
            return value;
        }

        return new JCast(expected, expression);
    }

    /// <summary>Explicit type arguments for a generic static call heading a chain, by the call: see <see cref="JavaGenerics.Witness"/>.</summary>
    private readonly Dictionary<JCall, string> _witnesses = new(ReferenceEqualityComparer.Instance);

    private void Witness(IrExpr value, string? expected)
    {
        if (expected is null || !JavaGenerics.IsParameterized(expected) || value is not JCall last)
        {
            return;
        }

        var chain = new List<JCall>();
        for (JExpr? at = last; at is JCall call; at = call.Receiver)
        {
            chain.Insert(0, call);
        }

        if (JavaGenerics.Witness(chain, expected, _naming.FindClass) is { } arguments
            && arguments.Select(a => Descriptors.FieldSignature(a, _naming.ClassName)).ToList() is var written && written.All(w => w is not null))
        {
            _witnesses[chain[0]] = $"<{string.Join(", ", written)}>";
        }
    }

    /// <summary>
    /// What a method declared <c>throws E</c> throws: a value of <c>E</c>'s erasure was cast to <c>E</c> in the source —
    /// a cast javac writes nothing for — or Java would not let the method throw it.
    /// </summary>
    private JExpr Thrown(JExpr exception)
    {
        if (_method is not { Signature: { } signature } method || exception.Type is not { } type || GenericType(exception) is { } known && known.StartsWith('T'))
        {
            return exception;
        }

        int caret = signature.IndexOf('^');
        while (caret >= 0 && caret + 1 < signature.Length)
        {
            int end = signature.IndexOf(';', caret);
            if (end < 0)
            {
                break;
            }

            string thrown = signature[(caret + 1)..(end + 1)];
            if (thrown.StartsWith('T') && JavaGenerics.ErasureIn(thrown, method, _naming.Class) == type)
            {
                return new JCast(thrown, exception);
            }

            caret = signature.IndexOf('^', end);
        }

        return exception;
    }

    /// <summary>The generic type a store's target was declared with: a local's from the tables, a field's of this class from its signature.</summary>
    private string? DeclaredGeneric(JExpr target) => target switch
    {
        JLocal local => _lifted?.Locals.Signatures.GetValueOrDefault(local.Name),
        JField field => OwnFieldSignature(field),
        _ => null,
    };

    /// <summary>
    /// A field of this class's generic signature, where its type variables are this class's: a static field, or one
    /// of this instance's from an instance method or initialiser.
    /// </summary>
    private string? OwnFieldSignature(JField field)
    {
        if (field.Owner != _naming.Class.Name || _naming.Class.Fields.FirstOrDefault(f => f.Name == field.Name && f.Descriptor == field.FieldType) is not { Signature: { } signature } declared)
        {
            return null;
        }

        // Through another instance, the variables are that instance's arguments, not this class's.
        bool staticContext = _method is { } method && (method.Access & JvmAccess.Static) != 0 && method.Name != "<clinit>";
        return (declared.Access & JvmAccess.Static) != 0 || (!staticContext && field.Instance is JLocal { Kind: JLocalKind.This }) ? signature : null;
    }

    /// <summary>The generic type an expression is known to have: a local's or parameter's, this class's field's, a call's declared return.</summary>
    private string? GenericType(JExpr expression) => expression switch
    {
        JLocal { Kind: JLocalKind.This } => null,
        JCall call when JavaGenerics.JdkResult(call, GenericType, _naming.FindClass) is { } wildcard && wildcard.Contains('*') => wildcard,
        _ when _lifted is not null && _method is not null => JavaGenerics.InScopeOf(expression, _lifted, _method, _naming.Class, _naming.FindClass),
        _ => null,
    };

    /// <summary>The generic type of what an expression reads, when the class file says: a local's, a parameter's, a field's of this JAR.</summary>
    private string? GenericOf(JExpr expression) => _lifted is null ? null : JavaGenerics.GenericOf(expression, _lifted, _naming.FindClass);

    private static readonly Dictionary<string, (string Primitive, string Unbox)> Boxes = new(StringComparer.Ordinal)
    {
        ["java/lang/Integer"] = ("I", "intValue"),
        ["java/lang/Long"] = ("J", "longValue"),
        ["java/lang/Short"] = ("S", "shortValue"),
        ["java/lang/Byte"] = ("B", "byteValue"),
        ["java/lang/Character"] = ("C", "charValue"),
        ["java/lang/Boolean"] = ("Z", "booleanValue"),
        ["java/lang/Float"] = ("F", "floatValue"),
        ["java/lang/Double"] = ("D", "doubleValue"),
    };

    /// <summary>The boxed value <c>x.intValue()</c> reads, or null when the expression is not an unboxing.</summary>
    private static JExpr? Unboxing(IrExpr expression)
        => expression is JCall { Kind: JCallKind.Virtual, Receiver: { } receiver, Args.Count: 0 } call
           && Boxes.TryGetValue(call.Owner, out var box) && box.Unbox == call.Name ? receiver : null;

    /// <summary>The primitive <c>Integer.valueOf(x)</c> boxes, or null when the expression is not a boxing.</summary>
    private static JExpr? Boxing(IrExpr expression)
        => expression is JCall { Kind: JCallKind.Static, Name: "valueOf", Args: [var value] } call
           && Boxes.TryGetValue(call.Owner, out var box) && call.Descriptor == $"({box.Primitive})L{call.Owner};"
           && (value.Type == box.Primitive || value is JConst) ? value is JConst c ? c.As(box.Primitive) : value : null;

    private static bool IsPrimitive(string? type) => type is "Z" or "C" or "B" or "S" or "I" or "J" or "F" or "D";

    /// <summary>
    /// A value as Java converts it into a variable of <paramref name="type"/> by itself: the compiler's own
    /// <c>Integer.valueOf</c> and <c>intValue()</c> go (Java boxes and unboxes on assignment), and so does a
    /// widening cast to the type the variable already has.
    /// </summary>
    private static IrExpr Fit(IrExpr value, string? type)
    {
        if (IsPrimitive(type) && Unboxing(value) is { } boxed)
        {
            return boxed;
        }

        if (type is ['L' or '[', ..] && Boxing(value) is { } primitive)
        {
            return primitive;
        }

        return value is JCast cast && cast.CastType == type && Widens(cast) ? cast.Operand : value;
    }

    /// <summary>A cast Java would make by itself: int to long, float or double; long to float or double; float to double.</summary>
    private static bool Widens(JCast cast) => (cast.Operand.Type, cast.CastType) switch
    {
        ("I" or "C" or "S" or "B", "J" or "F" or "D") => true,
        ("J", "F" or "D") => true,
        ("F", "D") => true,
        _ => false,
    };

    /// <summary>A constructor call on <c>this</c> is <c>super(...)</c> or <c>this(...)</c>; anything else prints as an expression.</summary>
    private string ExpressionStatement(JExpr expression)
    {
        // An accessor's assignment as a statement needs no parentheses around it.
        if (expression is JCall accessor && _naming.Scope?.Accessor(accessor) is { } accessed)
        {
            return Expr(accessed);
        }

        if (expression is JCall { Name: "<init>", Receiver: JLocal { Kind: JLocalKind.This } } chained)
        {
            string keyword = chained.Owner == _naming.Class.Name ? "this" : "super";

            // The outer instance and captured values the compiler passes along are implicit here too; only an outer
            // instance that is not this class's own is written, as outer.super(...).
            var (outer, args, descriptor) = _naming.Scope?.Constructed(new JNew(chained.Owner, chained.Descriptor, chained.Args))
                                            ?? (null, chained.Args, chained.Descriptor);
            string prefix = keyword == "super" && outer is not (null or JLocal or JField { Instance: JLocal { Kind: JLocalKind.This } })
                ? Expr(outer, Precedence.Primary) + "."
                : string.Empty;
            return $"{prefix}{keyword}({Arguments(args, descriptor, chained.Owner, "<init>")})";
        }

        return Expr(expression);
    }

    // --- expressions --------------------------------------------------------------------

    private enum Precedence
    {
        Lowest = 0,
        Assignment = 1,
        Conditional = 2,
        OrElse = 3,
        AndAlso = 4,
        Or = 5,
        Xor = 6,
        And = 7,
        Equality = 8,
        Relational = 9,
        Shift = 10,
        Additive = 11,
        Multiplicative = 12,
        Cast = 13,
        Unary = 14,
        Primary = 16,
    }

    private string Condition(IrExpr condition) => Expr(Unboxing(condition) is { Type: "Ljava/lang/Boolean;" } flag ? flag : condition);

    private string Expr(IrExpr expression, Precedence min = Precedence.Lowest)
    {
        if (++_printDepth > MaxPrintDepth)
        {
            _printDepth--;
            return "…";
        }

        try
        {
            var (text, precedence) = Print(expression);
            return precedence < min ? $"({text})" : text;
        }
        finally
        {
            _printDepth--;
        }
    }

    private (string Text, Precedence Precedence) Print(IrExpr expression) => expression switch
    {
        JLocal local => (local.Name, Precedence.Primary),
        JConst constant => Constant(constant),
        JField field => (FieldText(field), Precedence.Primary),
        JArrayElement element => ($"{Expr(element.Array, Precedence.Primary)}[{Expr(element.Index)}]", Precedence.Primary),
        JArrayLength length => ($"{Expr(length.Array, Precedence.Primary)}.length", Precedence.Primary),
        // A signature-polymorphic call returns what the call site says only when a cast says it: without one, javac
        // takes it for the declared Object and looks for a different method.
        JCall call when PolymorphicResult(call) is { } result => ($"({CastTypeText(result)}) {CallText(call)}", Precedence.Cast),
        JCall call => (CallText(call), Precedence.Primary),
        JNew created => (Created(created, null), Precedence.Primary),
        JUninitialized fresh => ($"new {_naming.ClassName(fresh.Owner)} /* not yet constructed */", Precedence.Primary),
        JNewArray array => (NewArrayText(array), Precedence.Primary),
        JBinary binary => BinaryText(binary),
        JNegate negate => ($"-{Expr(negate.Operand, Precedence.Unary)}", Precedence.Unary),
        JCast { Operand: JCall call } cast when JavaGenerics.CastIsRedundant(cast.CastType, call, GenericOf, _naming.FindClass) => Print(call),

        // A value of a type variable cast to its bound's erasure: javac's cast, since the variable erases to Object
        // where the value was stored; the source's value already had the bound's methods.
        JCast { CastType: ['L' or '[', ..] } bounded when _method is not null && GenericType(bounded.Operand) is { } variable && variable.TrimStart('[').StartsWith('T')
                                                     && JavaGenerics.ErasureIn(variable, _method, _naming.Class) == bounded.CastType => Print(bounded.Operand),

        // Objects.requireNonNull(x) returns x's own type: javac's cast after it is its erasure, which x already has.
        JCast { Operand: JCall { Owner: "java/util/Objects", Name: "requireNonNull", Args: [JExpr checkedValue, ..] } nonNull } nullCheck
            when checkedValue.Type == nullCheck.CastType => Print(nonNull),
        JCast cast => ($"({CastTypeText(cast.CastType)}) {Expr(cast.Operand, Precedence.Unary)}", Precedence.Cast),
        JInstanceOf test => ($"{Expr(test.Operand, Precedence.Relational)} instanceof {_naming.Type(test.TestedType)}", Precedence.Relational),
        JCompare compare => ($"{CompareOwner(compare)}.compare({Expr(compare.Left)}, {Expr(compare.Right)})", Precedence.Primary),
        JCaught caught => ($"/* caught {_naming.Type(caught.Type)} */", Precedence.Primary),
        JDynamic dynamic => DynamicText(dynamic),
        JAssignExpr assign => ($"{Expr(assign.Target, Precedence.Primary)} = {Expr(Fit(Typed(assign.Value, assign.Target.Type), assign.Target.Type), Precedence.Assignment)}", Precedence.Assignment),
        JSwitchExpr switchExpression => (SwitchExpressionText(switchExpression), Precedence.Assignment),
        JConditional conditional => ($"{Expr(conditional.Condition, Precedence.OrElse)} ? {Expr(conditional.Then, Precedence.Conditional + 1)} : {Expr(conditional.Else, Precedence.Conditional + 1)}", Precedence.Conditional),
        JUnknown unknown => ($"/* {unknown.Description} */", Precedence.Primary),
        IrCondition condition => ConditionText(condition),
        JLogical logical => LogicalText(logical),
        IrUnary { Op: IrUnaryOp.LogicalNot, Operand: JLogical inner } => Print(JLogical.Not(inner)),
        IrUnary { Op: IrUnaryOp.LogicalNot } not => ($"!{Expr(not.Operand, Precedence.Unary)}", Precedence.Unary),
        _ => (expression.ToString() ?? "?", Precedence.Primary),
    };

    private (string, Precedence) LogicalText(JLogical logical)
    {
        var precedence = logical.And ? Precedence.AndAlso : Precedence.OrElse;
        return ($"{Expr(logical.Left, precedence)} {(logical.And ? "&&" : "||")} {Expr(logical.Right, precedence + 1)}", precedence);
    }

    private (string, Precedence) ConditionText(IrCondition condition)
    {
        var left = condition.Left as JExpr;
        var right = condition.Right as JExpr;

        // A boolean compared with 0 or 1 is the boolean or its negation.
        if (left?.Type == "Z" && right is JConst { Value: int bit } && condition.Cc is IrCondCode.Equal or IrCondCode.NotEqual)
        {
            bool truth = (bit != 0) == (condition.Cc == IrCondCode.Equal);
            return truth ? Print(left) : ($"!{Expr(left, Precedence.Unary)}", Precedence.Unary);
        }

        string op = condition.Cc switch
        {
            IrCondCode.Equal => "==",
            IrCondCode.NotEqual => "!=",
            IrCondCode.Less or IrCondCode.Below => "<",
            IrCondCode.LessOrEqual or IrCondCode.BelowOrEqual => "<=",
            IrCondCode.Greater or IrCondCode.Above => ">",
            _ => ">=",
        };
        var precedence = op is "==" or "!=" ? Precedence.Equality : Precedence.Relational;
        var leftText = right is not null && left is JConst ? Typed(left, right.Type) : condition.Left;
        var rightText = left is not null && right is JConst ? Typed(right, left.Type) : condition.Right;

        // A boolean compared with a value the bytecode kept as an int — (a && b ? 1 : 0) != flag — compares booleans.
        if (right?.Type == "Z" && left is { Type: not "Z" } && AsBoolean(left) is { } leftTruth)
        {
            leftText = leftTruth;
        }
        else if (left?.Type == "Z" && right is { Type: not "Z" } && AsBoolean(right) is { } rightTruth)
        {
            rightText = rightTruth;
        }

        // x.intValue() < y reads x < y; on == and != only when the other side is a primitive, or it would compare references.
        bool relational = precedence == Precedence.Relational;
        if (Unboxing(leftText) is { } leftBoxed && (relational || IsPrimitive((rightText as JExpr)?.Type) && Unboxing(rightText) is null))
        {
            leftText = leftBoxed;
        }

        if (Unboxing(rightText) is { } rightBoxed && (relational || IsPrimitive((leftText as JExpr)?.Type)))
        {
            rightText = rightBoxed;
        }

        (leftText, rightText) = Widened(leftText, rightText);
        return ($"{Expr(leftText, precedence)} {op} {Expr(rightText, precedence + 1)}", precedence);
    }

    /// <summary>
    /// Operands without the widening cast Java's numeric promotion would make anyway: <c>(long) a + b</c> with a long
    /// <c>b</c> is <c>a + b</c>. Only one side loses its cast — <c>(long) a * (long) b</c> keeps one, or it would
    /// multiply as ints.
    /// </summary>
    private static (IrExpr Left, IrExpr Right) Widened(IrExpr left, IrExpr right)
    {
        if (right is JCast rightCast && Widens(rightCast) && (left as JExpr)?.Type == rightCast.CastType)
        {
            return (left, rightCast.Operand);
        }

        if (left is JCast leftCast && Widens(leftCast) && (right as JExpr)?.Type == leftCast.CastType)
        {
            return (leftCast.Operand, right);
        }

        return (left, right);
    }

    private (string, Precedence) BinaryText(JBinary binary)
    {
        var precedence = binary.Op switch
        {
            "*" or "/" or "%" => Precedence.Multiplicative,
            "+" or "-" => Precedence.Additive,
            "<<" or ">>" or ">>>" => Precedence.Shift,
            "&" => Precedence.And,
            "^" => Precedence.Xor,
            _ => Precedence.Or,
        };

        // &, | and ^ on booleans are boolean operators, and their operands print as booleans; arithmetic on a char
        // keeps its constants as numbers — c - 32, not c - ' '.
        var left = binary.Right.Type == "Z" ? Typed(binary.Left, "Z") : binary.Left;
        var right = binary.Left.Type == "Z" ? Typed(binary.Right, "Z") : binary.Right;

        // Arithmetic unboxes by itself.
        left = Unboxing(left) ?? left;
        right = Unboxing(right) ?? right;
        (left, right) = Widened(left, right);
        return ($"{Expr(left, precedence)} {binary.Op} {Expr(right, precedence + 1)}", precedence);
    }

    private string FieldText(JField field)
    {
        if (_naming.Scope?.SyntheticField(field) is { } synthetic)
        {
            return synthetic;
        }

        string name = _naming.FieldName(field.Owner, field.Name, field.FieldType);
        return field.Instance is null
            ? field.Owner == _naming.Class.Name && _naming.ForwardStatics?.Contains(field.Name) != true ? name : $"{_naming.ClassName(field.Owner)}.{name}"
            : $"{Expr(field.Instance, Precedence.Primary)}.{name}";
    }

    /// <summary>
    /// The methods the JVM calls with the call site's own type (JLS §15.12.3): <c>MethodHandle.invoke</c> and
    /// <c>invokeExact</c>, and <c>VarHandle</c>'s access modes.
    /// </summary>
    private static bool IsSignaturePolymorphic(string owner, string name) => owner switch
    {
        "java/lang/invoke/MethodHandle" => name is "invoke" or "invokeExact",
        "java/lang/invoke/VarHandle" => name is "get" or "set" or "getVolatile" or "setVolatile" or "getAcquire" or "setRelease"
            or "getOpaque" or "setOpaque" or "compareAndSet" or "compareAndExchange" or "compareAndExchangeAcquire"
            or "compareAndExchangeRelease" or "weakCompareAndSetPlain" or "weakCompareAndSet" or "weakCompareAndSetAcquire"
            or "weakCompareAndSetRelease" or "getAndSet" or "getAndSetAcquire" or "getAndSetRelease" or "getAndAdd"
            or "getAndAddAcquire" or "getAndAddRelease" or "getAndBitwiseOr" or "getAndBitwiseOrAcquire" or "getAndBitwiseOrRelease"
            or "getAndBitwiseAnd" or "getAndBitwiseAndAcquire" or "getAndBitwiseAndRelease" or "getAndBitwiseXor"
            or "getAndBitwiseXorAcquire" or "getAndBitwiseXorRelease",
        _ => false,
    };

    /// <summary>
    /// The cast a signature-polymorphic call's value needs: its call site's return type, unless that is what the method
    /// declares anyway — Object, void, or the boolean of VarHandle's compare-and-set.
    /// </summary>
    private static string? PolymorphicResult(JCall call)
        => IsSignaturePolymorphic(call.Owner, call.Name) && JCall.ReturnType(call.Descriptor) is { } result
           && result is not ("V" or "Ljava/lang/Object;") && !(result == "Z" && call.Owner == "java/lang/invoke/VarHandle")
            ? result
            : null;

    private string CallText(JCall call)
    {
        if (_naming.Scope?.Accessor(call) is { } accessed)
        {
            return Expr(accessed, Precedence.Primary);
        }

        string name = _naming.MethodName(call.Owner, call.Name, call.Descriptor);
        string args = Arguments(call.Args, call.Descriptor, call.Owner, call.Name);
        switch (call)
        {
            case { Receiver: null } when _witnesses.TryGetValue(call, out var witness):
                return $"{_naming.ClassName(call.Owner)}.{witness}{name}({args})";
            case { Receiver: null }:
                return call.Owner == _naming.Class.Name ? $"{name}({args})" : $"{_naming.ClassName(call.Owner)}.{name}({args})";
            case { Kind: JCallKind.Special, Receiver: JLocal { Kind: JLocalKind.This } } when call.Owner != _naming.Class.Name:
                return $"super.{name}({args})";
            case { Receiver: JLocal { Kind: JLocalKind.This } }:
                return $"this.{name}({args})";
            // A lambda or method reference called in place has no type to take but the one a cast gives it.
            case { Receiver: JDynamic { Target: not null } function }:
                return $"(({_naming.Type(function.Type)}) {Expr(function, Precedence.Cast)}).{name}({args})";
            default:
                return $"{Expr(call.Receiver!, Precedence.Primary)}.{name}({args})";
        }
    }

    /// <summary>Arguments typed by the parameters they fill, so a boolean parameter reads <c>true</c>, not 1.</summary>
    private string Arguments(IReadOnlyList<JExpr> args, string descriptor, string owner, string name)
    {
        var parameters = Descriptors.ParameterDescriptors(descriptor);

        // A signature-polymorphic call's type is made from its arguments' types: each one of another type, or null,
        // is cast to what the call site had.
        if (IsSignaturePolymorphic(owner, name))
        {
            return string.Join(", ", args.Select((a, i) =>
            {
                string? parameter = i < parameters.Count ? parameters[i] : null;
                var argument = Argument(a, parameter);
                return parameter is not null && (argument is JConst { Value: null } || argument is JExpr { Type: { } type } && type != parameter)
                    ? $"({CastTypeText(parameter)}) {Expr(argument, Precedence.Unary)}"
                    : Expr(argument, Precedence.Assignment + 1);
            }));
        }

        // A varargs call given nothing for its varargs: javac passes an empty array, the source passed nothing — unless
        // a method without that last parameter would then be the one called.
        if (args.Count > 0 && args.Count == parameters.Count && args[^1] is JNewArray { Elements: null, Dimensions: [JConst { Value: 0 }] } empty
            && empty.ArrayType.Count(c => c == '[') == 1 && IsVarargs(owner, name, descriptor)
            && _naming.FindClass(owner)?.Methods.Any(m => m.Name == name && m.Descriptor.StartsWith($"({string.Concat(parameters.Take(parameters.Count - 1))})", StringComparison.Ordinal)) != true)
        {
            return Arguments(args.Take(args.Count - 1).ToList(), $"({string.Concat(parameters.Take(parameters.Count - 1))})V", owner, name);
        }

        // A varargs call javac packed into an array: the array's elements are the arguments.
        if (args.Count > 0 && args.Count == parameters.Count && args[^1] is JNewArray { Elements: { Count: > 0 } elements } packed
            && !(elements.Count == 1 && (elements[0] is JConst { Value: null } || elements[0].Type is ['[', ..]))
            && IsVarargs(owner, name, descriptor))
        {
            string head = args.Count > 1 ? Arguments(args.Take(args.Count - 1).ToList(), $"({string.Concat(parameters.Take(parameters.Count - 1))})V", owner, name) + ", " : string.Empty;
            string elementType = packed.ArrayType[1..];
            return head + string.Join(", ", elements.Select(e => Expr(Fit(Typed(e, elementType), elementType), Precedence.Assignment + 1)));
        }

        var rivals = Rivals(owner, name, descriptor, parameters);
        var generics = owner == _naming.Class.Name && _naming.Class.Methods.FirstOrDefault(m => m.Name == name && m.Descriptor == descriptor) is { Signature: { } own } && !own.StartsWith('<')
            ? JavaGenerics.ParameterSignatures(own)
            : null;
        return string.Join(", ", args.Select((a, i) =>
        {
            if (generics is not null && generics.Count == args.Count)
            {
                Witness(a, generics[i]);
            }

            string? parameter = i < parameters.Count ? parameters[i] : null;
            var argument = Argument(a, parameter);

            // null or a lambda another overload could take as well says which it is for, as the source had to. A
            // lambda's cast needs the full generic type, or its parameters lose theirs; without one it goes uncast.
            if (parameter is ['L' or '[', ..] && a is JConst { Value: null } or JDynamic { Target: not null }
                && rivals.Any(r => r[i] != parameter && r[i] is ['L' or '[', ..])
                && (a is JConst ? parameter : ParameterSignature(owner, name, descriptor, i)) is { } castType)
            {
                return $"({CastTypeText(castType)}) {Expr(a, Precedence.Unary)}";
            }
            if (argument is JCast { CastType: "I" or "J" or "F" or "D", Operand.Type: "C" } cast && !CharOverloaded(owner, name, parameters.Count, i))
            {
                argument = cast.Operand;
            }

            if (Boxed(owner, name) && Boxing(argument) is { } primitive)
            {
                argument = primitive;
            }

            return Expr(argument, Precedence.Assignment + 1);
        }));
    }

    /// <summary>
    /// The other methods of this JAR a call could be taken for: the same name and number of parameters, and each
    /// parameter the same primitive or, for a reference, any reference — so that null or a lambda fits both.
    /// </summary>
    private List<IReadOnlyList<string>> Rivals(string owner, string name, string descriptor, IReadOnlyList<string> parameters)
    {
        if (_naming.FindClass(owner) is not { } file)
        {
            return [];
        }

        var rivals = new List<IReadOnlyList<string>>();
        foreach (var method in file.Methods)
        {
            if (method.Name != name || method.Descriptor == descriptor || (method.Access & (JvmAccess.Synthetic | JvmAccess.VolatileOrBridge)) != 0)
            {
                continue;
            }

            var others = Descriptors.ParameterDescriptors(method.Descriptor);
            if (others.Count == parameters.Count && others.Select((p, i) => p == parameters[i] || (p is ['L' or '[', ..] && parameters[i] is ['L' or '[', ..])).All(b => b))
            {
                rivals.Add(others);
            }
        }

        return rivals;
    }

    /// <summary>A parameter's generic type from the method's signature, when it names no type variable the caller cannot see.</summary>
    private string? ParameterSignature(string owner, string name, string descriptor, int index)
    {
        if (_naming.FindClass(owner)?.Methods.FirstOrDefault(m => m.Name == name && m.Descriptor == descriptor) is not { Signature: { } signature }
            || JavaGenerics.ParameterSignatures(signature) is not { } generic || index >= generic.Count)
        {
            return null;
        }

        return TypeVariable().IsMatch(generic[index]) ? null : generic[index];
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(^|[<;\[+\-*])T[^;<>]+;")]
    private static partial System.Text.RegularExpressions.Regex TypeVariable();

    /// <summary>A cast's type: a descriptor, or a generic signature when the cast says which overload a lambda is for.</summary>
    private string CastTypeText(string type)
        => (type.Contains('<', StringComparison.Ordinal) || TypeVariable().IsMatch(type)) && Descriptors.FieldSignature(type, _naming.ClassName) is { } generic
            ? generic
            : _naming.Type(type);

    /// <summary>JDK methods declared with varargs, which the JAR cannot say: their arrays are written out as arguments.</summary>
    private static readonly HashSet<(string Owner, string Name)> JdkVarargs =
    [
        ("java/util/Arrays", "asList"), ("java/util/List", "of"), ("java/util/Set", "of"), ("java/util/stream/Stream", "of"),
        ("java/util/stream/IntStream", "of"), ("java/lang/String", "format"), ("java/lang/String", "join"), ("java/lang/String", "formatted"),
        ("java/lang/Class", "getMethod"), ("java/lang/Class", "getDeclaredMethod"), ("java/lang/Class", "getConstructor"),
        ("java/lang/Class", "getDeclaredConstructor"), ("java/lang/reflect/Method", "invoke"), ("java/lang/reflect/Constructor", "newInstance"),
        ("java/util/Objects", "hash"), ("java/util/EnumSet", "of"), ("java/util/Collections", "addAll"), ("java/text/MessageFormat", "format"),
        ("java/io/PrintStream", "printf"), ("java/io/PrintStream", "format"), ("java/io/PrintWriter", "printf"), ("java/io/PrintWriter", "format"),
        ("java/nio/file/Paths", "get"), ("java/nio/file/Path", "of"),
    ];

    private bool IsVarargs(string owner, string name, string descriptor)
    {
        if (_naming.FindClass(owner) is { } file)
        {
            return file.Methods.Any(m => m.Name == name && m.Descriptor == descriptor && (m.Access & JvmAccess.TransientOrVarargs) != 0);
        }

        return JdkVarargs.Contains((owner, name));
    }

    /// <summary>JDK classes with methods taking a char beside one taking an int, where the cast picks the overload.</summary>
    private static readonly HashSet<string> CharOverloads = new(StringComparer.Ordinal)
    {
        "java/lang/StringBuilder", "java/lang/StringBuffer", "java/lang/AbstractStringBuilder", "java/io/PrintStream",
        "java/io/PrintWriter", "java/io/Writer", "java/io/StringWriter", "java/io/CharArrayWriter", "java/lang/Appendable",
    };

    /// <summary>Whether a char argument where an int is taken needs its cast: when the method has a char overload there.</summary>
    private bool CharOverloaded(string owner, string name, int arity, int position)
    {
        if (_naming.FindClass(owner) is { } file)
        {
            return file.Methods.Any(m => m.Name == name && Descriptors.ParameterDescriptors(m.Descriptor) is var ps && ps.Count == arity && ps[position] == "C");
        }

        return CharOverloads.Contains(owner) || (owner == "java/lang/String" && name is "valueOf" or "copyValueOf");
    }

    /// <summary>
    /// Collection methods whose arguments javac boxed and that have no primitive overload, so the box can go:
    /// <c>list.add(i)</c>. <c>List.remove</c> is not one — <c>remove(int)</c> removes by position.
    /// </summary>
    private static bool Boxed(string owner, string name)
        => owner.StartsWith("java/util/", StringComparison.Ordinal)
           && name is "add" or "put" or "get" or "containsKey" or "containsValue" or "contains" or "set" or "offer" or "push"
               or "addFirst" or "addLast" or "getOrDefault" or "putIfAbsent" or "indexOf" or "lastIndexOf" or "offerFirst" or "offerLast";

    /// <summary>
    /// An argument as the parameter's type. A char handed to a numeric parameter is cast, because Java would
    /// otherwise pick the char overload where one exists — <c>append(char)</c> for <c>append(int)</c>.
    /// </summary>
    private static IrExpr Argument(JExpr argument, string? parameter)
    {
        var typed = Typed(argument, parameter);

        // A constant only narrows by itself where it is assigned: as an argument, a byte or short one is cast.
        if (typed is JConst { Type: "B" or "S" } narrow && parameter is "B" or "S")
        {
            return new JCast(parameter, narrow with { ConstType = "I" });
        }

        return typed is JExpr { Type: "C" } character && typed is not (JConst or JCast) && parameter is "I" or "J" or "F" or "D"
            ? new JCast(parameter, character)
            : typed;
    }

    private string NewArrayText(JNewArray array)
    {
        string element = array.ArrayType;
        int dims = 0;
        while (dims < element.Length && element[dims] == '[')
        {
            dims++;
        }

        string type = _naming.Type(element[dims..]);
        var sb = new StringBuilder($"new {type}");
        if (array.Elements is { } elements)
        {
            string elementType = array.ArrayType[1..];
            sb.Append(string.Concat(Enumerable.Repeat("[]", dims)));
            return sb.Append(" {").Append(string.Join(", ", elements.Select(e => Expr(Fit(Typed(e, elementType), elementType), Precedence.Assignment + 1)))).Append('}').ToString();
        }

        for (int i = 0; i < dims; i++)
        {
            sb.Append(i < array.Dimensions.Count ? $"[{Expr(array.Dimensions[i])}]" : "[]");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Newer javac turns an object into a string before it concatenates — <c>String.valueOf(x) + " "</c> — which the
    /// source wrote as <c>x + " "</c>. Kept first in line when nothing next to it is a string, where it makes the sum one.
    /// </summary>
    private static JExpr Stringified(JExpr argument, bool first)
        => !first && argument is JCall { Kind: JCallKind.Static, Owner: "java/lang/String", Name: "valueOf", Descriptor: "(Ljava/lang/Object;)Ljava/lang/String;", Args: [{ Type: ['L' or '[', ..] } value] }
            && value is not JConst
            ? value
            : argument;

    private (string, Precedence) DynamicText(JDynamic dynamic)
    {
        if (dynamic.Concat is { } parts)
        {
            var pieces = new List<string>();
            bool startsWithString = parts.Count > 0 && parts[0] is string;
            for (int p = 0; p < parts.Count; p++)
            {
                pieces.Add(parts[p] switch
                {
                    string text => Quote(text),
                    int index when index < dynamic.Args.Count => Expr(Stringified(dynamic.Args[index], p == 0 && !(parts.Count > 1 && parts[1] is string)), Precedence.Additive + 1),
                    _ => "?",
                });
            }

            if (!startsWithString && !(parts.Count > 1 && parts[1] is string) && !(dynamic.Args.FirstOrDefault()?.Type == "Ljava/lang/String;"))
            {
                pieces.Insert(0, "\"\"");
            }

            return (pieces.Count == 0 ? "\"\"" : string.Join(" + ", pieces), Precedence.Additive);
        }

        if (dynamic.Target is { } target)
        {
            if (_naming.Scope?.Lambda(dynamic, _level) is { } lambda)
            {
                return (lambda, Precedence.Primary);
            }

            string method = target.Name == "<init>" ? "new" : _naming.MethodName(target.Owner, target.Name, target.Descriptor);

            // A virtual or interface method with its receiver captured is bound to it: receiver::name.
            if (dynamic.TargetKind is 5 or 7 or 9 && dynamic.Args.Count == 1)
            {
                return ($"{Expr(dynamic.Args[0], Precedence.Primary)}::{method}", Precedence.Primary);
            }

            if (dynamic.Args.Count == 0)
            {
                return ($"{_naming.ClassName(target.Owner)}::{method}", Precedence.Primary);
            }

            string owner = _naming.ClassName(target.Owner);
            string capture = $" /* captures {string.Join(", ", dynamic.Args.Select(a => Expr(a)))} */";
            return ($"({_naming.Type(dynamic.Type)}) {owner}::{method}{capture}", Precedence.Cast);
        }

        // A pattern switch's dispatch left as javac compiled it: what SwitchBootstraps computes, in Java.
        if (dynamic.IsPatternDispatch && PatternDispatchText(dynamic) is { } dispatch)
        {
            return (dispatch, Precedence.Primary);
        }

        string bootstrap = dynamic.Bootstrap is null ? string.Empty : $" /* {dynamic.Bootstrap.Replace('/', '.')} */";
        return ($"invokedynamic {dynamic.Name}({string.Join(", ", dynamic.Args.Select(a => Expr(a)))}){bootstrap}", Precedence.Primary);
    }

    /// <summary>
    /// <c>typeSwitch(value, restart)</c> written out: -1 for null, else the first label from <c>restart</c> on that matches —
    /// a class by <c>instanceof</c>, a string by <c>equals</c>, an enum constant by identity — else the number of labels.
    /// Null when a label is one it cannot write, or reading the operands twice could matter.
    /// </summary>
    private string? PatternDispatchText(JDynamic dynamic)
    {
        if (dynamic.Args is not [var value, var restart])
        {
            return null;
        }

        // Operands read more than once are read once, into variables of a switch expression's block, when they are not simple.
        var held = new List<string>();
        string v = value.IsSimple ? Expr(value, Precedence.Primary) : "$value";
        string r = restart.IsSimple ? Expr(restart, Precedence.Relational) : "$restart";
        if (!value.IsSimple)
        {
            held.Add($"{_naming.Type(value.Type ?? "Ljava/lang/Object;")} $value = {Expr(value)};");
        }

        if (!restart.IsSimple)
        {
            held.Add($"int $restart = {Expr(restart)};");
        }

        bool enumSwitch = dynamic.Bootstrap == JavaPatterns.EnumSwitch;
        var labels = dynamic.BootstrapArguments!;
        var sb = new StringBuilder($"({v} == null ? -1 : ");
        for (int i = 0; i < labels.Count; i++)
        {
            string? test = labels[i] switch
            {
                JConst { Value: ClassLiteral type } => $"{v} instanceof {_naming.Type(type.Descriptor)}",
                JConst { Value: string name } when enumSwitch && value.Type is { } enumType => $"{v} == {_naming.Type(enumType)}.{name}",
                JConst { Value: string text } => $"{Quote(text)}.equals({v})",
                JConst { Value: int number } => $"Integer.valueOf({number.ToString(CultureInfo.InvariantCulture)}).equals({v})",
                _ => null,
            };
            if (test is null)
            {
                return null;
            }

            sb.Append(CultureInfo.InvariantCulture, $"{r} <= {i} && {test} ? {i} : ");
        }

        string chain = sb.Append(labels.Count.ToString(CultureInfo.InvariantCulture)).Append(')').ToString();
        return held.Count == 0 ? chain : $"switch (0) {{ default -> {{ {string.Join(' ', held)} yield {chain}; }} }}";
    }

    private static string CompareOwner(JCompare compare) => compare.Left.Type switch
    {
        "J" => "Long",
        "F" => "Float",
        "D" => "Double",
        _ => "Integer",
    };

    /// <summary>
    /// A value as the type it is used as: an int constant headed for a boolean or char says so, and so do the arms
    /// of a conditional. A conditional that picks between truth values is the condition itself — javac turns
    /// <c>return a &gt; b &amp;&amp; c;</c> into one, pushing 1 on one path and 0 on the other.
    /// </summary>
    private static IrExpr Typed(IrExpr expression, string? type) => expression switch
    {
        JConst constant => constant.As(type),
        JConditional conditional when type == "Z" && AsBoolean(conditional) is { } truth => truth,
        JConditional conditional => conditional with { Then = (JExpr)TypedJ(conditional.Then, type), Else = (JExpr)TypedJ(conditional.Else, type) },

        // A bitwise operator whose result is stored as a boolean is the boolean operator; its operands print as booleans.
        JBinary { Op: "&" or "|" or "^" } bitwise when type == "Z" && (bitwise.Left.Type == "Z" || bitwise.Right.Type == "Z") => bitwise with { ResultType = "Z" },

        // An int where a narrower type is wanted — a local whose uses did not settle it — says so with a cast.
        JExpr { Type: "I" } narrowed when type is "C" or "B" or "S" => new JCast(type, narrowed),
        JExpr { Type: "I" } truth when type == "Z" => new IrCondition(IrCondCode.NotEqual, truth, JConst.Int(0)),
        JExpr j => j,
        IrCondition or IrUnary => expression,
        _ => new JUnknown(expression.ToString() ?? "?"),
    };

    private static JExpr TypedJ(JExpr expression, string? type) => Typed(expression, type) as JExpr ?? expression;

    private static readonly JConst True = new(1, "Z");
    private static readonly JConst False = new(0, "Z");

    /// <summary>A value as a boolean expression, or null when it is not one: 0 and 1, a boolean, and conditionals between them.</summary>
    private static IrExpr? AsBoolean(IrExpr value)
    {
        switch (value)
        {
            case JConst { Value: int bit }:
                return bit == 0 ? False : bit == 1 ? True : null;
            case JConditional c:
            {
                var then = AsBoolean(c.Then);
                var otherwise = AsBoolean(c.Else);
                if (then is null || otherwise is null)
                {
                    return null;
                }

                return (IsTrue(then), IsFalse(then), IsTrue(otherwise), IsFalse(otherwise)) switch
                {
                    (true, _, _, true) => c.Condition,
                    (_, true, true, _) => JavaShaping.Not(c.Condition),
                    (true, _, _, _) => new JLogical(false, c.Condition, otherwise),
                    (_, true, _, _) => new JLogical(true, JavaShaping.Not(c.Condition), otherwise),
                    (_, _, _, true) => new JLogical(true, c.Condition, then),
                    (_, _, true, _) => new JLogical(false, JavaShaping.Not(c.Condition), then),
                    _ when then is JExpr t && otherwise is JExpr o => c with { Then = t, Else = o },
                    _ => null,
                };
            }

            case JExpr { Type: "Z" }:
            case IrCondition or JLogical or IrUnary { Op: IrUnaryOp.LogicalNot }:
                return value;
            default:
                return null;
        }
    }

    private static bool IsTrue(IrExpr e) => e is JConst { Value: 1, ConstType: "Z" };

    private static bool IsFalse(IrExpr e) => e is JConst { Value: 0, ConstType: "Z" };

    private (string, Precedence) Constant(JConst constant)
    {
        string text = constant.Value switch
        {
            null => "null",
            int i when constant.Type == "Z" => i == 0 ? "false" : "true",
            int i when constant.Type == "C" => CharLiteral(i),
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture) + "L",
            float f => FloatText(f),
            double d => DoubleText(d),
            string s => Quote(s),
            ClassLiteral c => $"{_naming.Type(c.Descriptor)}.class",
            var other => other.ToString() ?? "?",
        };

        return (text, text.StartsWith('-') ? Precedence.Unary : Precedence.Primary);
    }

    internal static string FloatText(float f) => float.IsNaN(f) ? "Float.NaN"
        : float.IsPositiveInfinity(f) ? "Float.POSITIVE_INFINITY"
        : float.IsNegativeInfinity(f) ? "Float.NEGATIVE_INFINITY"
        : f.ToString("R", CultureInfo.InvariantCulture) + "f";

    internal static string DoubleText(double d)
    {
        if (double.IsNaN(d))
        {
            return "Double.NaN";
        }

        if (double.IsInfinity(d))
        {
            return d > 0 ? "Double.POSITIVE_INFINITY" : "Double.NEGATIVE_INFINITY";
        }

        string text = d.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    internal static string CharText(int value) => CharLiteral(value);

    private static string CharLiteral(int value) => value switch
    {
        '\'' => "'\\''",
        '\\' => "'\\\\'",
        '\n' => "'\\n'",
        '\r' => "'\\r'",
        '\t' => "'\\t'",
        >= 0x20 and < 0x7F => $"'{(char)value}'",
        _ => $"'\\u{value & 0xFFFF:X4}'",
    };

    public static string Quote(string text)
    {
        var sb = new StringBuilder(text.Length + 2);
        sb.Append('"');
        foreach (char c in text)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                // Control characters, and each half of a surrogate pair (javac joins the escapes back), as escapes.
                < ' ' or (>= '\u007F' and < '\u00A0') or (>= '\uD800' and <= '\uDFFF') => $"\\u{(int)c:X4}",
                _ => c.ToString(),
            });
        }

        return sb.Append('"').ToString();
    }

    // --- tidying and declarations ---------------------------------------------------------

    /// <summary>
    /// Statements after one that never falls through are dead until a label something still jumps to — the shared
    /// return block a <c>goto</c> used to reach, left where the structurer placed it once the jumps became returns.
    /// </summary>
    private CStmt DropUnreachable(CStmt statement)
    {
        switch (statement)
        {
            case CSeq seq:
                var kept = new List<CStmt>(seq.Items.Count);
                bool dead = false;
                foreach (var item in seq.Items)
                {
                    if (dead && !(item is CLabel label && _targets.Contains(label.Va)))
                    {
                        continue;
                    }

                    dead = false;
                    var tidied = DropUnreachable(item);

                    // if (a) { return; } else { rest } reads as if (a) { return; } rest — an early exit, one level shallower.
                    if (tidied is CIf { Else: { } rest } early && NeverFallsThrough(early.Then) && rest is not CIf)
                    {
                        kept.Add(early with { Else = null });
                        kept.AddRange(rest is CSeq restSeq ? restSeq.Items : [rest]);
                        dead = NeverFallsThrough(rest);
                        continue;
                    }

                    kept.Add(tidied);
                    dead = NeverFallsThrough(tidied);
                }

                return new CSeq(kept);
            case CIf conditional:
                return conditional with { Then = DropUnreachable(conditional.Then), Else = conditional.Else is null ? null : DropUnreachable(conditional.Else) };
            case CLoop loop:
                return loop with { Body = DropUnreachable(loop.Body) };
            case CSwitch dispatch:
                return dispatch with { Cases = dispatch.Cases.Select(c => c with { Body = DropUnreachable(c.Body) }).ToList() };
            case JTry attempt:
                return attempt with { Body = DropUnreachable(attempt.Body), Catches = attempt.Catches.Select(c => c with { Body = DropUnreachable(c.Body) }).ToList() };
            case CRaw { Statement: JRegion region } raw:
                return raw with { Statement = region with { Body = DropUnreachable(region.Body) } };
            default:
                return statement;
        }
    }

    /// <summary>Whether control can never run on past a statement. Loops and switches are assumed to fall through: their breaks are not followed.</summary>
    private static bool NeverFallsThrough(CStmt statement) => statement switch
    {
        CRaw { Statement: IrReturn or JThrow } => true,
        CRaw { Statement: JRegion region } => NeverFallsThrough(region.Body),
        CGoto or CBreak or CContinue => true,
        CSeq seq => seq.Items.LastOrDefault(i => i is not CLabel) is { } last && NeverFallsThrough(last),
        CIf { Else: { } otherwise } conditional => NeverFallsThrough(conditional.Then) && NeverFallsThrough(otherwise),
        JTry attempt => NeverFallsThrough(attempt.Body) && attempt.Catches.All(c => NeverFallsThrough(c.Body)),
        _ => false,
    };

    /// <summary>
    /// A jump to a block that only returns a local or a constant is that return: javac shares one such block
    /// between the paths that end the same way, and the jump reads better as what it does.
    /// </summary>
    private static CStmt InlineReturns(CStmt statement, IReadOnlyDictionary<ulong, IrReturn> returns) => statement switch
    {
        CGoto jump when returns.TryGetValue(jump.Va, out var ret) => new CRaw(ret),
        CSeq seq => new CSeq(seq.Items.Select(i => InlineReturns(i, returns)).ToList()),
        CIf conditional => conditional with { Then = InlineReturns(conditional.Then, returns), Else = conditional.Else is null ? null : InlineReturns(conditional.Else, returns) },
        CLoop loop => loop with { Body = InlineReturns(loop.Body, returns) },
        CSwitch dispatch => dispatch with { Cases = dispatch.Cases.Select(c => c with { Body = InlineReturns(c.Body, returns) }).ToList() },
        JTry attempt => attempt with
        {
            Body = InlineReturns(attempt.Body, returns),
            Catches = attempt.Catches.Select(c => c with { Body = InlineReturns(c.Body, returns) }).ToList(),
        },
        CRaw { Statement: JRegion region } raw => raw with { Statement = region with { Body = InlineReturns(region.Body, returns) } },
        _ => statement,
    };

    /// <summary>A void method's closing <c>return;</c>, and a constructor's call to <c>Object()</c>, say nothing.</summary>
    private CStmt Tidy(CStmt body, JvmMethod method)
    {
        if (body is not CSeq seq)
        {
            seq = new CSeq([body]);
        }

        var items = seq.Items.ToList();
        int last = items.FindLastIndex(i => i is not CLabel);
        if (last >= 0 && items[last] is CRaw { Statement: IrReturn { Value: null } } && JCall.ReturnType(method.Descriptor) == "V")
        {
            items.RemoveAt(last);
        }

        int first = items.FindIndex(i => i is CRaw);
        if (method.IsConstructor && first >= 0
            && items[first] is CRaw { Statement: JExprStmt { Expression: JCall { Name: "<init>", Args.Count: 0, Receiver: JLocal { Kind: JLocalKind.This } } call } }
            && call.Owner != _naming.Class.Name)
        {
            items.RemoveAt(first);
        }

        return new CSeq(items);
    }

    /// <summary>
    /// <c>catch (Throwable ex2) { t = ex2; … }</c> is <c>catch (Throwable t) { … }</c> when <c>t</c> lives only in
    /// such handlers: javac gave several clauses' variables one slot and one name, so no handler's own could be
    /// the slot, yet each handler sets it before reading it, and nothing outside them reads it at all.
    /// </summary>
    private static CStmt CatchVariables(CStmt body, LiftedMethod lifted)
    {
        var total = JavaTree.CountLocals(body);
        // By the handler's own variable, which names it: the rewrite below rebuilds the handlers it walks.
        var candidates = new Dictionary<string, (JCatch Handler, string Name)>(StringComparer.Ordinal);
        foreach (var handler in JavaTree.Descendants(body).OfType<JTry>().SelectMany(t => t.Catches))
        {
            if (handler is { Variable: { } caught, CatchType: var catchType }
                && JavaTree.Items(handler.Body).FirstOrDefault() is CRaw { Statement: IrAssign { Dst: JLocal target, Src: JLocal source } }
                && source.Name == caught && target.Name != caught && total.GetValueOrDefault(caught) == 1
                && target.Type == (catchType is null ? "Ljava/lang/Throwable;" : $"L{catchType};"))
            {
                candidates[caught] = (handler, target.Name);
            }
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in candidates.Values.GroupBy(c => c.Name, StringComparer.Ordinal))
        {
            // Handlers that already call their variable so keep their mentions to themselves as well.
            int own = JavaTree.Descendants(body).OfType<JTry>().SelectMany(t => t.Catches).Where(c => c.Variable == group.Key)
                .Sum(c => JavaTree.CountLocals(c.Body).GetValueOrDefault(group.Key));
            if (own + group.Sum(c => JavaTree.CountLocals(c.Handler.Body).GetValueOrDefault(group.Key)) == total.GetValueOrDefault(group.Key))
            {
                names.Add(group.Key);
                lifted.Locals.Caught(group.Key);
            }
        }

        if (names.Count == 0)
        {
            return body;
        }

        bool Renamed(JCatch c, out string name)
        {
            name = c.Variable is { } v && candidates.TryGetValue(v, out var found) && names.Contains(found.Name) ? found.Name : string.Empty;
            return name.Length > 0;
        }

        return JavaTree.Rewrite(body, statement => statement is JTry attempt && attempt.Catches.Any(c => Renamed(c, out _))
            ? attempt with
            {
                Catches = attempt.Catches.Select(c => Renamed(c, out string name)
                    ? c with { Variable = name, Body = JavaTree.Sequence(JavaTree.Items(c.Body).Skip(1)) }
                    : c).ToList(),
            }
            : statement);
    }

    /// <summary>
    /// Locals whose first mention is an assignment at the top level of the body, which can carry the declaration:
    /// nothing reads them before, and no branch skips it.
    /// </summary>
    private static HashSet<string> FoldableDeclarations(CStmt body, LiftedMethod lifted)
    {
        var folded = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var topLevel = body is CSeq seq ? seq.Items : [body];
        foreach (var item in topLevel)
        {
            if (item is CRaw { Statement: IrAssign { Dst: JLocal { Kind: JLocalKind.Local or JLocalKind.Temp or JLocalKind.Stack } target } assign }
                && !seen.Contains(target.Name) && lifted.Locals.Declared.ContainsKey(target.Name) && !lifted.Locals.IsCaught(target.Name)
                && !Names(assign.Src).Contains(target.Name))
            {
                folded.Add(target.Name);
            }

            foreach (var node in Descendants(item))
            {
                foreach (string name in NamesIn(node))
                {
                    seen.Add(name);
                }
            }
        }

        return folded;
    }

    /// <summary>Every local name the body mentions, in one walk: a huge static initializer has thousands of temporaries.</summary>
    private static HashSet<string> Mentioned(CStmt body) => Descendants(body).SelectMany(NamesIn).ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> NamesIn(CStmt node) => node switch
    {
        CRaw { Statement: IrAssign a } => Names(a.Dst).Concat(Names(a.Src)),
        CRaw { Statement: var s } => JavaRewrite.Evaluated(s).SelectMany(Names),
        CIf i => Names(i.Condition),
        CLoop { Condition: { } c } => Names(c),
        CSwitch s => Names(s.Value),
        JLoop l => (l.Condition is null ? [] : Names(l.Condition))
            .Concat(l.Init is IrAssign init ? Names(init.Dst).Concat(Names(init.Src)) : [])
            .Concat(l.Update is IrAssign update ? Names(update.Dst).Concat(Names(update.Src)) : []),
        JSwitch s => Names(s.Value),
        JSynchronized l => Names(l.Lock),
        _ => [],
    };

    private static HashSet<string> Names(IrExpr expression)
        => JavaRewrite.PostOrder(expression).OfType<JLocal>().Select(l => l.Name).ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<CStmt> Descendants(CStmt root) => JavaTree.Descendants(root);

    private static string LabelName(ulong va) => $"L{va:X4}";

    private void Line(int level, string text)
    {
        for (int i = 0; i < level; i++)
        {
            _sb.Append(Indent);
        }

        _sb.Append(text).Append('\n');
    }
}
