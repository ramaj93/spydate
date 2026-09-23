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

    public JavaNaming(ClassFile file, Func<string, string?> given)
    {
        Class = file;
        _given = given;
        Package = file.PackageName;
    }

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
    private LiftedMethod? _lifted;
    private JvmMethod? _method;
    private HashSet<string> _foldedDeclarations = [];
    private string? _returnType;
    private int _printDepth;

    public JavaEmitter(JavaNaming naming) => _naming = naming;

    public StringBuilder Output => _sb;

    /// <summary>Writes a method's body at <paramref name="level"/>, up to its closing brace; the header line carries the opening one.</summary>
    public void Body(JvmMethod method, LiftedMethod lifted, CStmt body, int level, IReadOnlyDictionary<ulong, IrReturn>? returns = null)
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
                foreach (var item in seq.Items)
                {
                    Write(item, level, topLevel);
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
        }
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
                Line(level, $"return {Expr(Typed(value, _returnType))};");
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
        var value = Typed(assign.Src, (assign.Dst as JExpr)?.Type);
        if (assign.Dst is JLocal local && topLevel && _foldedDeclarations.Remove(local.Name))
        {
            string? type = _lifted!.Locals.Declared.GetValueOrDefault(local.Name) ?? local.Type;
            return $"{_naming.Type(type)} {target} = {Expr(value)}";
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

            return $"{target} {binary.Op}= {Expr(Typed(binary.Right, left.Type), Precedence.Assignment + 1)}";
        }

        return $"{target} = {Expr(value)}";
    }

    /// <summary>A constructor call on <c>this</c> is <c>super(...)</c> or <c>this(...)</c>; anything else prints as an expression.</summary>
    private string ExpressionStatement(JExpr expression)
    {
        if (expression is JCall { Name: "<init>", Receiver: JLocal { Kind: JLocalKind.This } } chained)
        {
            string keyword = chained.Owner == _naming.Class.Name ? "this" : "super";
            return $"{keyword}({Arguments(chained.Args, chained.Descriptor)})";
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

    private string Condition(IrExpr condition) => Expr(condition);

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
        JNew created => ($"new {_naming.ClassName(created.Owner)}({Arguments(created.Args, created.Descriptor)})", Precedence.Primary),
        JUninitialized fresh => ($"new {_naming.ClassName(fresh.Owner)} /* not yet constructed */", Precedence.Primary),
        JNewArray array => (NewArrayText(array), Precedence.Primary),
        JBinary binary => BinaryText(binary),
        JNegate negate => ($"-{Expr(negate.Operand, Precedence.Unary)}", Precedence.Unary),
        JCast cast => ($"({_naming.Type(cast.CastType)}) {Expr(cast.Operand, Precedence.Unary)}", Precedence.Cast),
        JInstanceOf test => ($"{Expr(test.Operand, Precedence.Relational)} instanceof {_naming.Type(test.TestedType)}", Precedence.Relational),
        JCompare compare => ($"{CompareOwner(compare)}.compare({Expr(compare.Left)}, {Expr(compare.Right)})", Precedence.Primary),
        JCaught caught => ($"/* caught {_naming.Type(caught.Type)} */", Precedence.Primary),
        JDynamic dynamic => DynamicText(dynamic),
        JConditional conditional => ($"{Expr(conditional.Condition, Precedence.OrElse)} ? {Expr(conditional.Then, Precedence.Conditional)} : {Expr(conditional.Else, Precedence.Conditional)}", Precedence.Conditional),
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
        return ($"{Expr(leftText, precedence)} {op} {Expr(rightText, precedence + 1)}", precedence);
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

        // Operands typed like each other, so a char compared or combined with a constant shows the character.
        var left = binary.Right is JConst ? binary.Left : Typed(binary.Left, binary.Right.Type);
        var right = binary.Left is JConst ? binary.Right : Typed(binary.Right, binary.Left.Type);
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
        string args = Arguments(call.Args, call.Descriptor);
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
    private string Arguments(IReadOnlyList<JExpr> args, string descriptor)
    {
        var parameters = Descriptors.ParameterDescriptors(descriptor);
        return string.Join(", ", args.Select((a, i) => Expr(Typed(a, i < parameters.Count ? parameters[i] : null), Precedence.Assignment + 1)));
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

    private static JExpr Typed(IrExpr expression, string? type) => expression switch
    {
        JConst constant => constant.As(type),
        JExpr j => j,
        _ => new JUnknown(expression.ToString() ?? "?"),
    };

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
        _ => [],
    };

    private static HashSet<string> Names(IrExpr expression)
        => JavaRewrite.PostOrder(expression).OfType<JLocal>().Select(l => l.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>Every node of the tree, regions opened up, iteratively.</summary>
    private static IEnumerable<CStmt> Descendants(CStmt root)
    {
        var stack = new Stack<CStmt>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            switch (node)
            {
                case CSeq s:
                    for (int i = s.Items.Count - 1; i >= 0; i--)
                    {
                        stack.Push(s.Items[i]);
                    }

                    break;
                case CIf i:
                    if (i.Else is not null)
                    {
                        stack.Push(i.Else);
                    }

                    stack.Push(i.Then);
                    break;
                case CLoop l:
                    stack.Push(l.Body);
                    break;
                case CSwitch s:
                    foreach (var arm in Enumerable.Reverse(s.Cases))
                    {
                        stack.Push(arm.Body);
                    }

                    break;
                case JTry t:
                    foreach (var handler in Enumerable.Reverse(t.Catches))
                    {
                        stack.Push(handler.Body);
                    }

                    stack.Push(t.Body);
                    break;
                case CRaw { Statement: JRegion region }:
                    stack.Push(region.Body);
                    break;
            }
        }
    }

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
