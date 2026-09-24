using System.Globalization;
using System.Text;
using Spydate.Core.Jvm;
using Spydate.Core.Project;
using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Structuring;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// A class, or one member of it, written as Java: the declarations from the class file, the bodies from the
/// decompiler, and the class as its source had it rather than as javac split it up.
///
/// Every method is decompiled before anything is printed, because what a class looked like is spread over its
/// methods: an enum's constants and a field's initialiser live in the static initialiser or the constructors, a
/// record's accessors and canonical constructor are generated, an inner class's constructor takes its outer
/// instance and captured locals as parameters javac added. Those are taken out of the methods and put back where
/// the source wrote them. Classes nested in this one are printed inside it; an anonymous class where it is
/// created, a local class just before its first use, and a lambda in place of the synthetic method that holds its
/// body — each through the <see cref="IJavaScope"/> the printer asks. Anything that does not fit the shape
/// expected stays as the compiler left it.
/// </summary>
internal sealed class JavaClassWriter
{
    private const string Indent = "    ";

    private readonly JvmReading _reading;
    private readonly JvmType _type;
    private readonly CancellationToken _cancellationToken;
    private readonly JavaImports _imports;
    private readonly Dictionary<string, Facts> _facts = new(StringComparer.Ordinal);

    /// <summary>Synthetic methods and classes written out in place — a lambda's body, an anonymous or local class — so they are not written again.</summary>
    private readonly HashSet<string> _inlined = new(StringComparer.Ordinal);

    public JavaClassWriter(JvmReading reading, JvmType type, CancellationToken cancellationToken)
    {
        _reading = reading;
        _type = type;
        _cancellationToken = cancellationToken;
        _imports = new JavaImports(name => reading.FindType(name));
    }

    public string Class()
    {
        if (_type.File.Name.EndsWith("/package-info", StringComparison.Ordinal) || _type.File.Name == "package-info")
        {
            return PackageInfo();
        }

        var body = new StringBuilder();
        WriteType(_type, 0, body);
        var top = _type;
        while (top.Outer is { } outer)
        {
            top = outer;
        }

        var (text, imports) = _imports.Resolve(body.ToString(), _type.File.PackageName, top.File.Name, imports: true);
        var sb = new StringBuilder();
        if (_type.File.PackageName.Length > 0)
        {
            sb.Append("package ").Append(_type.File.PackageName.Replace('/', '.')).Append(";\n\n");
        }

        sb.Append(JavaImports.Section(imports));
        sb.Append(text);
        return sb.ToString();
    }

    /// <summary><c>package-info</c> is not a class but the package's annotations: <c>@Deprecated package x.y;</c>.</summary>
    private string PackageInfo()
    {
        var naming = new JavaNaming(_type.File, key => _reading.Annotations?.Get(key)?.Name, name => _reading.FindType(name)?.File);
        var sb = new StringBuilder();
        AnnotationLines(sb, naming, _type.File.Annotations, 0);
        sb.Append("package ").Append(_type.File.PackageName.Replace('/', '.')).Append(";\n");
        return _imports.Resolve(sb.ToString(), _type.File.PackageName, null, imports: false).Text;
    }

    public string Member(JvmMember member)
    {
        var facts = FactsOf(_type);
        var scope = new TypeScope(this, facts);
        var sb = new StringBuilder();
        sb.Append("// in ").Append(_type.KindName).Append(' ').Append(_type.FullName).Append('\n');
        if (member.Field is { } field)
        {
            sb.Append(FieldText(scope, field, null, 0));
        }
        else if (member.Method is { } method)
        {
            var decompiled = Decompile(facts, method);
            sb.Append(MethodText(scope, method, decompiled, 0));
        }

        return _imports.Resolve(sb.ToString(), _type.File.PackageName, null, imports: false).Text;
    }

    // --- facts ------------------------------------------------------------------------------------

    /// <summary>What a class is and which of its parts the compiler made.</summary>
    private sealed class Facts
    {
        public required JvmType Type { get; init; }

        public ClassFile File => Type.File;

        public bool Anonymous => Type.Outer is not null && string.IsNullOrEmpty(Type.Nesting?.SimpleName);

        public bool Local => Type.Outer is not null && Type.Nesting is { Outer: null, SimpleName: { Length: > 0 } };

        public bool IsEnum => (File.Access & JvmAccess.Enum) != 0 && File.SuperName == "java/lang/Enum";

        public bool IsRecord => File.SuperName == "java/lang/Record";

        public Dictionary<string, CtorShape> Ctors { get; } = new(StringComparer.Ordinal);

        /// <summary>Enum constants in declaration order, which is their ordinals'.</summary>
        public List<string> Constants { get; } = [];
    }

    /// <summary>Which of a constructor's parameters the compiler added: the outer instance, captured locals, an enum's name and ordinal.</summary>
    private sealed class CtorShape
    {
        public int OuterParam { get; set; } = -1;

        public Dictionary<int, string> Captures { get; } = [];

        public HashSet<int> Synthetic { get; } = [];

        public required IReadOnlyList<string> Parameters { get; init; }

        /// <summary>The descriptor of the parameters the source wrote.</summary>
        public string SourceDescriptor => $"({string.Concat(Parameters.Where((_, i) => !Synthetic.Contains(i)))})V";

        public IReadOnlyList<T> Keep<T>(IReadOnlyList<T> args) => args.Where((_, i) => !Synthetic.Contains(i)).ToList();
    }

    private Facts FactsOf(JvmType type)
    {
        if (_facts.TryGetValue(type.File.Name, out var known))
        {
            return known;
        }

        var facts = new Facts { Type = type };
        _facts[type.File.Name] = facts;
        foreach (var field in type.File.Fields)
        {
            if ((field.Access & JvmAccess.Enum) != 0)
            {
                facts.Constants.Add(field.Name);
            }
        }

        foreach (var method in type.File.Methods.Where(m => m.IsConstructor))
        {
            facts.Ctors[method.Descriptor] = Shape(facts, method);
        }

        return facts;
    }

    private CtorShape Shape(Facts facts, JvmMethod ctor)
    {
        var parameters = Descriptors.ParameterDescriptors(ctor.Descriptor);
        var shape = new CtorShape { Parameters = parameters };
        if (facts.IsEnum && parameters.Count >= 2 && parameters[0] == "Ljava/lang/String;" && parameters[1] == "I")
        {
            shape.Synthetic.Add(0);
            shape.Synthetic.Add(1);
        }

        var outer = facts.Type.Outer;
        bool instance = facts.Type.Nesting is { Outer: not null } row
            ? (row.Access & JvmAccess.Static) == 0 && !facts.File.IsInterface && !facts.IsEnum && !facts.IsRecord
              && (outer?.File.IsInterface != true)
            : (facts.Anonymous || facts.Local) && InstanceContext(facts);
        if (instance && outer is not null && parameters.Count > 0 && parameters[0] == $"L{outer.File.Name};")
        {
            shape.OuterParam = 0;
            shape.Synthetic.Add(0);
        }

        // javac 8's access constructor, which lets a nested class call a private one: the same parameters and a
        // last one of a class of the compiler's — an empty one, or any anonymous class it had anyway — always passed
        // null, only to tell the two apart. D8 does the same with a -IA class it never even writes out.
        if ((ctor.Access & JvmAccess.Synthetic) != 0 && parameters.Count > 0 && parameters[^1] is ['L', .., ';'] last
            && (_reading.FindType(last[1..^1]) is { } marker
                ? IsCompilerClass(marker) || (string.IsNullOrEmpty(marker.Nesting?.SimpleName) && marker.Outer is not null)
                : last.EndsWith("-IA;", StringComparison.Ordinal)))
        {
            shape.Synthetic.Add(parameters.Count - 1);
        }

        if (ctor.Code is not { } code)
        {
            return shape;
        }

        try
        {
            var lifted = JvmLifter.Lift(facts.File, ctor, code);
            var names = lifted.Locals.Parameters.Select(p => p.Name).ToList();
            foreach (var statement in lifted.Function.AllStatements)
            {
                if (statement is IrAssign { Dst: JField { Instance: JLocal { Kind: JLocalKind.This } } field, Src: JLocal { Kind: JLocalKind.Parameter } parameter }
                    && field.Owner == facts.File.Name && IsSyntheticField(facts.File, field.Name) && names.IndexOf(parameter.Name) is var index and >= 0)
                {
                    if (field.Name.StartsWith("this$", StringComparison.Ordinal))
                    {
                        shape.OuterParam = index;
                    }
                    else
                    {
                        shape.Captures[index] = field.Name;
                    }

                    shape.Synthetic.Add(index);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A constructor that does not lift keeps every parameter it has.
        }

        return shape;
    }

    /// <summary>Whether an anonymous or local class was declared where <c>this</c> exists: in an instance method, or not in a static one.</summary>
    private bool InstanceContext(Facts facts)
    {
        if (facts.Type.Outer is not { } outer)
        {
            return false;
        }

        if (facts.File.EnclosingMethod is { } enclosing)
        {
            var method = outer.File.Methods.FirstOrDefault(m => m.Name == enclosing.Name && m.Descriptor == enclosing.Descriptor);
            return method is not null && (method.Access & JvmAccess.Static) == 0;
        }

        return facts.File.Fields.Any(f => f.Name.StartsWith("this$", StringComparison.Ordinal));
    }

    private static bool IsSyntheticField(ClassFile file, string name)
        => name.StartsWith("this$", StringComparison.Ordinal) || name.StartsWith("val$", StringComparison.Ordinal)
           || file.Fields.FirstOrDefault(f => f.Name == name) is { Access: var access } && (access & JvmAccess.Synthetic) != 0;

    // --- decompiling --------------------------------------------------------------------------------

    /// <summary>A method as the printer takes it: lifted and structured, or why it could not be.</summary>
    private sealed class Decompiled
    {
        public required JvmMethod Method { get; init; }

        public LiftedMethod? Lifted { get; set; }

        public CStmt? Body { get; set; }

        public Dictionary<ulong, IrReturn>? Returns { get; set; }

        public string? Error { get; set; }

        public bool Structured => Returns is null;

        /// <summary>A record's canonical constructor with its component stores gone: written <c>Point { … }</c>.</summary>
        public bool Compact { get; set; }
    }

    private Decompiled Decompile(Facts facts, JvmMethod method, IReadOnlySet<string>? reserved = null)
    {
        var result = new Decompiled { Method = method };
        if (method.Code is not { } code)
        {
            return result;
        }

        try
        {
            try
            {
                result.Lifted = Prepare(facts.File, method, code, reserved);
                var lifted = result.Lifted;
                RemoveNullChecks(lifted.Function);
                EnumComparisons(lifted);
                FinalFieldCopies(facts.File, method, lifted.Function);
                result.Body = JavaShaping.Run(
                    JavaRegions.Structure(lifted),
                    lifted.Locals.IsUntabled,
                    e => e is JExpr j && ((JavaGenerics.GenericOf(j, lifted, FindFile) is { } g && JavaGenerics.IsParameterized(g))

                                          // this, in a class with a signature: its type parameters or a supertype's arguments are known.
                                          || (j is JLocal { Kind: JLocalKind.This } && facts.File.Signature is not null)));
            }
            catch (NotStructurableException ex)
            {
                // Structured without a goto when the graph allows, which javac's always do; otherwise the old way, with them.
                result.Lifted = Prepare(facts.File, method, code, reserved);
                result.Lifted.Function.Warnings.Insert(0, $"shown with gotos: {ex.Message}");
                result.Returns = result.Lifted.Function.Blocks
                    .Where(b => b.Statements is [IrReturn { Value: null or JExpr { IsSimple: true } }])
                    .ToDictionary(b => b.StartVa, b => (IrReturn)b.Statements[0]);
                result.Body = JavaRegions.Structure(result.Lifted, legacy: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Error = $"{ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}";
            result.Body = null;
        }

        return result;
    }

    /// <summary>
    /// The null check javac puts before <c>outer.new Inner()</c> and a bound <c>x::m</c> — <c>Objects.requireNonNull(x)</c>,
    /// or <c>x.getClass()</c> from older compilers — goes: the source had none, and the statement after it checks again.
    /// </summary>
    private void RemoveNullChecks(IrFunction function)
    {
        foreach (var block in function.Blocks)
        {
            var statements = block.Statements;
            for (int i = statements.Count - 2; i >= 0; i--)
            {
                if (statements[i] is not JExprStmt { Expression: var check }
                    || (check is JCall { Owner: "java/util/Objects", Name: "requireNonNull", Args: [var a] } ? a : check is JCall { Name: "getClass", Args.Count: 0, Receiver: { } r } ? r : null) is not { } value)
                {
                    continue;
                }

                bool guards = JavaRewrite.Evaluated(statements[i + 1]).SelectMany(JavaRewrite.PostOrder).Any(e => e switch
                {
                    JNew created => _reading.FindType(created.Owner) is { } type && FactsOf(type).Ctors.GetValueOrDefault(created.Descriptor) is { OuterParam: >= 0 } shape
                                    && shape.OuterParam < created.Args.Count && JavaEquality.Same(created.Args[shape.OuterParam], value),
                    JDynamic { Target: not null, Args: [var bound] } => JavaEquality.Same(bound, value),
                    _ => false,
                });
                if (guards)
                {
                    statements.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>A method lifted, its locals given their types, its temporaries folded back, and its <c>&amp;&amp;</c> / <c>||</c> ladders merged.</summary>
    private LiftedMethod Prepare(ClassFile file, JvmMethod method, CodeAttribute code, IReadOnlySet<string>? reserved)
    {
        var lifted = JvmLifter.Lift(file, method, code, reserved);
        JavaLocals.Run(lifted, method);
        JavaInliner.Run(lifted.Function);
        JavaGenerics.InferLocals(lifted, method, file, FindFile);

        // Conditions are merged across blocks, but never across the edge of a try: its boundaries stay blocks.
        var pinned = code.Handlers.SelectMany(h => new[] { (ulong)h.StartPc, (ulong)h.EndPc, (ulong)h.HandlerPc }).ToHashSet();
        JavaConditions.Merge(lifted.Function, pinned);
        return lifted;
    }

    // --- types --------------------------------------------------------------------------------------

    private void WriteType(JvmType type, int level, StringBuilder sb)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var facts = FactsOf(type);
        var scope = new TypeScope(this, facts);
        var file = type.File;
        ProjectNote(sb, _reading.AnnotationKey(type, null), level);
        AnnotationLines(sb, scope.Naming, file.Annotations, level);
        Pad(sb, level).Append(Declaration(scope)).Append(" {\n");
        sb.Append(Members(scope, level + 1));
        Pad(sb, level).Append("}\n");
    }

    private string Declaration(TypeScope scope)
    {
        var facts = scope.Facts;
        var file = facts.File;
        var naming = scope.Naming;
        string keyword = JvmModifiers.ClassKeyword(file.Access, file.SuperName);
        bool nested = facts.Type.Outer is not null;
        var access = facts.Type.DeclaredAccess;
        var words = JvmModifiers.Class(access, nested).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        // What the kind implies goes: a nested enum, record or interface is static, an enum or record final.
        if (keyword is "enum" or "record" or "interface" or "@interface")
        {
            words.RemoveAll(w => w is "static" or "final" or "abstract");
        }

        if (facts.Local)
        {
            words.RemoveAll(w => w is "static" or "public" or "private" or "protected");
        }

        var sb = new StringBuilder();
        if (words.Count > 0)
        {
            sb.Append(string.Join(' ', words)).Append(' ');
        }

        sb.Append(keyword).Append(' ').Append(DeclaredName(facts.Type));
        var generic = file.Signature is { } signature ? Descriptors.ClassSignature(signature, naming.ClassName) : null;
        if (generic is { TypeParameters.Length: > 0 } g)
        {
            sb.Append(g.TypeParameters);
        }

        if (keyword == "record" && file.RecordComponents is { } components)
        {
            sb.Append('(').Append(string.Join(", ", components.Select(c => $"{GenericOr(naming, c.Signature, c.Descriptor)} {c.Name}"))).Append(')');
        }

        if (file.SuperName is { } super && keyword == "class" && super != "java/lang/Object")
        {
            sb.Append(" extends ").Append(generic is { } withSuper ? withSuper.Super : naming.ClassName(super));
        }

        var interfaces = generic is { } withInterfaces ? withInterfaces.Interfaces.ToList() : file.Interfaces.Select(naming.ClassName).ToList();
        if (keyword == "@interface")
        {
            interfaces = interfaces.Where(i => i != JavaImports.Token("java/lang/annotation/Annotation")).ToList();
        }

        if (interfaces.Count > 0)
        {
            sb.Append(file.IsInterface ? " extends " : " implements ").Append(string.Join(", ", interfaces));
        }

        // The header is outside the class's body, where its own member classes are not in scope by their simple
        // names: they go through the class's name, extends Base<Outer.Result>.
        string header = sb.ToString();
        var pending = new Stack<(JvmType Type, string Path)>(facts.Type.NestedTypes.OfType<JvmType>().Select(t => (t, t.Nesting?.SimpleName ?? string.Empty)));
        while (pending.Count > 0)
        {
            var (member, path) = pending.Pop();
            if (path.Length == 0)
            {
                continue;
            }

            header = header.Replace(JavaImports.Token(member.File.Name), $"{naming.ClassName(file.Name)}.{path}", StringComparison.Ordinal);
            foreach (var deeper in member.NestedTypes.OfType<JvmType>())
            {
                pending.Push((deeper, deeper.Nesting?.SimpleName is { Length: > 0 } simple ? $"{path}.{simple}" : string.Empty));
            }
        }

        return header;
    }

    /// <summary>The name a class is declared under: its simple name, or its binary one when Java gives it none.</summary>
    private string DeclaredName(JvmType type)
    {
        if (_reading.Annotations?.Get(type.File.Name)?.Name is { } given)
        {
            return given.Split('.')[^1];
        }

        return type.Nesting?.SimpleName is { Length: > 0 } simple ? simple : type.File.Name[(type.File.Name.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// Everything in a class body, in the source's order: enum constants, fields with their initialisers, the static
    /// initialiser's remainder, constructors and methods, then the member classes.
    /// </summary>
    private string Members(TypeScope scope, int level)
    {
        var facts = scope.Facts;
        var file = facts.File;
        var methods = file.Methods.Where(m => !IsHiddenMethod(facts, m)).ToList();
        var decompiled = new Dictionary<JvmMethod, Decompiled>(ReferenceEqualityComparer.Instance);
        foreach (var method in methods)
        {
            decompiled[method] = Decompile(facts, method);
        }

        var clinit = methods.FirstOrDefault(m => m.IsStaticInitializer) is { } c ? decompiled[c] : null;
        var ctors = methods.Where(m => m.IsConstructor).Select(m => decompiled[m]).ToList();

        var constants = EnumConstants(facts, clinit);
        var staticInits = StaticInitializers(facts, clinit);
        var instanceInits = InstanceInitializers(facts, ctors);
        foreach (var ctor in ctors)
        {
            RemoveSyntheticStatements(facts, ctor);
        }

        SimplifyRecord(facts, ctors);

        var sb = new StringBuilder();
        var pieces = new List<string>();
        if (constants.Count > 0)
        {
            var constantText = new StringBuilder();
            for (int i = 0; i < constants.Count; i++)
            {
                constantText.Append(EnumConstantText(scope, constants[i], level));
                constantText.Append(i < constants.Count - 1 ? ",\n" : ";\n");
            }

            pieces.Add(constantText.ToString());
        }

        var fieldText = new StringBuilder();
        foreach (var field in file.Fields)
        {
            if (!IsHiddenField(facts, field))
            {
                (IrExpr Value, Decompiled From)? initializer = staticInits.TryGetValue(field.Name, out var s) ? s
                    : instanceInits.TryGetValue(field.Name, out var i) ? i : null;
                fieldText.Append(FieldText(scope, field, initializer, level));
            }
        }

        if (fieldText.Length > 0)
        {
            pieces.Add(fieldText.ToString());
        }

        foreach (var method in methods)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var d = decompiled[method];
            if (IsEmptyAfterRewriting(facts, d, ctors.Count))
            {
                continue;
            }

            pieces.Add(MethodText(scope, method, d, level));
        }

        foreach (var nested in facts.Type.NestedTypes.Cast<JvmType>())
        {
            var nestedFacts = FactsOf(nested);
            if (nestedFacts.Anonymous || nestedFacts.Local || IsCompilerClass(nested))
            {
                continue;
            }

            var nestedText = new StringBuilder();
            WriteType(nested, level, nestedText);
            pieces.Add(nestedText.ToString());
        }

        // Synthetic methods that were not written in place after all — a lambda that could not be, an accessor some call
        // still names — are still there to read, and to compile against.
        foreach (var method in file.Methods.Where(m => IsHiddenMethod(facts, m)
                     && ((IsLambdaBody(m) && !_inlined.Contains(Key(file.Name, m))) || (IsAccessor(m) && (_accessorsNeeded.Contains(Key(file.Name, m)) || !_accessors.ContainsKey(Key(file.Name, m)))))))
        {
            pieces.Add(MethodText(scope, method, Decompile(facts, method), level));
        }

        // Anonymous and local classes no method wrote out — one in a method that did not decompile — as declarations of their own.
        foreach (var nested in facts.Type.NestedTypes.Cast<JvmType>())
        {
            var nestedFacts = FactsOf(nested);
            if ((nestedFacts.Anonymous || nestedFacts.Local) && !_inlined.Contains(nested.File.Name) && !IsEnumConstantBody(facts, nested) && !IsCompilerClass(nested))
            {
                var nestedText = new StringBuilder();
                Pad(nestedText, level).Append("// ").Append(nestedFacts.Anonymous ? "anonymous" : "local").Append(" class, declared in ")
                    .Append(nested.File.EnclosingMethod?.Name ?? "an initialiser").Append('\n');
                WriteType(nested, level, nestedText);
                pieces.Add(nestedText.ToString());
            }
        }

        sb.AppendJoin("\n", pieces);
        return sb.ToString();
    }

    private static string Key(string owner, JvmMethod method) => $"{owner}.{method.Name}{method.Descriptor}";

    /// <summary>A class only the compiler wrote: synthetic, or holding nothing but switch maps.</summary>
    private static bool IsCompilerClass(JvmType type)
        => (type.File.Access & JvmAccess.Synthetic) != 0
           || (type.File.Fields.Count > 0 && type.File.Fields.All(f => f.Name.StartsWith("$SwitchMap$", StringComparison.Ordinal)));

    private static bool IsLambdaBody(JvmMethod method) => method.Name.StartsWith("lambda$", StringComparison.Ordinal);

    private static bool IsAccessor(JvmMethod method)
        => IsAccessorName(method.Name) && (method.Access & (JvmAccess.Static | JvmAccess.Synthetic)) == (JvmAccess.Static | JvmAccess.Synthetic);

    /// <summary>javac 8 calls its accessors <c>access$000</c>; D8, desugaring nestmates for Android, <c>-$$Nest$fgetlog</c>.</summary>
    private static bool IsAccessorName(string name)
        => name.StartsWith("access$", StringComparison.Ordinal) || name.StartsWith("-$$Nest$", StringComparison.Ordinal);

    /// <summary>Accessors some call could not be written out for, which must then be printed for it to compile.</summary>
    private readonly HashSet<string> _accessorsNeeded = new(StringComparer.Ordinal);

    private readonly Dictionary<string, (IReadOnlyList<string> Parameters, JExpr Expression)?> _accessors = new(StringComparer.Ordinal);

    /// <summary>
    /// A synthetic accessor javac wrote so an inner class could reach a private member, written out where it is
    /// called: <c>access$000(x)</c> reading <c>x.secret</c> is <c>x.secret</c>, <c>access$002(x, v)</c> setting it is
    /// <c>x.secret = v</c>, <c>access$100(x, a)</c> calling <c>x.twice(a)</c> is that call. Each parameter must be used
    /// once, so no argument is evaluated twice or not at all.
    /// </summary>
    internal JExpr? AccessorExpression(JCall call)
    {
        if (call is not { Kind: JCallKind.Static } || !IsAccessorName(call.Name)
            || _reading.FindType(call.Owner) is not { } owner
            || owner.File.Methods.FirstOrDefault(m => m.Name == call.Name && m.Descriptor == call.Descriptor) is not { } method || !IsAccessor(method))
        {
            return null;
        }

        string key = Key(call.Owner, method);
        if (!_accessors.TryGetValue(key, out var shape))
        {
            shape = AccessorShape(FactsOf(owner), method);
            _accessors[key] = shape;
        }

        if (shape is not { } known || known.Parameters.Count != call.Args.Count)
        {
            _accessorsNeeded.Add(key);
            return null;
        }

        var arguments = new Dictionary<string, JExpr>(StringComparer.Ordinal);
        for (int i = 0; i < known.Parameters.Count; i++)
        {
            arguments[known.Parameters[i]] = call.Args[i];
        }

        return (JExpr)JavaRewrite.Replace(known.Expression, l => l.Kind == JLocalKind.Parameter && arguments.TryGetValue(l.Name, out var arg) ? arg : null);
    }

    private (IReadOnlyList<string> Parameters, JExpr Expression)? AccessorShape(Facts facts, JvmMethod method)
    {
        var decompiled = Decompile(facts, method);
        if (decompiled is not { Lifted: { } lifted, Body: { } body, Structured: true })
        {
            return null;
        }

        var parameters = lifted.Locals.Parameters.Select(p => p.Name).ToList();
        var items = JavaTree.Items(body).Where(i => !JavaTree.IsEmpty(i) && i is not CRaw { Statement: IrReturn { Value: null } }).ToList();
        JExpr? expression = items switch
        {
            [CRaw { Statement: IrReturn { Value: JExpr value } }] => value,
            [CRaw { Statement: JExprStmt { Expression: var value } }] => value,
            [CRaw { Statement: IrAssign { Dst: JField or JArrayElement, Src: JLocal { Kind: JLocalKind.Parameter } stored } set }, CRaw { Statement: IrReturn { Value: JLocal returned } }]
                when returned.Name == stored.Name => new JAssignExpr((JExpr)set.Dst, stored),
            _ => null,
        };

        if (expression is null)
        {
            return null;
        }

        var mentions = JavaRewrite.PostOrder(expression).OfType<JLocal>().GroupBy(l => l.Name).ToDictionary(g => g.Key, g => g.Count());
        bool eachOnce = parameters.All(p => mentions.GetValueOrDefault(p) == 1)
                        && JavaRewrite.PostOrder(expression).OfType<JLocal>().All(l => l.Kind == JLocalKind.Parameter);
        return eachOnce ? (parameters, expression) : null;
    }

    private bool IsEnumConstantBody(Facts outer, JvmType nested)
        => outer.IsEnum && nested.File.SuperName == outer.File.Name;

    /// <summary>Methods the compiler generated that the source does not have, or that are written elsewhere.</summary>
    private static bool IsHiddenMethod(Facts facts, JvmMethod method)
    {
        var file = facts.File;
        if ((method.Access & (JvmAccess.Synthetic | JvmAccess.VolatileOrBridge)) != 0)
        {
            return true;
        }

        if (facts.IsEnum && (method.Access & JvmAccess.Static) != 0
            && (method is { Name: "values", Descriptor: var d } && d == $"()[L{file.Name};" || method is { Name: "valueOf" } && method.Descriptor == $"(Ljava/lang/String;)L{file.Name};"))
        {
            return true;
        }

        if (facts.IsRecord && file.RecordComponents is { } components)
        {
            if (components.Any(c => method.Name == c.Name && method.Descriptor == $"(){c.Descriptor}") && ReturnsOwnField(file, method))
            {
                return true;
            }

            if (method.Name is "toString" or "hashCode" or "equals" && IsObjectMethodsBootstrap(file, method))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A record accessor as javac writes it: <c>aload_0; getfield; return</c>.</summary>
    private static bool ReturnsOwnField(ClassFile file, JvmMethod method)
        => method.Code is { Code.Length: 5 } code && code.Code.Span[0] == 0x2A && code.Code.Span[1] == 0xB4;

    /// <summary>A record's toString, hashCode or equals left to <c>ObjectMethods.bootstrap</c>.</summary>
    private static bool IsObjectMethodsBootstrap(ClassFile file, JvmMethod method)
    {
        if (method.Code is not { } code)
        {
            return false;
        }

        foreach (var instruction in Bytecode.Decode(code.Code.Span))
        {
            if (instruction.Opcode == 0xBA && file.Pool.Get(instruction.Operand) is { Tag: ConstantTag.InvokeDynamic, A: var bootstrapIndex }
                && bootstrapIndex < file.BootstrapMethods.Count
                && file.Pool.Get(file.BootstrapMethods[bootstrapIndex].MethodHandle) is { Tag: ConstantTag.MethodHandle, B: var handle }
                && file.Pool.Member(handle) is { Owner: "java/lang/runtime/ObjectMethods" })
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHiddenField(Facts facts, JvmField field)
    {
        if ((field.Access & JvmAccess.Synthetic) != 0 || field.Name.StartsWith("this$", StringComparison.Ordinal) || field.Name.StartsWith("val$", StringComparison.Ordinal))
        {
            return true;
        }

        if (facts.IsEnum && ((field.Access & JvmAccess.Enum) != 0 || field.Name == "$VALUES"))
        {
            return true;
        }

        return facts.IsRecord && (field.Access & JvmAccess.Static) == 0 && facts.File.RecordComponents?.Any(c => c.Name == field.Name) == true;
    }

    /// <summary>A constructor or initialiser with nothing left once the compiler's parts are gone.</summary>
    private static bool IsEmptyAfterRewriting(Facts facts, Decompiled d, int ctorCount)
    {
        if (d.Body is null || d.Error is not null || !d.Structured)
        {
            return false;
        }

        var items = JavaTree.Items(d.Body).Where(i => !JavaTree.IsEmpty(i) && !IsImplicitReturn(i) && !IsImplicitSuper(facts, i)).ToList();
        if (d.Method.IsStaticInitializer)
        {
            return items.Count == 0;
        }

        if (!d.Method.IsConstructor || items.Count > 0)
        {
            return false;
        }

        // An anonymous class's constructor, and the one a class gets when it declares none, say nothing.
        if (facts.Anonymous || (facts.IsEnum && facts.Type.NestedTypes.Count == 0 && ctorCount == 1 && Descriptors.ParameterDescriptors(d.Method.Descriptor).Count == 2))
        {
            return true;
        }

        var shape = facts.Ctors.GetValueOrDefault(d.Method.Descriptor);
        int sourceParams = Descriptors.ParameterDescriptors(d.Method.Descriptor).Count - (shape?.Synthetic.Count ?? 0);
        var access = d.Method.Access & (JvmAccess.Public | JvmAccess.Protected | JvmAccess.Private);
        var classAccess = facts.Type.DeclaredAccess & (JvmAccess.Public | JvmAccess.Protected | JvmAccess.Private);
        return ctorCount == 1 && sourceParams == 0 && (access == classAccess || facts.Local || facts.IsEnum);
    }

    private static bool IsImplicitReturn(CStmt item) => item is CRaw { Statement: IrReturn { Value: null } };

    /// <summary><c>super()</c> to a constructor with no arguments, which Java calls without being asked.</summary>
    private static bool IsImplicitSuper(Facts facts, CStmt item)
        => item is CRaw { Statement: JExprStmt { Expression: JCall { Name: "<init>", Args.Count: 0, Receiver: JLocal { Kind: JLocalKind.This } } call } }
           && call.Owner != facts.File.Name;

    // --- class-level rewrites ---------------------------------------------------------------------------

    /// <summary>An enum constant: its name, the arguments the source gave it, and the class of its body, if it has one.</summary>
    private sealed record EnumConstant(string Name, JNew Creation, JvmType? Body, Decompiled Clinit);

    /// <summary>
    /// An enum's constants, taken out of its static initialiser: <c>RED = new Color("RED", 0, "r")</c> is the constant
    /// <c>RED("r")</c>, and <c>$VALUES</c>'s assignment goes with them.
    /// </summary>
    private List<EnumConstant> EnumConstants(Facts facts, Decompiled? clinit)
    {
        var constants = new List<EnumConstant>();
        if (!facts.IsEnum || clinit?.Body is not { } body || !clinit.Structured)
        {
            return constants;
        }

        var items = JavaTree.Items(body).ToList();
        for (int i = 0; i < items.Count; i++)
        {
            switch (items[i])
            {
                case CRaw { Statement: IrAssign { Dst: JField { Instance: null } field, Src: JNew created } }
                    when field.Owner == facts.File.Name && facts.Constants.Contains(field.Name) && created.Args.Count >= 2:
                    var bodyClass = created.Owner != facts.File.Name ? _reading.FindType(created.Owner) : null;
                    constants.Add(new EnumConstant(field.Name, created, bodyClass, clinit));
                    items.RemoveAt(i--);
                    break;
                case CRaw { Statement: IrAssign { Dst: JField { Instance: null, Name: "$VALUES" } values } } when values.Owner == facts.File.Name:
                    items.RemoveAt(i--);
                    break;
            }
        }

        clinit.Body = JavaTree.Sequence(items);
        return constants;
    }

    private string EnumConstantText(TypeScope scope, EnumConstant constant, int level)
    {
        var sb = new StringBuilder();
        Pad(sb, level).Append(constant.Name);
        var args = constant.Creation.Args.Skip(2).ToList();
        var types = Descriptors.ParameterDescriptors(constant.Creation.Descriptor).Skip(2).ToList();

        // Past the name and ordinal, only what the source passed: a constant with a body is made through its class's
        // access constructor, whose marker argument is the compiler's (javac 8's, or D8's -IA).
        if (_reading.FindType(constant.Creation.Owner) is { } created && FactsOf(created).Ctors.GetValueOrDefault(constant.Creation.Descriptor) is { } shape
            && shape.Parameters.Count == constant.Creation.Args.Count)
        {
            var keep = Enumerable.Range(2, constant.Creation.Args.Count - 2).Where(i => !shape.Synthetic.Contains(i)).ToList();
            args = keep.Select(i => constant.Creation.Args[i]).ToList();
            types = keep.Select(i => shape.Parameters[i]).ToList();
        }
        if (args.Count > 0)
        {
            var emitter = new JavaEmitter(scope.Naming);
            sb.Append('(').Append(string.Join(", ", args.Select((a, i) => emitter.Initializer(constant.Clinit.Method, constant.Clinit.Lifted!, a, i < types.Count ? types[i] : null, null, level)))).Append(')');
        }

        if (constant.Body is { } bodyClass)
        {
            _inlined.Add(bodyClass.File.Name);
            var bodyScope = new TypeScope(this, FactsOf(bodyClass));
            sb.Append(" {\n").Append(Members(bodyScope, level + 1));
            Pad(sb, level).Append('}');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Static fields' initialisers, taken from the start of the static initialiser: <c>F = expr</c> for a field of this
    /// class, as long as the assignments come in the order the fields are declared and read no other field of the
    /// class — so moving them to the declarations runs them in the same order. The static block keeps the rest.
    /// </summary>
    private static Dictionary<string, (IrExpr Value, Decompiled From)> StaticInitializers(Facts facts, Decompiled? clinit)
    {
        var found = new Dictionary<string, (IrExpr, Decompiled)>(StringComparer.Ordinal);
        if (clinit?.Body is not { } body || !clinit.Structured)
        {
            return found;
        }

        var fieldOrder = facts.File.Fields.Select(f => f.Name).ToList();
        var items = JavaTree.Items(body).ToList();
        int last = -1;
        int taken = 0;
        foreach (var item in items)
        {
            if (item is CRaw { Statement: IrAssign { Dst: JField { Instance: null } field, Src: var value } }
                && field.Owner == facts.File.Name && facts.File.Fields.FirstOrDefault(f => f.Name == field.Name) is { } declared
                && (declared.Access & JvmAccess.Static) != 0)
            {
                if (IsHiddenField(facts, declared) && field.Name != "$assertionsDisabled")
                {
                    break;
                }

                int position = fieldOrder.IndexOf(field.Name);
                if (field.Name == "$assertionsDisabled" || (position > last && !found.ContainsKey(field.Name) && SelfContained(facts, value)))
                {
                    if (field.Name != "$assertionsDisabled")
                    {
                        found[field.Name] = (value, clinit);
                        last = position;
                    }

                    taken++;
                    continue;
                }
            }

            break;
        }

        clinit.Body = JavaTree.Sequence(items.Skip(taken));
        return found;
    }

    /// <summary>An initialiser that reads no local and no field of the class it initialises.</summary>
    private static bool SelfContained(Facts facts, IrExpr value)
        => !JavaRewrite.PostOrder(value).Any(e => e is JLocal { Kind: not JLocalKind.This } || e is JField f && f.Owner == facts.File.Name)
           && !JavaRewrite.PostOrder(value).Any(e => e is JLocal { Kind: JLocalKind.This });

    /// <summary>
    /// Instance fields' initialisers: the assignments every constructor that calls <c>super(…)</c> makes right after
    /// it, the same in each and reading no parameter — which is what javac makes of <c>private List x = new ArrayList();</c>.
    /// </summary>
    private static Dictionary<string, (IrExpr Value, Decompiled From)> InstanceInitializers(Facts facts, List<Decompiled> ctors)
    {
        var found = new Dictionary<string, (IrExpr, Decompiled)>(StringComparer.Ordinal);
        var chained = ctors.Where(c => c.Body is not null && c.Structured && CallsSuper(facts, c.Body!)).ToList();
        if (chained.Count == 0 || chained.Count != ctors.Count(c => c.Body is not null && !CallsThis(facts, c.Body!)) || facts.IsRecord)
        {
            return found;
        }

        var lists = chained.Select(c => JavaTree.Items(c.Body!).ToList()).ToList();
        var starts = lists.Select(l => l.FindIndex(i => IsSuperCall(facts, i)) + 1).ToList();
        int count = 0;
        while (true)
        {
            if (Enumerable.Range(0, lists.Count).Any(k => starts[k] + count >= lists[k].Count))
            {
                break;
            }

            var first = lists[0][starts[0] + count];
            if (first is not CRaw { Statement: IrAssign { Dst: JField { Instance: JLocal { Kind: JLocalKind.This } } field, Src: var value } }
                || field.Owner != facts.File.Name || found.ContainsKey(field.Name)
                || JavaRewrite.PostOrder(value).Any(e => e is JLocal { Kind: not JLocalKind.This })
                || !Enumerable.Range(0, lists.Count).All(k => JavaEquality.Same(lists[k][starts[k] + count], first)))
            {
                break;
            }

            found[field.Name] = (value, chained[0]);
            count++;
        }

        for (int k = 0; k < chained.Count; k++)
        {
            lists[k].RemoveRange(starts[k], count);
            chained[k].Body = JavaTree.Sequence(lists[k]);
        }

        return found;
    }

    private static bool IsSuperCall(Facts facts, CStmt item)
        => item is CRaw { Statement: JExprStmt { Expression: JCall { Name: "<init>", Receiver: JLocal { Kind: JLocalKind.This } } call } } && call.Owner != facts.File.Name;

    private static bool CallsSuper(Facts facts, CStmt body) => JavaTree.Items(body).Any(i => IsSuperCall(facts, i));

    private static bool CallsThis(Facts facts, CStmt body)
        => JavaTree.Items(body).Any(i => i is CRaw { Statement: JExprStmt { Expression: JCall { Name: "<init>", Receiver: JLocal { Kind: JLocalKind.This } } call } } && call.Owner == facts.File.Name);

    /// <summary>
    /// A constructor without what the compiler added: its stores of the outer instance and captured locals, an enum's
    /// <c>super(name, ordinal)</c>, and an anonymous class's call to its superclass, whose arguments go where it is created.
    /// </summary>
    private void RemoveSyntheticStatements(Facts facts, Decompiled ctor)
    {
        if (ctor.Body is null || !ctor.Structured || !facts.Ctors.TryGetValue(ctor.Method.Descriptor, out var shape))
        {
            return;
        }

        var items = JavaTree.Items(ctor.Body).Where(item => item switch
        {
            CRaw { Statement: IrAssign { Dst: JField { Instance: JLocal { Kind: JLocalKind.This } } field, Src: JLocal { Kind: JLocalKind.Parameter } } }
                => !(field.Owner == facts.File.Name && IsSyntheticField(facts.File, field.Name)),
            CRaw { Statement: JExprStmt { Expression: JCall { Name: "<init>", Owner: "java/lang/Enum" } } } => !facts.IsEnum,
            _ when facts.Anonymous && IsSuperCall(facts, item) => false,
            _ => true,
        }).ToList();
        ctor.Body = JavaTree.Sequence(items);
        _ = shape;
    }

    /// <summary>
    /// A record's canonical constructor without the stores of its components, which Java makes itself: nothing left
    /// means no constructor to write; anything left is the compact form, <c>public Point { … }</c>.
    /// </summary>
    private static void SimplifyRecord(Facts facts, List<Decompiled> ctors)
    {
        if (!facts.IsRecord || facts.File.RecordComponents is not { } components)
        {
            return;
        }

        string canonical = $"({string.Concat(components.Select(c => c.Descriptor))})V";
        foreach (var ctor in ctors.Where(c => c.Method.Descriptor == canonical && c.Body is not null && c.Structured))
        {
            var items = JavaTree.Items(ctor.Body!).Where(i => !IsImplicitReturn(i)).ToList();
            int n = components.Count;
            if (items.Count < n)
            {
                continue;
            }

            bool stores = true;
            for (int k = 0; k < n; k++)
            {
                if (items[items.Count - n + k] is not CRaw { Statement: IrAssign { Dst: JField { Instance: JLocal { Kind: JLocalKind.This } } field, Src: JLocal { Kind: JLocalKind.Parameter } } }
                    || field.Name != components[k].Name)
                {
                    stores = false;
                }
            }

            if (stores)
            {
                items.RemoveRange(items.Count - n, n);
                ctor.Body = JavaTree.Sequence(items);
                ctor.Compact = true;
            }
        }
    }

    // --- members ------------------------------------------------------------------------------------

    private string FieldText(TypeScope scope, JvmField field, (IrExpr Value, Decompiled From)? initializer, int level)
    {
        var sb = new StringBuilder();
        var file = scope.Facts.File;
        var naming = scope.Naming;
        ProjectNote(sb, _reading.AnnotationKey(scope.Facts.Type, scope.Facts.Type.Members.Cast<JvmMember>().FirstOrDefault(m => m.Field == field)), level);
        AnnotationLines(sb, naming, field.Annotations, level);
        Pad(sb, level);
        var modifiers = JvmModifiers.Field(field.Access).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (file.IsInterface)
        {
            modifiers.RemoveAll(m => m is "public" or "static" or "final");
        }

        if (modifiers.Count > 0)
        {
            sb.Append(string.Join(' ', modifiers)).Append(' ');
        }

        sb.Append(GenericOr(naming, field.Signature, field.Descriptor)).Append(' ').Append(naming.FieldName(file.Name, field.Name, field.Descriptor));
        if (field.ConstantValue != 0 && file.Pool.Get(field.ConstantValue) is { } constant)
        {
            sb.Append(" = ").Append(ConstantText(file, constant, field.Descriptor));
        }
        else if (initializer is { } init && init.From.Lifted is { } lifted)
        {
            var emitter = new JavaEmitter(naming);
            string? generic = field.Signature;
            if ((field.Access & JvmAccess.Static) != 0)
            {
                naming.ForwardStatics = file.Fields.SkipWhile(f => !ReferenceEquals(f, field)).Where(f => (f.Access & JvmAccess.Static) != 0)
                    .Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            }

            try
            {
                sb.Append(" = ").Append(emitter.Initializer(init.From.Method, lifted, init.Value, field.Descriptor, generic, level));
            }
            finally
            {
                naming.ForwardStatics = null;
            }
        }

        return sb.Append(";\n").ToString();
    }

    private string MethodText(TypeScope scope, JvmMethod method, Decompiled decompiled, int level)
    {
        var facts = scope.Facts;
        var sb = new StringBuilder();
        var member = facts.Type.Members.Cast<JvmMember>().FirstOrDefault(m => m.Method == method);
        ProjectNote(sb, member is null ? null : _reading.AnnotationKey(facts.Type, member), level);
        AnnotationLines(sb, scope.Naming, method.Annotations, level);
        bool initializer = facts.Anonymous && method.IsConstructor;
        Pad(sb, level).Append(initializer ? string.Empty : MethodHeader(scope, method, decompiled));
        if (method.Code is null)
        {
            if (method.AnnotationDefault is { } value)
            {
                sb.Append(" default ").Append(ElementText(scope.Naming, value));
            }

            return sb.Append(";\n").ToString();
        }

        sb.Append(initializer ? "{\n" : " {\n");
        if (decompiled.Error is { } error || decompiled.Body is null || decompiled.Lifted is null)
        {
            Pad(sb, level + 1).Append("// this method could not be decompiled (").Append(decompiled.Error ?? "no code").Append("); its bytecode view shows it\n");
            Pad(sb, level).Append("}\n");
            return sb.ToString();
        }

        scope.Current = decompiled;
        try
        {
            var emitter = new JavaEmitter(scope.Naming);
            emitter.Body(method, decompiled.Lifted, decompiled.Body, level, decompiled.Returns, structured: decompiled.Structured);
            sb.Append(emitter.Output);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Pad(sb, level + 1).Append("// this method could not be printed (").Append(ex.GetType().Name).Append(": ").Append(ex.Message.ReplaceLineEndings(" ")).Append(")\n");
            Pad(sb, level).Append("}\n");
        }
        finally
        {
            scope.Current = null;
        }

        return sb.ToString();
    }

    private string MethodHeader(TypeScope scope, JvmMethod method, Decompiled decompiled)
    {
        var facts = scope.Facts;
        var file = facts.File;
        var naming = scope.Naming;
        if (method.IsStaticInitializer)
        {
            return "static";
        }

        var sb = new StringBuilder();
        var modifiers = JvmModifiers.Method(method.Access).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (file.IsInterface)
        {
            modifiers.RemoveAll(m => m is "public" || (m == "abstract" && method.Code is null));
            if ((method.Access & (JvmAccess.Abstract | JvmAccess.Static | JvmAccess.Private)) == 0 && method.Code is not null)
            {
                modifiers.Add("default");
            }
        }

        if (method.IsConstructor && (facts.IsEnum || facts.Local))
        {
            modifiers.RemoveAll(m => m is "private" or "public" or "protected");
        }

        if (modifiers.Count > 0)
        {
            sb.Append(string.Join(' ', modifiers)).Append(' ');
        }

        var parameterTypes = Descriptors.ParameterDescriptors(method.Descriptor);
        var generic = method.Signature is { } signature ? Descriptors.MethodSignature(signature, naming.ClassName) : null;
        var shape = method.IsConstructor ? facts.Ctors.GetValueOrDefault(method.Descriptor) : null;
        var kept = Enumerable.Range(0, parameterTypes.Count).Where(i => shape is null || !shape.Synthetic.Contains(i)).ToList();

        // A generic signature lists only the source's parameters; it lines up with them or not at all.
        bool useGeneric = generic is { } g && (g.Parameters.Count == parameterTypes.Count || g.Parameters.Count == kept.Count);
        if (generic is { TypeParameters.Length: > 0 } withTypes)
        {
            sb.Append(withTypes.TypeParameters).Append(' ');
        }

        if (!method.IsConstructor)
        {
            sb.Append(generic is { } withReturn ? withReturn.Return : naming.Type(JCall.ReturnType(method.Descriptor))).Append(' ');
            sb.Append(naming.MethodName(file.Name, method.Name, method.Descriptor));
        }
        else
        {
            sb.Append(DeclaredName(facts.Type));
            if (decompiled.Compact)
            {
                return sb.ToString();
            }
        }

        // The same names the body uses, so a parameter reads the same in the signature and the code.
        var names = decompiled.Lifted?.Locals.Parameters.Select(p => p.Name).ToList()
                    ?? (method.Code is { } code ? new LocalNamer(file, method, code).Parameters.Select(p => p.Name).ToList() : null);
        bool varargs = (method.Access & JvmAccess.TransientOrVarargs) != 0;
        sb.Append('(');
        for (int k = 0; k < kept.Count; k++)
        {
            int i = kept[k];
            int genericIndex = generic is { } gs && gs.Parameters.Count == parameterTypes.Count ? i : k;
            string type = useGeneric ? generic!.Value.Parameters[genericIndex] : naming.Type(parameterTypes[i]);
            if (varargs && k == kept.Count - 1 && type.EndsWith("[]", StringComparison.Ordinal))
            {
                type = type[..^2] + "...";
            }

            string annotations = i < method.ParameterAnnotations.Count ? string.Concat(method.ParameterAnnotations[i].Select(a => AnnotationText(naming, a) + " ")) : string.Empty;
            sb.Append(k > 0 ? ", " : string.Empty).Append(annotations).Append(type).Append(' ').Append(names is not null && i < names.Count ? names[i] : $"arg{i}");
        }

        sb.Append(')');
        var thrown = generic is { Throws.Count: > 0 } withThrows ? withThrows.Throws.ToList() : method.Exceptions.Select(naming.ClassName).ToList();
        if (thrown.Count > 0)
        {
            sb.Append(" throws ").Append(string.Join(", ", thrown));
        }

        return sb.ToString();
    }

    private static string GenericOr(JavaNaming naming, string? signature, string descriptor)
        => signature is not null && Descriptors.FieldSignature(signature, naming.ClassName) is { } generic ? generic : naming.Type(descriptor);

    private static string ConstantText(ClassFile file, Constant constant, string descriptor) => constant.Tag switch
    {
        ConstantTag.String => JavaEmitter.Quote(file.Pool.Utf8(constant.A) ?? string.Empty),
        ConstantTag.Integer when descriptor == "Z" => constant.Bits == 0 ? "false" : "true",
        ConstantTag.Integer when descriptor == "C" => JavaEmitter.CharText((int)constant.Bits),
        ConstantTag.Integer => ((int)constant.Bits).ToString(CultureInfo.InvariantCulture),
        ConstantTag.Long => constant.Bits.ToString(CultureInfo.InvariantCulture) + "L",
        ConstantTag.Float => JavaEmitter.FloatText(BitConverter.Int32BitsToSingle((int)constant.Bits)),
        ConstantTag.Double => JavaEmitter.DoubleText(BitConverter.Int64BitsToDouble(constant.Bits)),
        _ => "/* constant */",
    };

    // --- annotations ------------------------------------------------------------------------------------

    /// <summary>Annotations javac or kotlinc wrote for tools, which a reader of the source never saw.</summary>
    private static readonly HashSet<string> CompilerAnnotations = new(StringComparer.Ordinal)
    {
        "Lkotlin/Metadata;", "Lkotlin/jvm/internal/SourceDebugExtension;", "Ljdk/internal/ValueBased;",
    };

    private static void AnnotationLines(StringBuilder sb, JavaNaming naming, IReadOnlyList<JvmAnnotation> annotations, int level)
    {
        foreach (var annotation in annotations.Where(a => !CompilerAnnotations.Contains(a.Type)))
        {
            Pad(sb, level).Append(AnnotationText(naming, annotation)).Append('\n');
        }
    }

    private static string AnnotationText(JavaNaming naming, JvmAnnotation annotation)
    {
        string type = naming.Type(annotation.Type);
        if (annotation.Elements.Count == 0)
        {
            return $"@{type}";
        }

        if (annotation.Elements is [{ Name: "value" } only])
        {
            return $"@{type}({ElementText(naming, only.Value)})";
        }

        return $"@{type}({string.Join(", ", annotation.Elements.Select(e => $"{e.Name} = {ElementText(naming, e.Value)}"))})";
    }

    private static string ElementText(JavaNaming naming, JvmElementValue value) => value switch
    {
        JvmConstantElement { Tag: 's', Value: string text } => JavaEmitter.Quote(text),
        JvmConstantElement { Tag: 'Z', Value: int bit } => bit != 0 ? "true" : "false",
        JvmConstantElement { Tag: 'C', Value: int c } => JavaEmitter.CharText(c),
        JvmConstantElement { Tag: 'J', Value: long l } => l.ToString(CultureInfo.InvariantCulture) + "L",
        JvmConstantElement { Tag: 'F', Value: float f } => JavaEmitter.FloatText(f),
        JvmConstantElement { Tag: 'D', Value: double d } => JavaEmitter.DoubleText(d),
        JvmConstantElement { Tag: 'B', Value: int b } => $"(byte) {b.ToString(CultureInfo.InvariantCulture)}",
        JvmConstantElement { Tag: 'S', Value: int s } => $"(short) {s.ToString(CultureInfo.InvariantCulture)}",
        JvmConstantElement { Value: int i } => i.ToString(CultureInfo.InvariantCulture),
        JvmEnumElement e => $"{naming.Type(e.Type)}.{e.Constant}",
        JvmClassElement c => $"{naming.Type(c.Descriptor)}.class",
        JvmNestedAnnotation n => AnnotationText(naming, n.Annotation),
        JvmArrayElement { Values: [var single] } => ElementText(naming, single),
        JvmArrayElement a => $"{{{string.Join(", ", a.Values.Select(v => ElementText(naming, v)))}}}",
        _ => "/* value */",
    };

    /// <summary>What the project says about a member: its new name and a comment, as comments above it.</summary>
    private void ProjectNote(StringBuilder sb, string? key, int level)
    {
        if (key is null || _reading.Annotations?.Get(key) is not { } annotation)
        {
            return;
        }

        string by = annotation.Source == AnnotationSource.Agent ? " (agent)" : string.Empty;
        if (annotation.Name is not null)
        {
            Pad(sb, level).Append("// renamed: ").Append(annotation.Name).Append(by).Append('\n');
        }

        if (annotation.Comment is { } comment)
        {
            Pad(sb, level).Append("// ").Append(comment).Append(annotation.Name is null ? by : string.Empty).Append('\n');
        }
    }

    private static StringBuilder Pad(StringBuilder sb, int level)
    {
        for (int i = 0; i < level; i++)
        {
            sb.Append(Indent);
        }

        return sb;
    }

    // --- the scope the printer asks -------------------------------------------------------------------

    /// <summary>The class a method is printed in, answering the printer's questions about what the compiler made.</summary>
    private sealed class TypeScope : IJavaScope
    {
        private readonly JavaClassWriter _writer;

        public TypeScope(JavaClassWriter writer, Facts facts, IReadOnlyDictionary<string, string>? captures = null)
        {
            _writer = writer;
            Facts = facts;
            Captures = captures ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var reading = writer._reading;
            Naming = new JavaNaming(facts.File, key => reading.Annotations?.Get(key)?.Name, name => reading.FindType(name)?.File, this);
        }

        public Facts Facts { get; }

        public JavaNaming Naming { get; }

        /// <summary>What each captured-local field reads as here: <c>val$count</c> is the local the class was created with.</summary>
        public IReadOnlyDictionary<string, string> Captures { get; }

        /// <summary>The method being printed, for a lambda's names and a local class's creation.</summary>
        public Decompiled? Current { get; set; }

        public string? SyntheticField(JField field)
        {
            if (field.Instance is not JLocal { Kind: JLocalKind.This } || !IsSyntheticField(_writer.FindFile(field.Owner) ?? Facts.File, field.Name))
            {
                // this.this$0.x read through another object: only this class's own are known.
                if (field.Instance is JField { Name: var inner } && inner.StartsWith("this$", StringComparison.Ordinal) && field.Name.StartsWith("this$", StringComparison.Ordinal))
                {
                    return $"{Naming.ClassName(field.FieldType[1..^1])}.this";
                }

                return null;
            }

            if (field.Name.StartsWith("this$", StringComparison.Ordinal) && field.FieldType is ['L', .., ';'])
            {
                return $"{Naming.ClassName(field.FieldType[1..^1])}.this";
            }

            if (field.Name.StartsWith("val$", StringComparison.Ordinal))
            {
                return Captures.TryGetValue(field.Name, out var name) ? name : field.Name[4..];
            }

            return null;
        }

        public (JExpr? Outer, IReadOnlyList<JExpr> Args, string Descriptor)? Constructed(JNew created)
        {
            if (_writer._reading.FindType(created.Owner) is not { } type)
            {
                return null;
            }

            var facts = _writer.FactsOf(type);
            if (!facts.Ctors.TryGetValue(created.Descriptor, out var shape) || shape.Synthetic.Count == 0 || created.Args.Count != shape.Parameters.Count)
            {
                return null;
            }

            // A local class's outer instance is implicit; a member class's is written before new.
            JExpr? outer = shape.OuterParam >= 0 && !facts.Local ? created.Args[shape.OuterParam] : null;
            return (outer, shape.Keep(created.Args), shape.SourceDescriptor);
        }

        public string? Anonymous(JNew created, Func<IrArguments, string> arguments, int level)
            => _writer.AnonymousText(this, created, arguments, level);

        public string? Lambda(JDynamic dynamic, int level) => _writer.LambdaText(this, dynamic, level);

        public (JExpr Value, IReadOnlyDictionary<int, string> Names)? EnumSwitch(IrExpr value) => _writer.EnumSwitch(value);

        public string? LocalClass(string internalName, int level) => _writer.LocalClassText(this, internalName, level);

        public string? AnonymousType(string internalName)
        {
            if (_writer._reading.FindType(internalName) is not { } type || !_writer.FactsOf(type).Anonymous)
            {
                return null;
            }

            var file = type.File;
            var generic = file.Signature is { } signature ? Descriptors.ClassSignature(signature, Naming.ClassName) : null;
            return file.SuperName is "java/lang/Object" && file.Interfaces.Count == 1
                ? generic is { Interfaces.Count: 1 } g ? g.Interfaces[0] : Naming.ClassName(file.Interfaces[0])
                : generic is { } withSuper ? withSuper.Super : Naming.ClassName(file.SuperName ?? "java/lang/Object");
        }

        public JExpr? Accessor(JCall call) => _writer.AccessorExpression(call);

        public bool IsLocalClass(string internalName)
            => _writer._reading.FindType(internalName) is { } type && _writer.FactsOf(type).Local && !_writer._inlined.Contains(internalName);
    }

    private ClassFile? FindFile(string internalName) => _reading.FindType(internalName)?.File;

    // --- anonymous, local and lambda ------------------------------------------------------------------

    /// <summary>
    /// <c>new Supplier&lt;Integer&gt;() { … }</c> where javac wrote <c>new Outer$2(this, captured)</c>: the class's members
    /// printed in place, its constructor's arguments split into the superclass's (written in the parentheses) and
    /// the outer instance and captured locals (which the members read by their own names).
    /// </summary>
    private string? AnonymousText(TypeScope scope, JNew created, Func<IrArguments, string> arguments, int level)
    {
        if (_reading.FindType(created.Owner) is not { } type || FactsOf(type) is not { Anonymous: true } facts
            || !facts.Ctors.TryGetValue(created.Descriptor, out var shape) || created.Args.Count != shape.Parameters.Count
            || !_inlined.Add(type.File.Name))
        {
            return null;
        }

        var captures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (index, field) in shape.Captures)
        {
            if (created.Args[index] is JLocal local)
            {
                captures[field] = local.Name;
            }
            else
            {
                _inlined.Remove(type.File.Name);
                return null;
            }
        }

        var file = type.File;
        var naming = scope.Naming;
        var generic = file.Signature is { } signature ? Descriptors.ClassSignature(signature, naming.ClassName) : null;
        string super = file.SuperName is "java/lang/Object" && file.Interfaces.Count == 1
            ? generic is { Interfaces.Count: 1 } g ? g.Interfaces[0] : naming.ClassName(file.Interfaces[0])
            : generic is { } withSuper ? withSuper.Super : naming.ClassName(file.SuperName ?? "java/lang/Object");

        // The superclass's arguments: the constructor's source parameters, in order.
        var superArgs = shape.Keep(created.Args);
        string superDescriptor = shape.SourceDescriptor;
        var inner = new TypeScope(this, facts, captures);
        var sb = new StringBuilder();
        sb.Append("new ").Append(super).Append('(').Append(arguments(new IrArguments(superArgs, superDescriptor, file.SuperName ?? "java/lang/Object"))).Append(") {\n");
        sb.Append(Members(inner, level + 1));
        Pad(sb, level).Append('}');
        return sb.ToString();
    }

    /// <summary>A local class's declaration, printed where its first use is, its captured locals named as the creation passes them.</summary>
    private string? LocalClassText(TypeScope scope, string internalName, int level)
    {
        if (_reading.FindType(internalName) is not { } type || FactsOf(type) is not { Local: true } facts || !_inlined.Add(internalName))
        {
            return null;
        }

        var captures = new Dictionary<string, string>(StringComparer.Ordinal);
        if (scope.Current?.Body is { } body)
        {
            var creation = JavaTree.Descendants(body).SelectMany(ExpressionsOf).SelectMany(JavaRewrite.PostOrder).OfType<JNew>().FirstOrDefault(n => n.Owner == internalName);
            if (creation is not null && facts.Ctors.TryGetValue(creation.Descriptor, out var shape))
            {
                foreach (var (index, field) in shape.Captures)
                {
                    if (index < creation.Args.Count && creation.Args[index] is JLocal local)
                    {
                        captures[field] = local.Name;
                    }
                }
            }
        }

        var inner = new TypeScope(this, facts, captures);
        var sb = new StringBuilder();
        AnnotationLines(sb, inner.Naming, type.File.Annotations, level);
        Pad(sb, level).Append(Declaration(inner)).Append(" {\n");
        sb.Append(Members(inner, level + 1));
        Pad(sb, level).Append("}\n");
        return sb.ToString();
    }

    private static IEnumerable<IrExpr> ExpressionsOf(CStmt statement) => statement switch
    {
        CRaw { Statement: IrAssign a } => [a.Dst, a.Src],
        CRaw { Statement: var s } => JavaRewrite.Evaluated(s),
        CIf i => [i.Condition],
        JLoop l => new IrExpr?[] { l.Condition, (l.Init as IrAssign)?.Src, (l.Update as IrAssign)?.Src }.OfType<IrExpr>(),
        JSwitch s => [s.Value],
        JSynchronized l => [l.Lock],
        _ => [],
    };

    /// <summary>
    /// A lambda in place of the synthetic method holding its body: its captured values become the enclosing method's
    /// own names again, the rest of its parameters the lambda's, and a body that only returns a value is that value.
    /// </summary>
    private string? LambdaText(TypeScope scope, JDynamic dynamic, int level)
    {
        if (dynamic.Target is not { } target || !target.Name.StartsWith("lambda$", StringComparison.Ordinal)
            || _reading.FindType(target.Owner) is not { } owner
            || owner.File.Methods.FirstOrDefault(m => m.Name == target.Name && m.Descriptor == target.Descriptor) is not { Code: not null } method)
        {
            return null;
        }

        // A lambda the project named is a method in its own right: it stays one, referenced by that name.
        if (_reading.Annotations?.Get($"{owner.File.Name}.{method.Name}{method.Descriptor}")?.Name is not null)
        {
            return null;
        }

        bool instance = (method.Access & JvmAccess.Static) == 0;
        var captured = instance ? dynamic.Args.Skip(1).ToList() : dynamic.Args.ToList();
        if (instance && dynamic.Args.FirstOrDefault() is not JLocal { Kind: JLocalKind.This } || captured.Any(a => a is not (JLocal or JConst)))
        {
            return null;
        }

        var reserved = new HashSet<string>(StringComparer.Ordinal);
        if (scope.Current?.Lifted is { } enclosing)
        {
            reserved.UnionWith(enclosing.Locals.Declared.Keys);
            reserved.UnionWith(enclosing.Locals.Parameters.Select(p => p.Name));
        }

        var facts = FactsOf(owner);
        var decompiled = Decompile(facts, method, reserved);
        if (decompiled is not { Lifted: { } lifted, Body: { } body, Structured: true })
        {
            return null;
        }

        var parameters = lifted.Locals.Parameters;
        if (parameters.Count < captured.Count)
        {
            return null;
        }

        var substitutions = new Dictionary<string, JExpr>(StringComparer.Ordinal);
        for (int i = 0; i < captured.Count; i++)
        {
            substitutions[parameters[i].Name] = captured[i];
        }

        body = JavaTree.ReplaceLocals(body, l => l.Kind == JLocalKind.Parameter && substitutions.TryGetValue(l.Name, out var arg) ? arg : null);
        var own = parameters.Skip(captured.Count).Select(p => p.Name).ToList();
        decompiled.Body = body;
        _inlined.Add(Key(owner.File.Name, method));

        var lambdaScope = owner.File.Name == scope.Facts.File.Name ? scope : new TypeScope(this, facts);

        // javac writes int[]::new as a lambda of its own, x$0 -> new int[x$0]: it is the reference again.
        if (captured.Count == 0 && own is [var size]
            && JavaTree.Items(body).Where(i => !JavaTree.IsEmpty(i)).ToList() is [CRaw { Statement: IrReturn { Value: JNewArray { Elements: null, Dimensions: [JLocal { Name: var length }] } created } }]
            && length == size)
        {
            return $"{lambdaScope.Naming.Type(created.ArrayType)}::new";
        }

        var saved = lambdaScope.Current;
        lambdaScope.Current = decompiled;
        try
        {
            var emitter = new JavaEmitter(lambdaScope.Naming);
            return emitter.Lambda(method, lifted, body, level, own);
        }
        finally
        {
            lambdaScope.Current = saved;
        }
    }

    /// <summary>
    /// D8 keeps what it stored in a final field in its register and goes on using the register: <c>x = new
    /// HashMap(); NAMES = x; x.put(…)</c>. The field holds that very object from then on, so after the store the
    /// register reads as the field — <c>NAMES.put(…)</c> — as javac writes it, and the store becomes the field's
    /// initialiser. Only in the static initialiser for a static field, or a constructor for one of this object's.
    /// </summary>
    private static void FinalFieldCopies(ClassFile file, JvmMethod method, IrFunction function)
    {
        if (!method.IsStaticInitializer && !method.IsConstructor)
        {
            return;
        }

        var definitions = function.AllStatements.OfType<IrAssign>().Where(a => a.Dst is JLocal)
            .GroupBy(a => ((JLocal)a.Dst).Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var block in function.Blocks)
        {
            for (int i = 0; i < block.Statements.Count; i++)
            {
                if (block.Statements[i] is not IrAssign { Dst: JField field, Src: JLocal { Kind: JLocalKind.Local or JLocalKind.Temp } copy }
                    || definitions.GetValueOrDefault(copy.Name) != 1 || field.Owner != file.Name
                    || (method.IsStaticInitializer ? field.Instance is not null : field.Instance is not JLocal { Kind: JLocalKind.This })
                    || file.Fields.FirstOrDefault(f => f.Name == field.Name && f.Descriptor == field.FieldType) is not { } declared
                    || (declared.Access & JvmAccess.Final) == 0 || ((declared.Access & JvmAccess.Static) != 0) != method.IsStaticInitializer)
                {
                    continue;
                }

                // Its definition must be in this block, before the store: then every read after the store is after it.
                int definedAt = block.Statements.FindIndex(st => st is IrAssign { Dst: JLocal d } && d.Name == copy.Name);
                if (definedAt < 0 || definedAt > i)
                {
                    continue;
                }

                for (int j = i + 1; j < block.Statements.Count; j++)
                {
                    block.Statements[j] = JavaRewrite.Replace(block.Statements[j], l => l.Name == copy.Name ? field : null);
                }

                foreach (var other in function.Blocks.Where(b => !ReferenceEquals(b, block)))
                {
                    for (int j = 0; j < other.Statements.Count; j++)
                    {
                        other.Statements[j] = JavaRewrite.Replace(other.Statements[j], l => l.Name == copy.Name ? field : null);
                    }
                }
            }
        }
    }

    /// <summary>
    /// D8 writes a switch on an enum with few cases as comparisons of the switch map's number with each case's —
    /// <c>x = $SwitchMap$…[e.ordinal()]; if (x == 1) …</c> — which the switch map's hidden class would have to be
    /// printed for. They are comparisons of the enum: <c>e == TimeUnit.SECONDS</c>, the number read back as its
    /// constant through the switch map, and the local that held it holds the enum.
    /// </summary>
    private void EnumComparisons(LiftedMethod lifted)
    {
        var statements = lifted.Function.Blocks.SelectMany(b => b.Statements).ToList();

        // Dalvik reads the map and the ordinal into registers of their own: a local stored once is what was stored.
        var single = statements.OfType<IrAssign>().Where(a => a.Dst is JLocal)
            .GroupBy(a => ((JLocal)a.Dst).Name, StringComparer.Ordinal).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Src, StringComparer.Ordinal);
        IrExpr Resolve(IrExpr expression, int depth)
            => depth > 4 ? expression : JavaRewrite.Replace(expression, l => single.TryGetValue(l.Name, out var v) && v is JExpr value and (JField or JCall { Name: "ordinal" }) ? (JExpr)Resolve(value, depth + 1) : null);

        var holders = new Dictionary<string, (JExpr Receiver, IReadOnlyDictionary<int, string> Names)>(StringComparer.Ordinal);
        foreach (var statement in statements)
        {
            if (statement is IrAssign { Dst: JLocal local, Src: JArrayElement raw } && Resolve(raw, 0) is JArrayElement { Array: JField { Name: var map } } element
                && map.StartsWith("$SwitchMap$", StringComparison.Ordinal) && EnumSwitch(element) is { } found
                && statements.Count(x => x is IrAssign { Dst: JLocal other } && other.Name == local.Name) == 1)
            {
                holders[local.Name] = found;
            }
        }

        // Only a local read nowhere else than in such comparisons becomes the enum.
        foreach (string name in holders.Keys.ToList())
        {
            int reads = statements.SelectMany(JavaRewrite.Evaluated).SelectMany(JavaRewrite.PostOrder).Count(e => e is JLocal l && l.Name == name);
            int compared = statements.OfType<IrBranch>().Count(b => Compared(b.Condition, name, holders[name].Names) is not null);
            if (reads != compared)
            {
                holders.Remove(name);
            }
        }

        bool rewritten = false;
        foreach (var block in lifted.Function.Blocks)
        {
            for (int i = 0; i < block.Statements.Count; i++)
            {
                switch (block.Statements[i])
                {
                    case IrAssign { Dst: JLocal local } when holders.TryGetValue(local.Name, out var holder) && holder.Receiver is JLocal:
                        // A local or parameter: the comparisons read it directly, and the holder goes.
                        block.Statements.RemoveAt(i--);
                        break;
                    case IrAssign { Dst: JLocal local } assign when holders.TryGetValue(local.Name, out var holder):
                    {
                        var retyped = lifted.Locals.Retype(local, holder.Receiver.Type);
                        block.Statements[i] = assign with { Dst = retyped, Src = holder.Receiver };
                        break;
                    }

                    case IrBranch { Condition: IrCondition { Cc: IrCondCode.Equal or IrCondCode.NotEqual } condition } branch:
                    {
                        // The holder, or the map's element read in place.
                        (JExpr Value, IReadOnlyDictionary<int, string> Names)? subject =
                            condition.Left is JLocal l && holders.TryGetValue(l.Name, out var held)
                                ? (held.Receiver is JLocal ? held.Receiver : lifted.Locals.Retype(l, held.Receiver.Type), held.Names)
                            : condition.Left is JArrayElement { Array: JField { Name: var map } } element && map.StartsWith("$SwitchMap$", StringComparison.Ordinal) && EnumSwitch(element) is { } direct ? direct
                            : null;
                        if (subject is { } s2 && condition.Right is JConst { Value: int k } && s2.Names.TryGetValue(k, out var constant)
                            && s2.Value.Type is ['L', .. var enumName, ';'] enumType)
                        {
                            block.Statements[i] = branch with { Condition = condition with { Left = s2.Value, Right = new JField(null, enumName, constant, enumType) } };
                            rewritten = true;
                        }

                        break;
                    }
                }
            }
        }

        if (rewritten)
        {
            RemoveUnread(lifted.Function);
        }
    }

    /// <summary>
    /// What reading the switch map left behind once nothing reads it — the map in a register, the ordinal in
    /// another — goes: a field read and <c>ordinal()</c> do nothing else.
    /// </summary>
    private static void RemoveUnread(IrFunction function)
    {
        var reads = function.AllStatements.SelectMany(JavaRewrite.Evaluated).SelectMany(JavaRewrite.PostOrder).OfType<JLocal>()
            .GroupBy(l => l.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var block in function.Blocks)
        {
            block.Statements.RemoveAll(st => st is IrAssign { Dst: JLocal { Kind: not (JLocalKind.Parameter or JLocalKind.This) } local, Src: var value }
                                             && reads.GetValueOrDefault(local.Name) == 0
                                             && value is JCall { Name: "ordinal", Args.Count: 0 } or JField { Name: ['$', 'S', 'w', 'i', 't', 'c', 'h', 'M', 'a', 'p', '$', ..] });
        }
    }

    /// <summary>The case number a condition compares a local with, when it is an equality with a number the switch map names.</summary>
    private static int? Compared(IrExpr condition, string name, IReadOnlyDictionary<int, string> names)
        => condition is IrCondition { Cc: IrCondCode.Equal or IrCondCode.NotEqual, Left: JLocal { } l, Right: JConst { Value: int k } } && l.Name == name && names.ContainsKey(k) ? k : null;

    /// <summary>
    /// A switch on an enum, from what javac made of it: <c>e.ordinal()</c> for an enum of the same compilation, whose
    /// constants' order is their ordinals', or <c>$SwitchMap$…[e.ordinal()]</c>, whose case numbers the switch map's
    /// class assigns in its static initialiser.
    /// </summary>
    private (JExpr Value, IReadOnlyDictionary<int, string> Names)? EnumSwitch(IrExpr value)
    {
        switch (value)
        {
            case JCall { Name: "ordinal", Descriptor: "()I", Receiver: { Type: ['L', .. var enumName, ';'] } receiver }
                when _reading.FindType(enumName) is { } enumType && FactsOf(enumType) is { IsEnum: true } facts:
            {
                var names = new Dictionary<int, string>();
                for (int i = 0; i < facts.Constants.Count; i++)
                {
                    names[i] = facts.Constants[i];
                }

                return (receiver, names);
            }

            case JArrayElement { Array: JField { Instance: null, Name: var mapName } map, Index: JCall { Name: "ordinal", Receiver: { } receiver } }
                when mapName.StartsWith("$SwitchMap$", StringComparison.Ordinal) && SwitchMap(map.Owner, mapName) is { Count: > 0 } names:
                return (receiver, names);

            default:
                return null;
        }
    }

    /// <summary>A switch map's case numbers, from the static initialiser that fills it: <c>map[E.X.ordinal()] = k</c>.</summary>
    private Dictionary<int, string>? SwitchMap(string owner, string field)
    {
        if (_reading.FindType(owner)?.File is not { } file
            || file.Methods.FirstOrDefault(m => m.IsStaticInitializer) is not { Code: { } code } clinit)
        {
            return null;
        }

        try
        {
            var lifted = JvmLifter.Lift(file, clinit, code);
            JavaInliner.Run(lifted.Function);

            // Dalvik keeps each value in a register: a local stored once is read as what was stored in it.
            var single = lifted.Function.AllStatements.OfType<IrAssign>().Where(a => a.Dst is JLocal)
                .GroupBy(a => ((JLocal)a.Dst).Name, StringComparer.Ordinal).Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.First().Src, StringComparer.Ordinal);
            IrExpr Resolve(IrExpr expression, int depth)
                => depth > 8 ? expression : JavaRewrite.Replace(expression, l => single.TryGetValue(l.Name, out var v) && v is JExpr value ? (JExpr)Resolve(value, depth + 1) : null);

            // D8 may keep the new array in its register and store through it rather than read the field back.
            var holders = lifted.Function.AllStatements
                .Select(st => st is IrAssign { Dst: JField { Name: var stored }, Src: JLocal holder } && stored == field ? holder.Name : null)
                .OfType<string>().ToHashSet(StringComparer.Ordinal);

            var names = new Dictionary<int, string>();
            foreach (var original in lifted.Function.AllStatements)
            {
                bool throughHolder = original is IrAssign { Dst: JArrayElement { Array: JLocal array } } && holders.Contains(array.Name);
                var statement = original is IrAssign assign ? assign with { Dst = Resolve(assign.Dst, 0), Src = Resolve(assign.Src, 0) } : original;
                if (statement is IrAssign { Dst: JArrayElement { Array: var target, Index: JCall { Name: "ordinal", Receiver: JField { Instance: null } constant } }, Src: JConst { Value: int k } }
                    && (throughHolder || target is JField { Name: var name } && name == field))
                {
                    names[k] = constant.Name;
                }
            }

            return names;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
