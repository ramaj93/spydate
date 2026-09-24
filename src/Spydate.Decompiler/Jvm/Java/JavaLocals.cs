using Spydate.Core.Jvm;
using Spydate.Decompiler.Native.IR;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// Gives the locals the table did not describe their real identities and types.
///
/// A JVM slot is a register: javac reuses one for variables in different scopes, and a shrinker reuses them for
/// anything, parameters included. Without a local variable table one name covers them all, typed by whichever
/// value came first — a catch variable, then an unrelated string. So each untabled name is split into its webs:
/// the definitions a use can see are joined (reaching definitions, with every protected block feeding its
/// handlers), and each group of definitions and the uses they reach is one variable, typed by what is stored in it.
///
/// Then the ints: the JVM has no boolean, char, byte or short locals, only ints. A local (or a value the lifter
/// held on the stack) is a boolean when everything stored in it is a boolean or 0 or 1 and nothing uses it as a
/// number, a char when it holds chars and something reads it as one, and so on — constraints that only ever
/// narrow, solved to a fixed point, with int the answer whenever they disagree. A value used where its declared
/// type does not fit is cast by the printer.
/// </summary>
internal static class JavaLocals
{
    private const int MaxRounds = 32;

    public static void Run(LiftedMethod lifted, JvmMethod method)
    {
        var graph = new Graph(lifted);
        foreach (string name in graph.Names())
        {
            SplitWebs(lifted, graph, name);
        }

        Retype(lifted);
        SmallInts(lifted, JCall.ReturnType(method.Descriptor));
    }

    /// <summary>
    /// Untabled reference locals typed again from what is stored in them, now that every web has its own name and
    /// type: a web split off late may have been typed from an array read whose array had not been split yet.
    /// </summary>
    private static void Retype(LiftedMethod lifted)
    {
        for (int round = 0; round < 3; round++)
        {
            var stores = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var current = new Dictionary<string, JLocal>(StringComparer.Ordinal);
            foreach (var statement in lifted.Function.AllStatements)
            {
                if (statement is IrAssign { Dst: JLocal { Kind: JLocalKind.Local, Type: ['L' or '[', ..] } local, Src: JExpr value } && lifted.Locals.IsUntabled(local.Name))
                {
                    current[local.Name] = local;
                    if (!stores.TryGetValue(local.Name, out var types))
                    {
                        types = [];
                        stores[local.Name] = types;
                    }

                    if (value is not JConst { Value: null } && value.Type is { } type)
                    {
                        types.Add(type);
                    }
                }
            }

            var retyped = new Dictionary<string, JLocal>(StringComparer.Ordinal);
            foreach (var (name, types) in stores)
            {
                if (types.Count == 1 && types.First() is ['L' or '[', ..] only && only != current[name].Type)
                {
                    retyped[name] = lifted.Locals.Retype(current[name], only);
                }
            }

            if (retyped.Count == 0)
            {
                return;
            }

            foreach (var block in lifted.Function.Blocks)
            {
                for (int s = 0; s < block.Statements.Count; s++)
                {
                    var statement = JavaRewrite.Replace(block.Statements[s], l => retyped.GetValueOrDefault(l.Name));
                    if (statement is IrAssign { Dst: JLocal target } assign && retyped.TryGetValue(target.Name, out var local))
                    {
                        statement = assign with { Dst = local };
                    }

                    block.Statements[s] = statement;
                }
            }
        }
    }

    // --- webs ------------------------------------------------------------------------------------

    /// <summary>The method's blocks with exceptional edges: a protected block feeds each of its handlers.</summary>
    private sealed class Graph
    {
        public Graph(LiftedMethod lifted)
        {
            Lifted = lifted;
            Blocks = lifted.Function.Blocks;
            var index = new Dictionary<ulong, int>();
            for (int i = 0; i < Blocks.Count; i++)
            {
                index.TryAdd(Blocks[i].StartVa, i);
            }

            Entry = index.GetValueOrDefault(lifted.Function.EntryVa);
            Successors = new List<int>[Blocks.Count];
            Handlers = new List<int>[Blocks.Count];
            ReadNames = new HashSet<string>[Blocks.Count][];
            Defined = new string?[Blocks.Count][];
            for (int i = 0; i < Blocks.Count; i++)
            {
                var statements = Blocks[i].Statements;
                ReadNames[i] = new HashSet<string>[statements.Count];
                Defined[i] = new string?[statements.Count];
                for (int s = 0; s < statements.Count; s++)
                {
                    var read = JavaRewrite.Evaluated(statements[s]).SelectMany(JavaRewrite.PostOrder).OfType<JLocal>().Select(l => l.Name).ToHashSet(StringComparer.Ordinal);
                    ReadNames[i][s] = read.Count == 0 ? NoNames : read;
                    Defined[i][s] = statements[s] is IrAssign { Dst: JLocal defined } ? defined.Name : null;
                    foreach (string name in Defined[i][s] is { } stored ? read.Append(stored) : read)
                    {
                        if (!Positions.TryGetValue(name, out var at))
                        {
                            at = [];
                            Positions[name] = at;
                        }

                        if (at.Count == 0 || at[^1] != (i, s))
                        {
                            at.Add((i, s));
                        }
                    }
                }

                Successors[i] = Blocks[i].Successors.Where(index.ContainsKey).Select(s => index[s]).Distinct().ToList();
                Handlers[i] = lifted.Handlers
                    .Where(h => (int)Blocks[i].StartVa >= h.StartPc && (int)Blocks[i].StartVa < h.EndPc && index.ContainsKey((ulong)h.HandlerPc))
                    .Select(h => index[(ulong)h.HandlerPc]).Distinct().ToList();
            }
        }

        public LiftedMethod Lifted { get; }

        public List<IrBlock> Blocks { get; }

        public int Entry { get; }

        public List<int>[] Successors { get; }

        /// <summary>Handlers each block's code can throw to.</summary>
        public List<int>[] Handlers { get; }

        /// <summary>The locals each statement reads, by block and position: worked out once, not once per name.</summary>
        public HashSet<string>[][] ReadNames { get; }

        /// <summary>The local each statement stores to, if any.</summary>
        public string?[][] Defined { get; }

        /// <summary>Where each name is read or stored, in block and statement order.</summary>
        public Dictionary<string, List<(int Block, int Index)>> Positions { get; } = new(StringComparer.Ordinal);

        private static readonly HashSet<string> NoNames = new(StringComparer.Ordinal);

        /// <summary>The untabled locals and parameters the method stores to.</summary>
        public IEnumerable<string> Names()
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var statement in Blocks.SelectMany(b => b.Statements))
            {
                if (statement is IrAssign { Dst: JLocal { Kind: JLocalKind.Local or JLocalKind.Parameter } local } && Lifted.Locals.IsUntabled(local.Name))
                {
                    names.Add(local.Name);
                }
            }

            return names;
        }
    }

    /// <summary>
    /// A store to a reference local of a value of another type than the one it reads from it — and a cast of the
    /// local stored back into it, <c>x = (Entry) x</c>, which Dalvik writes for every checked cast.
    /// </summary>
    private static bool Retyped(IrStmt statement, string name)
        => statement is IrAssign { Dst: JLocal { Type: ['L' or '[', ..] }, Src: JExpr { Type: ['L' or '[', ..] and var stored } value }
           && JavaRewrite.PostOrder(value).OfType<JLocal>().FirstOrDefault(l => l.Name == name) is { Type: ['L' or '[', ..] and var read }
           && read != stored && (read != "Ljava/lang/Object;" || value is JCast { Operand: JLocal { } cast } && cast.Name == name);

    private static void SplitWebs(LiftedMethod lifted, Graph graph, string name)
    {
        var blocks = graph.Blocks;

        // Definition sites; a parameter's value on entry is one more, numbered last.
        var sites = new List<(int Block, int Index)>();
        JLocal? original = null;
        bool parameter = false;
        var positions = graph.Positions.GetValueOrDefault(name) ?? [];
        foreach (var (b, s) in positions)
        {
            if (graph.Defined[b][s] == name && blocks[b].Statements[s] is IrAssign { Dst: JLocal local })
            {
                sites.Add((b, s));
                original ??= local;
                parameter |= local.Kind == JLocalKind.Parameter;
            }
        }

        if (original is null)
        {
            return;
        }

        var siteIndex = new Dictionary<(int, int), int>();
        for (int d = 0; d < sites.Count; d++)
        {
            siteIndex[sites[d]] = d;
        }

        int entryDef = sites.Count;
        int total = sites.Count + (parameter ? 1 : 0);
        if (total < 2)
        {
            return;
        }

        // Reaching definitions, to a fixed point, as bitsets over the definitions. A block hands on its last store
        // when it has one and what reached it otherwise, so only the blocks' edges are walked, not their statements.
        int words = (total + 63) / 64;
        var into = new ulong[blocks.Count * words];
        var all = new ulong[blocks.Count * words];
        var lastStore = new int[blocks.Count];
        Array.Fill(lastStore, -1);
        for (int d = 0; d < sites.Count; d++)
        {
            all[(sites[d].Block * words) + (d / 64)] |= 1UL << (d % 64);
            lastStore[sites[d].Block] = Math.Max(lastStore[sites[d].Block], d);
        }

        if (parameter)
        {
            into[(graph.Entry * words) + (entryDef / 64)] |= 1UL << (entryDef % 64);
        }

        var work = new Queue<int>(Enumerable.Range(0, blocks.Count));
        var queued = new bool[blocks.Count];
        Array.Fill(queued, true);
        var outgoing = new ulong[words];
        while (work.Count > 0)
        {
            int b = work.Dequeue();
            queued[b] = false;
            if (lastStore[b] >= 0)
            {
                Array.Clear(outgoing);
                outgoing[lastStore[b] / 64] = 1UL << (lastStore[b] % 64);
            }
            else
            {
                Array.Copy(into, b * words, outgoing, 0, words);
            }

            foreach (int next in graph.Successors[b])
            {
                if (Grow(into, next * words, outgoing, 0, words) && !queued[next])
                {
                    work.Enqueue(next);
                    queued[next] = true;
                }
            }

            // Anything in the block can throw, so a handler sees what reached the block and every store in it.
            foreach (int handler in graph.Handlers[b])
            {
                if ((Grow(into, handler * words, into, b * words, words) | Grow(into, handler * words, all, b * words, words)) && !queued[handler])
                {
                    work.Enqueue(handler);
                    queued[handler] = true;
                }
            }
        }

        // A use joins every definition that reaches it into one variable.
        var parent = Enumerable.Range(0, total).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        var readWeb = new Dictionary<(int, int), int>();
        var read = new HashSet<int>();
        HashSet<int>? current = null;
        int currentBlock = -1;
        foreach (var (b, s) in positions)
        {
            if (b != currentBlock)
            {
                currentBlock = b;
                current = [];
                for (int d = 0; d < total; d++)
                {
                    if ((into[(b * words) + (d / 64)] & (1UL << (d % 64))) != 0)
                    {
                        current.Add(d);
                    }
                }
            }

            if (current!.Count > 0 && graph.ReadNames[b][s].Contains(name))
            {
                int first = current.First();
                foreach (int d in current)
                {
                    parent[Find(d)] = Find(first);
                    read.Add(d);
                }

                readWeb[(b, s)] = first;
            }

            if (graph.Defined[b][s] == name)
            {
                // x = x + 2 updates one variable: the store joins the variable it read — unless what it stores is
                // another kind of thing, x = x.iterator() in a slot a shrinker reused.
                if (readWeb.TryGetValue((b, s), out int updated) && !Retyped(blocks[b].Statements[s], name))
                {
                    parent[Find(siteIndex[(b, s)])] = Find(updated);
                }

                current = [siteIndex[(b, s)]];
            }
        }

        // A store nothing reads (javac's `int r = 0` before every path assigns r again) is the same variable as the
        // next store of the same kind, not a variable of its own — unless it stores a caught exception: that is a
        // catch clause's variable, which nothing after the handler can see.
        var order = Enumerable.Range(0, sites.Count).OrderBy(d => blocks[sites[d].Block].StartVa).ThenBy(d => sites[d].Index).ToList();
        foreach (var dead in Enumerable.Range(0, total).GroupBy(Find).Where(w => !w.Any(read.Contains) && !w.Contains(entryDef)).ToList())
        {
            if (dead.Any(d => blocks[sites[d].Block].Statements[sites[d].Index] is IrAssign { Src: JCaught }))
            {
                continue;
            }

            int last = dead.Max(d => order.IndexOf(d));
            var kind = Category(blocks[sites[dead.First()].Block].Statements[sites[dead.First()].Index]);
            int next = order.Skip(last + 1).FirstOrDefault(d => read.Contains(d) && Category(blocks[sites[d].Block].Statements[sites[d].Index]) == kind, -1);
            if (next >= 0)
            {
                parent[Find(dead.Key)] = Find(next);
            }
        }

        var webs = Enumerable.Range(0, total).GroupBy(Find).ToList();
        if (webs.Count < 2)
        {
            return;
        }

        // The web a parameter enters with, or else the earliest store, keeps the name; each other one gets its own.
        int Earliest(IGrouping<int, int> web) => web.Min(d => d == entryDef ? -1 : (int)blocks[sites[d].Block].Statements[sites[d].Index].Va);
        var primary = parameter ? webs.First(w => w.Contains(entryDef)) : webs.OrderBy(Earliest).First();
        var localOf = new Dictionary<int, JLocal>();
        foreach (var web in webs.OrderBy(Earliest))
        {
            string? type = WebType(web.Where(d => d != entryDef).Select(d => (IrAssign)blocks[sites[d].Block].Statements[sites[d].Index]), original.Type);
            JLocal local;
            if (ReferenceEquals(web, primary))
            {
                local = parameter ? original with { Kind = JLocalKind.Parameter } : lifted.Locals.Retype(original, type);
            }
            else
            {
                local = lifted.Locals.Split(name, type);
            }

            localOf[web.Key] = local;
        }

        foreach (var (b, s) in positions)
        {
            var statement = blocks[b].Statements[s];
            if (readWeb.TryGetValue((b, s), out int reached))
            {
                var reader = localOf[Find(reached)];
                statement = JavaRewrite.Replace(statement, l => l.Name == name ? reader : null);
            }

            if (statement is IrAssign { Dst: JLocal local } assign && local.Name == name)
            {
                statement = assign with { Dst = localOf[Find(siteIndex[(b, s)])] };
            }

            blocks[b].Statements[s] = statement;
        }
    }

    /// <summary>What sort of value a store writes: an int-like, a long, a float, a double, or a reference.</summary>
    private static char Category(IrStmt store) => store is IrAssign { Src: JExpr { Type: [var c, ..] } }
        ? c switch
        {
            'Z' or 'C' or 'B' or 'S' or 'I' => 'I',
            'L' or '[' => 'L',
            _ => c,
        }
        : '?';

    /// <summary>ORs <paramref name="words"/> words of a bitset into another; true when that added anything.</summary>
    private static bool Grow(ulong[] target, int targetAt, ulong[] source, int sourceAt, int words)
    {
        bool grew = false;
        for (int w = 0; w < words; w++)
        {
            ulong before = target[targetAt + w];
            ulong after = before | source[sourceAt + w];
            if (after != before)
            {
                target[targetAt + w] = after;
                grew = true;
            }
        }

        return grew;
    }

    /// <summary>
    /// The type of the values a web stores: the one they share, else the declared one. A null says nothing; an int
    /// kind stays as the slot's kind for the int pass to narrow.
    /// </summary>
    private static string? WebType(IEnumerable<IrAssign> stores, string? declared)
    {
        var types = stores.Select(a => a.Src is JConst { Value: null } ? null : (a.Src as JExpr)?.Type).Where(t => t is not null).Distinct().ToList();
        if (declared is "I" or "J" or "F" or "D" or null || types.Count == 0)
        {
            return declared is "I" ? "I" : types.Count == 1 ? types[0] : declared;
        }

        return types.Count == 1 && types[0] is ['L' or '[', ..] ? types[0] : declared;
    }

    // --- booleans, chars, bytes and shorts -----------------------------------------------------------

    [Flags]
    private enum Kinds
    {
        None = 0,
        Z = 1,
        C = 2,
        B = 4,
        S = 8,
        I = 16,
        All = Z | C | B | S | I,
    }

    private sealed class Node
    {
        public Kinds Possible = Kinds.All;
        public Kinds Evidence;
        public bool OnlyFlags = true;
        public bool Used;
        public readonly List<string> CopiedFrom = [];
        public readonly List<string> CopiedTo = [];
        public JLocal Local = null!;
    }

    private static Kinds Of(string? type) => type switch
    {
        "Z" => Kinds.Z,
        "C" => Kinds.C,
        "B" => Kinds.B,
        "S" => Kinds.S,
        "I" => Kinds.I,
        _ => Kinds.None,
    };

    /// <summary>The kinds a variable may be to take a value of this kind without a cast.</summary>
    private static Kinds Holding(Kinds value) => value switch
    {
        Kinds.Z => Kinds.Z,
        Kinds.C => Kinds.C | Kinds.I,
        Kinds.B => Kinds.B | Kinds.S | Kinds.I,
        Kinds.S => Kinds.S | Kinds.I,
        Kinds.I => Kinds.I,
        _ => Kinds.All,
    };

    /// <summary>The kinds a variable may be to be read where this type is expected without a cast.</summary>
    private static Kinds ReadableAs(string? type) => type switch
    {
        "Z" => Kinds.Z,
        "C" => Kinds.C,
        "B" => Kinds.B,
        "S" => Kinds.B | Kinds.S,
        "I" or "J" or "F" or "D" => Kinds.C | Kinds.B | Kinds.S | Kinds.I,
        _ => Kinds.All,
    };

    private static void SmallInts(LiftedMethod lifted, string? returnType)
    {
        var statements = lifted.Function.Blocks.SelectMany(b => b.Statements).ToList();
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var local in statements.SelectMany(Locals).Where(l => l.Type == "I" && IsNarrowable(lifted, l)))
        {
            nodes.TryAdd(local.Name, new Node { Local = local });
        }

        if (nodes.Count == 0)
        {
            return;
        }

        Node? NodeOf(IrExpr e) => e is JLocal l && nodes.TryGetValue(l.Name, out var n) ? n : null;

        void Expect(IrExpr e, string? type)
        {
            if (NodeOf(e) is { } node)
            {
                node.Used = true;
                node.Possible &= ReadableAs(type);
                if (type is "Z" or "C" or "B" or "S")
                {
                    node.Evidence |= Of(type);
                }
                else if (type is "I" or "J" or "F" or "D")
                {
                    node.OnlyFlags = false;
                }
            }
        }

        void NotBoolean(IrExpr e)
        {
            if (NodeOf(e) is { } node)
            {
                node.Used = true;
                node.Possible &= ~Kinds.Z;
                node.OnlyFlags = false;
            }
        }

        void Visit(IrExpr expression)
        {
            foreach (var e in JavaRewrite.PostOrder(expression))
            {
                switch (e)
                {
                    case JCall call:
                        Arguments(call.Args, call.Descriptor);
                        break;
                    case JNew created:
                        Arguments(created.Args, created.Descriptor);
                        break;
                    case JDynamic dynamic:
                        Arguments(dynamic.Args, dynamic.Descriptor);
                        break;
                    case JBinary { Op: "&" or "|" or "^" } bitwise:
                        Same(bitwise.Left, bitwise.Right);
                        break;
                    case JBinary arithmetic:
                        NotBoolean(arithmetic.Left);
                        NotBoolean(arithmetic.Right);
                        break;
                    case JArrayElement element:
                        NotBoolean(element.Index);
                        break;
                    case JNewArray array:
                        foreach (var dimension in array.Dimensions)
                        {
                            NotBoolean(dimension);
                        }

                        break;
                    case JCast cast:
                        NotBoolean(cast.Operand);
                        break;
                    case JNegate negate:
                        NotBoolean(negate.Operand);
                        break;
                    case JCompare compare:
                        NotBoolean(compare.Left);
                        NotBoolean(compare.Right);
                        break;
                    case IrCondition condition:
                        Compared(condition, condition.Left, condition.Right);
                        Compared(condition, condition.Right, condition.Left);
                        break;
                    case IrUnary { Op: IrUnaryOp.LogicalNot } not:
                        Expect(not.Operand, "Z");
                        break;
                    case JLogical logical:
                        Expect(logical.Left, "Z");
                        Expect(logical.Right, "Z");
                        break;
                }
            }
        }

        void Arguments(IReadOnlyList<JExpr> args, string descriptor)
        {
            var parameters = Descriptors.ParameterDescriptors(descriptor);
            for (int i = 0; i < args.Count && i < parameters.Count; i++)
            {
                Expect(args[i], parameters[i]);
            }
        }

        void Same(IrExpr a, IrExpr b)
        {
            if (NodeOf(a) is { } x && NodeOf(b) is { } y)
            {
                x.CopiedFrom.Add(y.Local.Name);
                y.CopiedFrom.Add(x.Local.Name);
                x.CopiedTo.Add(y.Local.Name);
                y.CopiedTo.Add(x.Local.Name);
            }
            else if (NodeOf(a) is { } only && b is JExpr { Type: var type } && b is not JConst)
            {
                only.Possible &= Holding(Of(type)) & ReadableAs(type);
                only.Evidence |= Of(type) & (Kinds.Z | Kinds.C | Kinds.B | Kinds.S);
            }
            else if (NodeOf(b) is { } other && a is JExpr { Type: var aType } && a is not JConst)
            {
                other.Possible &= Holding(Of(aType)) & ReadableAs(aType);
                other.Evidence |= Of(aType) & (Kinds.Z | Kinds.C | Kinds.B | Kinds.S);
            }
        }

        void Compared(IrCondition condition, IrExpr side, IrExpr other)
        {
            if (NodeOf(side) is not { } node)
            {
                return;
            }

            node.Used = true;

            bool equality = condition.Cc is IrCondCode.Equal or IrCondCode.NotEqual;
            switch (other)
            {
                case JConst { Value: int k }:
                    if (!(equality && k is 0 or 1))
                    {
                        node.Possible &= ~Kinds.Z;
                        node.OnlyFlags = false;
                    }

                    if (k is < char.MinValue or > char.MaxValue)
                    {
                        node.Possible &= ~Kinds.C;
                    }

                    break;
                case JLocal when NodeOf(other) is not null:
                    Same(side, other);
                    if (!equality)
                    {
                        NotBoolean(side);
                    }

                    break;
                case JExpr { Type: var type }:
                    if (!equality || type != "Z")
                    {
                        node.Possible &= ~Kinds.Z;
                        node.OnlyFlags = false;
                    }
                    else
                    {
                        node.Possible &= Kinds.Z;
                        node.Evidence |= Kinds.Z;
                    }

                    if (type == "C")
                    {
                        node.Evidence |= Kinds.C;
                    }

                    break;
            }
        }

        foreach (var statement in statements)
        {
            switch (statement)
            {
                case IrAssign { Dst: var target, Src: var value }:
                    Visit(value);
                    if (target is not JLocal)
                    {
                        Visit(target);
                    }

                    if (NodeOf(target) is { } stored)
                    {
                        switch (value)
                        {
                            case JConst { Value: int k }:
                                if (k is not (0 or 1))
                                {
                                    stored.Possible &= ~Kinds.Z;
                                    stored.OnlyFlags = false;
                                }

                                if (k is < char.MinValue or > char.MaxValue)
                                {
                                    stored.Possible &= ~Kinds.C;
                                }

                                if (k is < sbyte.MinValue or > sbyte.MaxValue)
                                {
                                    stored.Possible &= ~Kinds.B;
                                }

                                if (k is < short.MinValue or > short.MaxValue)
                                {
                                    stored.Possible &= ~Kinds.S;
                                }

                                break;
                            case var copied when NodeOf(copied) is { } source:
                                source.Used = true;
                                stored.CopiedFrom.Add(source.Local.Name);
                                source.CopiedTo.Add(stored.Local.Name);
                                break;

                            // x = x | flag: a bitwise operator is as boolean as its operands, which the lifter could not yet tell.
                            case JBinary { Op: "&" or "|" or "^" } bitwise when NodeOf(bitwise.Left) is not null || NodeOf(bitwise.Right) is not null:
                                foreach (var operand in new[] { bitwise.Left, bitwise.Right })
                                {
                                    if (NodeOf(operand) is { } source)
                                    {
                                        stored.CopiedFrom.Add(source.Local.Name);
                                        source.CopiedTo.Add(stored.Local.Name);
                                    }
                                    else if (operand is JExpr { Type: var operandType } && operand is not JConst)
                                    {
                                        stored.Possible &= Holding(Of(operandType));
                                        stored.Evidence |= Of(operandType) & Kinds.Z;
                                    }
                                }

                                break;
                            case JExpr { Type: var type }:
                                stored.Possible &= Holding(Of(type));
                                stored.Evidence |= Of(type) & (Kinds.Z | Kinds.C | Kinds.B | Kinds.S);
                                if (Of(type) != Kinds.Z)
                                {
                                    stored.OnlyFlags = false;
                                }

                                break;
                            default:
                                stored.OnlyFlags = false;
                                break;
                        }
                    }
                    else if (target is JExpr { Type: var targetType } && target is not JLocal)
                    {
                        Expect(value, targetType);
                    }
                    else if (target is JLocal { Type: var localType } && NodeOf(value) is not null)
                    {
                        Expect(value, localType);
                    }

                    break;
                case IrReturn { Value: { } value }:
                    Visit(value);
                    Expect(value, returnType);
                    break;
                case IrSwitch dispatch:
                    Visit(dispatch.Value);
                    NotBoolean(dispatch.Value);
                    break;
                case IrBranch branch:
                    Visit(branch.Condition);
                    if (NodeOf(branch.Condition) is { } tested)
                    {
                        tested.Used = true;
                        tested.Possible &= Kinds.Z;
                        tested.Evidence |= Kinds.Z;
                    }

                    break;
                default:
                    foreach (var root in JavaRewrite.Evaluated(statement))
                    {
                        Visit(root);
                    }

                    break;
            }
        }

        // Copies narrow each other until nothing changes: a store takes only what its source can hand it, and a
        // source only what every place it is stored can hold.
        for (int round = 0; round < MaxRounds; round++)
        {
            bool changed = false;
            foreach (var node in nodes.Values)
            {
                var before = (node.Possible, node.Evidence, node.OnlyFlags);
                foreach (string from in node.CopiedFrom)
                {
                    var source = nodes[from];
                    var holdable = Kinds.None;
                    foreach (var kind in Enum.GetValues<Kinds>().Where(k => k is Kinds.Z or Kinds.C or Kinds.B or Kinds.S or Kinds.I && (source.Possible & k) != 0))
                    {
                        holdable |= Holding(kind);
                    }

                    node.Possible &= holdable;
                    node.Evidence |= source.Evidence;
                    node.OnlyFlags &= source.OnlyFlags;
                }

                foreach (string to in node.CopiedTo)
                {
                    var target = nodes[to];
                    var fits = Kinds.None;
                    foreach (var kind in Enum.GetValues<Kinds>().Where(k => k is Kinds.Z or Kinds.C or Kinds.B or Kinds.S or Kinds.I))
                    {
                        if ((Holding(kind) & target.Possible) != 0)
                        {
                            fits |= kind;
                        }
                    }

                    node.Possible &= fits;
                    node.Evidence |= target.Evidence;
                }

                changed |= before != (node.Possible, node.Evidence, node.OnlyFlags);
            }

            if (!changed)
            {
                break;
            }
        }

        var retyped = new Dictionary<string, JLocal>(StringComparer.Ordinal);
        foreach (var node in nodes.Values)
        {
            string? type = Choose(node);
            if (type is not null)
            {
                retyped[node.Local.Name] = lifted.Locals.Retype(node.Local, type);
            }
        }

        if (retyped.Count == 0)
        {
            return;
        }

        foreach (var block in lifted.Function.Blocks)
        {
            for (int s = 0; s < block.Statements.Count; s++)
            {
                var statement = JavaRewrite.Replace(block.Statements[s], l => retyped.GetValueOrDefault(l.Name));
                if (statement is IrAssign { Dst: JLocal target } assign && retyped.TryGetValue(target.Name, out var local))
                {
                    statement = assign with { Dst = local };
                }

                block.Statements[s] = statement;
            }
        }
    }

    /// <summary>The narrower type a node settles on, or null to leave it an int.</summary>
    private static string? Choose(Node node)
    {
        if (node.Possible == Kinds.Z || (node.Possible.HasFlag(Kinds.Z) && (node.Evidence.HasFlag(Kinds.Z) || (node.OnlyFlags && node.Used))))
        {
            return "Z";
        }

        foreach (var (kind, type) in new[] { (Kinds.C, "C"), (Kinds.B, "B"), (Kinds.S, "S") })
        {
            if (node.Possible.HasFlag(kind) && (node.Evidence.HasFlag(kind) || node.Possible == kind))
            {
                return type;
            }
        }

        return null;
    }

    /// <summary>Locals whose type is the lifter's guess: untabled slots, temporaries and stack values — never a parameter or a tabled variable.</summary>
    private static bool IsNarrowable(LiftedMethod lifted, JLocal local) => local.Kind switch
    {
        JLocalKind.Temp or JLocalKind.Stack => true,
        JLocalKind.Local => lifted.Locals.IsUntabled(local.Name),
        _ => false,
    };

    private static IEnumerable<JLocal> Locals(IrStmt statement)
    {
        var roots = JavaRewrite.Evaluated(statement).ToList();
        if (statement is IrAssign { Dst: var target })
        {
            roots.Add(target);
        }

        return roots.SelectMany(JavaRewrite.PostOrder).OfType<JLocal>();
    }
}
