using System.Globalization;
using System.Text;
using Spydate.Core.Jvm;
using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Structuring;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// How the Java printer names things: the class being printed (for its package and to call its own members
/// without qualification), and the names the project has given classes and members, by annotation key.
/// </summary>
internal sealed class JavaNaming
{
    private readonly Func<string, string?> _given;
    private readonly Func<string, ClassFile?> _find;

    public JavaNaming(ClassFile file, Func<string, string?> given, Func<string, ClassFile?>? find = null)
    {
        Class = file;
        _given = given;
        _find = find ?? (_ => null);
        Package = file.PackageName;
    }

    /// <summary>A class of the same JAR by internal name, or null for one it does not hold — the JDK's, a library's.</summary>
    public ClassFile? FindClass(string internalName) => internalName == Class.Name ? Class : _find(internalName);

    public ClassFile Class { get; }

    public string Package { get; }

    public string? GivenClassName(string internalName) => _given(internalName);

    public string MethodName(string owner, string name, string descriptor) => _given($"{owner}.{name}{descriptor}") ?? name;

    public string FieldName(string owner, string name, string descriptor) => _given($"{owner}.{name}:{descriptor}") ?? name;

    /// <summary>
    /// A descriptor as Java writes the type: <c>int</c>, <c>String</c>, <c>java.util.List</c>, <c>byte[][]</c>.
    /// Classes in <c>java.lang</c> and in the class's own package go by their simple name; a nested class by
    /// <c>Outer.Inner</c>; a class the project renamed, by its new name.
    /// </summary>
    public string Type(string? descriptor)
    {
        if (descriptor is null or "")
        {
            return "Object";
        }

        int dims = 0;
        while (dims < descriptor.Length && descriptor[dims] == '[')
        {
            dims++;
        }

        string element = descriptor[dims..] switch
        {
            "Z" => "boolean",
            "B" => "byte",
            "C" => "char",
            "S" => "short",
            "I" => "int",
            "J" => "long",
            "F" => "float",
            "D" => "double",
            "V" => "void",
            ['L', .. var name, ';'] => ClassName(name),
            var other => other,
        };

        return dims == 0 ? element : element + string.Concat(Enumerable.Repeat("[]", dims));
    }

    /// <summary>An internal class name as the printed class name.</summary>
    public string ClassName(string internalName)
    {
        if (_given(internalName) is { } given)
        {
            return given;
        }

        int slash = internalName.LastIndexOf('/');
        string package = slash < 0 ? string.Empty : internalName[..slash];
        string simple = slash < 0 ? internalName : internalName[(slash + 1)..];

        // Outer$Inner reads Outer.Inner; Outer$1, an anonymous class, keeps its binary name.
        var nested = new StringBuilder(simple.Length);
        for (int i = 0; i < simple.Length; i++)
        {
            nested.Append(simple[i] == '$' && i + 1 < simple.Length && !char.IsDigit(simple[i + 1]) && i > 0 ? '.' : simple[i]);
        }

        string shortName = nested.ToString();
        return package == "java/lang" || package == Package ? shortName : $"{package.Replace('/', '.')}.{shortName}";
    }
}

/// <summary>
/// Prints a structured method body — and a whole class around such bodies — as Java-shaped pseudo-code: real
/// Java syntax for everything the structure expressed, <c>goto L0012;</c> and a label for the edges it did not,
/// and the compiler's own shapes (a <c>finally</c> copied onto each exit, a switch on a string's hash) left as
/// they are rather than guessed back into sugar.
/// </summary>
internal sealed class JavaEmitter
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
            body = JavaDeclarations.Blocked(body);
            var names = lifted.Locals.Declared.Keys.Where(n => !lifted.Locals.IsCaught(n)).ToHashSet(StringComparer.Ordinal);
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
        switch (statement)
        {
            case CSeq seq:
                for (int i = 0; i < seq.Items.Count; i++)
                {
                    if (_declarations is not null)
                    {
                        foreach (string name in _declarations.Before(seq, i))
                        {
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

    /// <summary>A <c>for</c>'s initialiser, with the declaration when the variable lives only in the loop.</summary>
    private string ForInit(JLoop loop, IrAssign init)
    {
        if (init.Dst is JLocal local && _declarations?.Declares(loop) == true)
        {
            return $"{DeclaredType(local.Name, local.Type)} {local.Name} = {Value(init.Src, local)}";
        }

        return Assignment(init, topLevel: false);
    }

    private void WriteSwitch(JSwitch dispatch, int level)
    {
        var breakable = _breakable;
        _breakable = dispatch.Label;
        var values = _lifted?.SwitchValues.GetValueOrDefault(dispatch.Va);
        Line(level, $"{LabelPrefix(dispatch.Label)}switch ({Expr(dispatch.Value)}) {{");
        foreach (var arm in dispatch.Cases)
        {
            foreach (string label in CaseLabels(arm, values))
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

    /// <summary>An arm's labels by value; the arm that has <c>default</c> needs no others — they go there anyway.</summary>
    private static IEnumerable<string> CaseLabels(CCase arm, int?[]? values)
    {
        var resolved = arm.Labels.Select(i => values is not null && i < values.Length ? values[i] : i).ToList();
        if (values is not null && resolved.Contains(null))
        {
            return ["default:"];
        }

        return resolved.OrderBy(v => v).Select(v => $"case {v!.Value.ToString(CultureInfo.InvariantCulture)}:");
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
        Line(level, "try {");
        Write(attempt.Body, level + 1);
        foreach (var handler in attempt.Catches)
        {
            string type = handler.CatchType is null ? "Throwable" : _naming.ClassName(handler.CatchType);
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
                Line(level, $"throw {Expr(thrown.Exception)};");
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
        return fitted is JNew created ? Created(created, generic) : Expr(fitted);
    }

    /// <summary>A local's declared type: its generic type when the class file gave one, else its descriptor's.</summary>
    private string DeclaredType(string name, string? fallback)
    {
        if (_lifted!.Locals.Signatures.TryGetValue(name, out var signature) && Descriptors.FieldSignature(signature, _naming.ClassName) is { } generic)
        {
            return generic;
        }

        return _naming.Type(_lifted.Locals.Declared.GetValueOrDefault(name) ?? fallback);
    }

    /// <summary>
    /// A value as it is stored into <paramref name="target"/>: typed and fitted to it, and a <c>new</c> of a generic
    /// class written with <c>&lt;&gt;</c> when the target's declared type has type arguments.
    /// </summary>
    private string Value(IrExpr value, IrExpr target)
    {
        string? type = (target as JExpr)?.Type;
        var fitted = Fit(Typed(value, type), type);
        return fitted is JNew created && target is JExpr expression && GenericOf(expression) is { } generic ? Created(created, generic) : Expr(fitted);
    }

    /// <summary><c>new T(…)</c>, or <c>new T&lt;&gt;(…)</c> when the value goes somewhere of a parameterized type and T is generic.</summary>
    private string Created(JNew created, string? targetGeneric)
    {
        bool diamond = targetGeneric is not null && JavaGenerics.IsParameterized(targetGeneric) && JavaGenerics.IsGenericClass(created.Owner, _naming.FindClass);
        return $"new {_naming.ClassName(created.Owner)}{(diamond ? "<>" : string.Empty)}({Arguments(created.Args, created.Descriptor, created.Owner, "<init>")})";
    }

    /// <summary>The generic type of what an expression reads, when the class file says: a local's, a parameter's, a field's of this JAR.</summary>
    private string? GenericOf(JExpr expression) => expression switch
    {
        JLocal local => _lifted?.Locals.Signatures.GetValueOrDefault(local.Name),
        JField field => _naming.FindClass(field.Owner)?.Fields.FirstOrDefault(f => f.Name == field.Name && f.Descriptor == field.FieldType)?.Signature is { } s
                        && JavaGenerics.IsParameterized(s) ? s : null,
        _ => null,
    };

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
        if (expression is JCall { Name: "<init>", Receiver: JLocal { Kind: JLocalKind.This } } chained)
        {
            string keyword = chained.Owner == _naming.Class.Name ? "this" : "super";
            return $"{keyword}({Arguments(chained.Args, chained.Descriptor, chained.Owner, "<init>")})";
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
        JCall call => (CallText(call), Precedence.Primary),
        JNew created => (Created(created, null), Precedence.Primary),
        JUninitialized fresh => ($"new {_naming.ClassName(fresh.Owner)} /* not yet constructed */", Precedence.Primary),
        JNewArray array => (NewArrayText(array), Precedence.Primary),
        JBinary binary => BinaryText(binary),
        JNegate negate => ($"-{Expr(negate.Operand, Precedence.Unary)}", Precedence.Unary),
        JCast { Operand: JCall call } cast when JavaGenerics.CastIsRedundant(cast.CastType, call, GenericOf, _naming.FindClass) => Print(call),
        JCast cast => ($"({_naming.Type(cast.CastType)}) {Expr(cast.Operand, Precedence.Unary)}", Precedence.Cast),
        JInstanceOf test => ($"{Expr(test.Operand, Precedence.Relational)} instanceof {_naming.Type(test.TestedType)}", Precedence.Relational),
        JCompare compare => ($"{CompareOwner(compare)}.compare({Expr(compare.Left)}, {Expr(compare.Right)})", Precedence.Primary),
        JCaught caught => ($"/* caught {_naming.Type(caught.Type)} */", Precedence.Primary),
        JDynamic dynamic => DynamicText(dynamic),
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
        string name = _naming.FieldName(field.Owner, field.Name, field.FieldType);
        return field.Instance is null
            ? field.Owner == _naming.Class.Name ? name : $"{_naming.ClassName(field.Owner)}.{name}"
            : $"{Expr(field.Instance, Precedence.Primary)}.{name}";
    }

    private string CallText(JCall call)
    {
        string name = _naming.MethodName(call.Owner, call.Name, call.Descriptor);
        string args = Arguments(call.Args, call.Descriptor, call.Owner, call.Name);
        switch (call)
        {
            case { Receiver: null }:
                return call.Owner == _naming.Class.Name ? $"{name}({args})" : $"{_naming.ClassName(call.Owner)}.{name}({args})";
            case { Kind: JCallKind.Special, Receiver: JLocal { Kind: JLocalKind.This } } when call.Owner != _naming.Class.Name:
                return $"super.{name}({args})";
            case { Receiver: JLocal { Kind: JLocalKind.This } }:
                return $"this.{name}({args})";
            default:
                return $"{Expr(call.Receiver!, Precedence.Primary)}.{name}({args})";
        }
    }

    /// <summary>Arguments typed by the parameters they fill, so a boolean parameter reads <c>true</c>, not 1.</summary>
    private string Arguments(IReadOnlyList<JExpr> args, string descriptor, string owner, string name)
    {
        var parameters = Descriptors.ParameterDescriptors(descriptor);
        return string.Join(", ", args.Select((a, i) =>
        {
            string? parameter = i < parameters.Count ? parameters[i] : null;
            var argument = Argument(a, parameter);
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
        for (int i = 0; i < dims; i++)
        {
            sb.Append(i < array.Dimensions.Count ? $"[{Expr(array.Dimensions[i])}]" : "[]");
        }

        return sb.ToString();
    }

    private (string, Precedence) DynamicText(JDynamic dynamic)
    {
        if (dynamic.Concat is { } parts)
        {
            var pieces = new List<string>();
            bool startsWithString = parts.Count > 0 && parts[0] is string;
            foreach (var part in parts)
            {
                pieces.Add(part switch
                {
                    string text => Quote(text),
                    int index when index < dynamic.Args.Count => Expr(dynamic.Args[index], Precedence.Additive + 1),
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
            string owner = target.Owner == _naming.Class.Name ? _naming.ClassName(target.Owner) : _naming.ClassName(target.Owner);
            string reference = $"{owner}::{(target.Name == "<init>" ? "new" : _naming.MethodName(target.Owner, target.Name, target.Descriptor))}";
            string capture = dynamic.Args.Count == 0 ? string.Empty : $" /* captures {string.Join(", ", dynamic.Args.Select(a => Expr(a)))} */";
            return ($"({_naming.Type(dynamic.Type)}) {reference}{capture}", Precedence.Cast);
        }

        string bootstrap = dynamic.Bootstrap is null ? string.Empty : $" /* {dynamic.Bootstrap.Replace('/', '.')} */";
        return ($"invokedynamic {dynamic.Name}({string.Join(", ", dynamic.Args.Select(a => Expr(a)))}){bootstrap}", Precedence.Primary);
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

    private static string FloatText(float f) => float.IsNaN(f) ? "Float.NaN"
        : float.IsPositiveInfinity(f) ? "Float.POSITIVE_INFINITY"
        : float.IsNegativeInfinity(f) ? "Float.NEGATIVE_INFINITY"
        : f.ToString("R", CultureInfo.InvariantCulture) + "f";

    private static string DoubleText(double d)
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
                < ' ' or (>= '\u007F' and < ' ') => $"\\u{(int)c:X4}",
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
            && items[first] is CRaw { Statement: JExprStmt { Expression: JCall { Name: "<init>", Owner: "java/lang/Object", Args.Count: 0, Receiver: JLocal { Kind: JLocalKind.This } } } })
        {
            items.RemoveAt(first);
        }

        return new CSeq(items);
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
