using Spydate.Core.Jvm;
using Spydate.Decompiler.Native.IR;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// What generic signatures say about the values a method handles, as far as the decompiler can know it: the type
/// arguments of a local's, parameter's or field's declared type, and which methods return one of their class's
/// type variables. javac erases generics and casts the result of such a call (<c>(String) it.next()</c>); when the
/// receiver's declared type fixes the variable to exactly the cast's type, the source had no cast.
///
/// The library's own classes are not in the JAR, so their generic methods come from a table of the common
/// collection and wrapper methods; a class in the JAR answers from its own signatures. Anything not known keeps
/// its cast, which is always correct Java.
/// </summary>
internal static class JavaGenerics
{
    /// <summary>JDK methods that return their class's type variable, by owner and name: the variable's position.</summary>
    private static readonly Dictionary<(string Owner, string Name), int> Returns = new()
    {
        [("java/util/Iterator", "next")] = 0,
        [("java/util/ListIterator", "next")] = 0,
        [("java/util/ListIterator", "previous")] = 0,
        [("java/util/Enumeration", "nextElement")] = 0,
        [("java/util/List", "get")] = 0,
        [("java/util/List", "remove")] = 0,
        [("java/util/List", "set")] = 0,
        [("java/util/List", "getFirst")] = 0,
        [("java/util/List", "getLast")] = 0,
        [("java/util/ArrayList", "get")] = 0,
        [("java/util/ArrayList", "remove")] = 0,
        [("java/util/ArrayList", "set")] = 0,
        [("java/util/LinkedList", "get")] = 0,
        [("java/util/LinkedList", "getFirst")] = 0,
        [("java/util/LinkedList", "getLast")] = 0,
        [("java/util/LinkedList", "poll")] = 0,
        [("java/util/LinkedList", "peek")] = 0,
        [("java/util/LinkedList", "pop")] = 0,
        [("java/util/LinkedList", "removeFirst")] = 0,
        [("java/util/LinkedList", "removeLast")] = 0,
        [("java/util/Queue", "poll")] = 0,
        [("java/util/Queue", "peek")] = 0,
        [("java/util/Queue", "remove")] = 0,
        [("java/util/Queue", "element")] = 0,
        [("java/util/Deque", "poll")] = 0,
        [("java/util/Deque", "peek")] = 0,
        [("java/util/Deque", "pop")] = 0,
        [("java/util/Deque", "pollFirst")] = 0,
        [("java/util/Deque", "pollLast")] = 0,
        [("java/util/Deque", "peekFirst")] = 0,
        [("java/util/Deque", "peekLast")] = 0,
        [("java/util/Deque", "removeFirst")] = 0,
        [("java/util/Deque", "removeLast")] = 0,
        [("java/util/Deque", "getFirst")] = 0,
        [("java/util/Deque", "getLast")] = 0,
        [("java/util/ArrayDeque", "poll")] = 0,
        [("java/util/ArrayDeque", "peek")] = 0,
        [("java/util/ArrayDeque", "pop")] = 0,
        [("java/util/Stack", "pop")] = 0,
        [("java/util/Stack", "peek")] = 0,
        [("java/util/Stack", "push")] = 0,
        [("java/util/Vector", "get")] = 0,
        [("java/util/Vector", "elementAt")] = 0,
        [("java/util/Map", "get")] = 1,
        [("java/util/Map", "put")] = 1,
        [("java/util/Map", "remove")] = 1,
        [("java/util/Map", "getOrDefault")] = 1,
        [("java/util/Map", "putIfAbsent")] = 1,
        [("java/util/Map", "computeIfAbsent")] = 1,
        [("java/util/Map", "merge")] = 1,
        [("java/util/HashMap", "get")] = 1,
        [("java/util/HashMap", "put")] = 1,
        [("java/util/HashMap", "remove")] = 1,
        [("java/util/TreeMap", "get")] = 1,
        [("java/util/LinkedHashMap", "get")] = 1,
        [("java/util/concurrent/ConcurrentHashMap", "get")] = 1,
        [("java/util/concurrent/ConcurrentMap", "get")] = 1,
        [("java/util/Hashtable", "get")] = 1,
        [("java/util/Map$Entry", "getKey")] = 0,
        [("java/util/Map$Entry", "getValue")] = 1,
        [("java/util/Optional", "get")] = 0,
        [("java/util/Optional", "orElse")] = 0,
        [("java/util/Optional", "orElseThrow")] = 0,
        [("java/util/function/Supplier", "get")] = 0,
        [("java/util/function/Function", "apply")] = 1,
        [("java/util/concurrent/Future", "get")] = 0,
        [("java/util/concurrent/Callable", "call")] = 0,
        [("java/lang/ThreadLocal", "get")] = 0,
        [("java/lang/ref/Reference", "get")] = 0,
        [("java/lang/ref/WeakReference", "get")] = 0,
        [("java/lang/ref/SoftReference", "get")] = 0,
        [("java/util/concurrent/atomic/AtomicReference", "get")] = 0,
        [("java/lang/Class", "cast")] = 0,
        [("java/lang/Class", "newInstance")] = 0,
    };

    /// <summary>JDK methods that return an array of their class's type variable: <c>Class&lt;T&gt;.getEnumConstants()</c> is a <c>T[]</c>.</summary>
    private static readonly Dictionary<(string Owner, string Name), int> ArrayReturns = new()
    {
        [("java/lang/Class", "getEnumConstants")] = 0,
    };

    /// <summary>JDK methods returning a type built from their class's type arguments: <c>{0}</c> is the first.</summary>
    private static readonly Dictionary<(string Owner, string Name), string> ParameterizedReturns = new()
    {
        [("java/lang/Iterable", "iterator")] = "Ljava/util/Iterator<{0}>;",
        [("java/util/Collection", "iterator")] = "Ljava/util/Iterator<{0}>;",
        [("java/util/List", "iterator")] = "Ljava/util/Iterator<{0}>;",
        [("java/util/Set", "iterator")] = "Ljava/util/Iterator<{0}>;",
        [("java/util/Queue", "iterator")] = "Ljava/util/Iterator<{0}>;",
        [("java/util/Deque", "iterator")] = "Ljava/util/Iterator<{0}>;",
        [("java/util/ArrayList", "iterator")] = "Ljava/util/Iterator<{0}>;",
        [("java/util/List", "listIterator")] = "Ljava/util/ListIterator<{0}>;",
        [("java/util/Collection", "stream")] = "Ljava/util/stream/Stream<{0}>;",
        [("java/util/List", "stream")] = "Ljava/util/stream/Stream<{0}>;",
        [("java/util/Set", "stream")] = "Ljava/util/stream/Stream<{0}>;",
        [("java/util/List", "subList")] = "Ljava/util/List<{0}>;",
    };

    /// <summary>JDK methods whose result has a wildcard for its type argument, which only a cast makes more exact.</summary>
    private static readonly Dictionary<(string Owner, string Name), string> WildcardReturns = new()
    {
        [("java/lang/Object", "getClass")] = "Ljava/lang/Class<*>;",
        [("java/lang/Class", "getComponentType")] = "Ljava/lang/Class<*>;",
        [("java/lang/Class", "getSuperclass")] = "Ljava/lang/Class<*>;",
        [("java/lang/Class", "forName")] = "Ljava/lang/Class<*>;",
        [("java/lang/Class", "getDeclaringClass")] = "Ljava/lang/Class<*>;",
        [("java/lang/Class", "getEnclosingClass")] = "Ljava/lang/Class<*>;",
    };

    /// <summary>
    /// A JDK call's generic result as far as the tables say: its class's type variable as the receiver's generic type
    /// fixes it, or a wildcard type. Null when unknown.
    /// </summary>
    public static string? JdkResult(JCall call, Func<JExpr, string?> genericOf, Func<string, ClassFile?> findClass)
    {
        if (WildcardReturns.TryGetValue((call.Owner, call.Name), out var wildcard))
        {
            return wildcard;
        }

        if (call.Receiver is not { } receiver || genericOf(receiver) is not { } receiverType || ClassOf(receiverType) is not { } receiverClass
            || (receiverClass != call.Owner && !(Supertypes.TryGetValue(receiverClass, out var supers) && supers.Contains(call.Owner))
                && !(call.Owner is "java/util/Collection" or "java/lang/Iterable" && IsCollection(receiverClass))))
        {
            return null;
        }

        var arguments = TypeArguments(receiverType);
        if (ParameterizedReturns.TryGetValue((call.Owner, call.Name), out var template))
        {
            return arguments.Count == 1 && !arguments[0].StartsWith('-') && arguments[0] != "*" ? template.Replace("{0}", arguments[0], StringComparison.Ordinal) : null;
        }

        if (ReturnedVariable(call, findClass) is not { } returned)
        {
            return null;
        }

        if (returned.Index >= arguments.Count)
        {
            return null;
        }

        string argument = arguments[returned.Index];
        string? element = argument.StartsWith('+') ? argument[1..] : argument.StartsWith('-') || argument == "*" ? null : argument;
        return element is null ? null : returned.Array ? "[" + element : element;
    }

    private static bool IsCollection(string internalName)
        => internalName is "java/util/List" or "java/util/Set" or "java/util/Collection" or "java/util/Queue" or "java/util/Deque"
           || (Supertypes.TryGetValue(internalName, out var supers) && supers.Any(s => s is "java/util/List" or "java/util/Queue" or "java/util/Deque"))
           || internalName is "java/util/HashSet" or "java/util/LinkedHashSet" or "java/util/TreeSet" or "java/util/SortedSet" or "java/util/NavigableSet";

    /// <summary>JDK classes known to be generic, for which <c>new T&lt;&gt;()</c> is right.</summary>
    private static readonly HashSet<string> GenericClasses = new(StringComparer.Ordinal)
    {
        "java/util/ArrayList", "java/util/LinkedList", "java/util/HashMap", "java/util/LinkedHashMap", "java/util/TreeMap",
        "java/util/HashSet", "java/util/LinkedHashSet", "java/util/TreeSet", "java/util/ArrayDeque", "java/util/PriorityQueue",
        "java/util/Vector", "java/util/Stack", "java/util/Hashtable", "java/util/IdentityHashMap", "java/util/WeakHashMap",
        "java/util/EnumMap", "java/util/concurrent/ConcurrentHashMap", "java/util/concurrent/CopyOnWriteArrayList",
        "java/util/concurrent/CopyOnWriteArraySet", "java/util/concurrent/ConcurrentLinkedQueue", "java/util/concurrent/ConcurrentLinkedDeque",
        "java/util/concurrent/LinkedBlockingQueue", "java/util/concurrent/ArrayBlockingQueue", "java/util/concurrent/LinkedBlockingDeque",
        "java/util/concurrent/ConcurrentSkipListMap", "java/util/concurrent/ConcurrentSkipListSet", "java/util/concurrent/atomic/AtomicReference",
        "java/lang/ThreadLocal", "java/lang/ref/WeakReference", "java/lang/ref/SoftReference", "java/util/concurrent/FutureTask",
        "java/util/concurrent/CompletableFuture",
    };

    /// <summary>A concrete JDK collection read as the interface its methods are listed under, with the same type arguments.</summary>
    private static readonly Dictionary<string, string[]> Supertypes = new(StringComparer.Ordinal)
    {
        ["java/util/ArrayList"] = ["java/util/List"],
        ["java/util/LinkedList"] = ["java/util/List", "java/util/Deque", "java/util/Queue"],
        ["java/util/Vector"] = ["java/util/List"],
        ["java/util/ListIterator"] = ["java/util/Iterator"],
        ["java/util/HashMap"] = ["java/util/Map"],
        ["java/util/LinkedHashMap"] = ["java/util/Map"],
        ["java/util/TreeMap"] = ["java/util/Map"],
        ["java/util/Hashtable"] = ["java/util/Map"],
        ["java/util/concurrent/ConcurrentHashMap"] = ["java/util/Map", "java/util/concurrent/ConcurrentMap"],
        ["java/util/concurrent/ConcurrentMap"] = ["java/util/Map"],
        ["java/util/SortedMap"] = ["java/util/Map"],
        ["java/util/NavigableMap"] = ["java/util/Map"],
        ["java/util/ArrayDeque"] = ["java/util/Deque", "java/util/Queue"],
        ["java/util/Deque"] = ["java/util/Queue"],
        ["java/util/concurrent/BlockingQueue"] = ["java/util/Queue"],
        ["java/util/concurrent/LinkedBlockingQueue"] = ["java/util/Queue"],
        ["java/util/concurrent/ConcurrentLinkedQueue"] = ["java/util/Queue"],
        ["java/util/PriorityQueue"] = ["java/util/Queue"],
        ["java/util/Stack"] = ["java/util/List"],
        ["java/lang/ref/WeakReference"] = ["java/lang/ref/Reference"],
        ["java/lang/ref/SoftReference"] = ["java/lang/ref/Reference"],
    };

    /// <summary>
    /// The generic type of what an expression reads, as far as the class files say: a local's or parameter's, a field
    /// of this JAR's, a method of this JAR's that returns a concrete one, and a map's entry, key and value views.
    /// </summary>
    public static string? GenericOf(JExpr expression, LiftedMethod lifted, Func<string, ClassFile?> findClass)
    {
        switch (expression)
        {
            case JLocal local:
                return lifted.Locals.Signatures.GetValueOrDefault(local.Name);
            case JField field:
                return findClass(field.Owner)?.Fields.FirstOrDefault(f => f.Name == field.Name && f.Descriptor == field.FieldType)?.Signature is { } s
                       && IsParameterized(s) ? s : null;
            case JCall { Receiver: { } receiver, Args.Count: 0, Name: "entrySet" or "keySet" or "values" } call
                when GenericOf(receiver, lifted, findClass) is { } map && TypeArguments(map) is [var key, var value]
                     && ClassOf(map) is { } mapClass && (mapClass == "java/util/Map" || Supertypes.GetValueOrDefault(mapClass)?.Contains("java/util/Map") == true):
                return call.Name switch
                {
                    "entrySet" => $"Ljava/util/Set<Ljava/util/Map$Entry<{key}{value}>;>;",
                    "keySet" => $"Ljava/util/Set<{key}>;",
                    _ => $"Ljava/util/Collection<{value}>;",
                };
            case JCall call when findClass(call.Owner)?.Methods.FirstOrDefault(m => m.Name == call.Name && m.Descriptor == call.Descriptor)?.Signature is { } signature
                                 && ReturnSignature(signature) is { } returned && IsParameterized(returned) && !returned.Contains(";T", StringComparison.Ordinal) && !returned.Contains("<T", StringComparison.Ordinal):
                return returned;
            default:
                return null;
        }
    }

    /// <summary>
    /// Generic types for the locals no table described, from what is stored in them: <c>T current = array[i]</c>,
    /// <c>T result = this.reference.get()</c> with an <c>AtomicReference&lt;T&gt;</c> field. Every store must agree,
    /// and every source must be one whose type variables are in scope here — the method's own, and this class's
    /// through <c>this</c> — so the declaration compiles where it is written.
    /// </summary>
    public static void InferLocals(LiftedMethod lifted, JvmMethod method, ClassFile file, Func<string, ClassFile?> findClass)
    {
        bool instance = (method.Access & JvmAccess.Static) == 0;
        var scope = new Scope(file, method, instance, findClass);
        var stores = new Dictionary<string, (JLocal Local, List<JExpr> Values)>(StringComparer.Ordinal);
        foreach (var statement in lifted.Function.AllStatements)
        {
            if (statement is IrAssign { Dst: JLocal { Kind: JLocalKind.Local or JLocalKind.Temp or JLocalKind.Stack, Type: ['L' or '[', ..] } local, Src: JExpr value }
                && !lifted.Locals.Signatures.ContainsKey(local.Name))
            {
                if (!stores.TryGetValue(local.Name, out var entry))
                {
                    entry = (local, []);
                    stores[local.Name] = entry;
                }

                entry.Values.Add(value);
            }
        }

        // Rounds, so a local copied from one found in the round before is found too.
        for (int round = 0; round < 4 && stores.Count > 0; round++)
        {
            bool found = false;
            foreach (var (name, (local, values)) in stores.ToList())
            {
                var types = values.Where(v => v is not JConst { Value: null }).Select(v => InScope(v, lifted, scope)).ToList();
                if (types.Count > 0 && types[0] is { } first && types.All(t => t == first) && first != local.Type && Fits(first, local.Type))
                {
                    lifted.Locals.Infer(name, first);
                    stores.Remove(name);
                    found = true;
                }
            }

            if (!found)
            {
                break;
            }
        }

        Backwards(lifted, method, file, stores);
    }

    /// <summary>
    /// A local that only ever holds a <c>new</c> of a generic class, typed from where its value goes: returned from
    /// a method returning <c>List&lt;T&gt;</c>, or stored to a field or local of that type, <c>ArrayList o = new
    /// ArrayList()</c> is <c>ArrayList&lt;T&gt; o = new ArrayList&lt;&gt;()</c>. Every such use must agree.
    /// </summary>
    private static void Backwards(LiftedMethod lifted, JvmMethod method, ClassFile file, Dictionary<string, (JLocal Local, List<JExpr> Values)> stores)
    {
        var created = stores.Where(s => s.Value.Values.All(v => v is JNew or JConst { Value: null }) && s.Value.Values.OfType<JNew>().Select(n => n.Owner).Distinct().Count() == 1)
            .ToDictionary(s => s.Key, s => s.Value.Values.OfType<JNew>().First().Owner, StringComparer.Ordinal);
        if (created.Count == 0)
        {
            return;
        }

        string? returned = method.Signature is { } signature ? ReturnSignature(signature) : null;
        var targets = new Dictionary<string, HashSet<string?>>(StringComparer.Ordinal);
        foreach (var statement in lifted.Function.AllStatements)
        {
            var (name, target) = statement switch
            {
                IrReturn { Value: JLocal local } => (local.Name, returned),
                IrAssign { Dst: JLocal to, Src: JLocal from } => (from.Name, lifted.Locals.Signatures.GetValueOrDefault(to.Name)),
                IrAssign { Dst: JField { Instance: null or JLocal { Kind: JLocalKind.This } } field, Src: JLocal from } when field.Owner == file.Name
                    => (from.Name, file.Fields.FirstOrDefault(f => f.Name == field.Name && f.Descriptor == field.FieldType)?.Signature),
                _ => ((string?)null, (string?)null),
            };

            if (name is not null && created.ContainsKey(name))
            {
                (targets.TryGetValue(name, out var set) ? set : targets[name] = []).Add(target);
            }
        }

        foreach (var (name, set) in targets)
        {
            if (set.Count == 1 && set.First() is { } expected && IsParameterized(expected) && Specialize(created[name], expected) is { } generic)
            {
                lifted.Locals.Infer(name, generic);
            }
        }
    }

    /// <summary>
    /// <paramref name="className"/> with the type arguments that make it <paramref name="expected"/> or a subtype of
    /// it: the same class, or a JDK collection its interface lists with the same arguments.
    /// </summary>
    private static string? Specialize(string className, string expected)
    {
        if (ClassOf(expected) is not { } target || TypeArguments(expected) is not { Count: > 0 } arguments || arguments.Any(a => a.StartsWith('*') || a.StartsWith('+') || a.StartsWith('-')))
        {
            return null;
        }

        bool sub = className == target || (Supertypes.TryGetValue(className, out var supers) && supers.Contains(target))
                   || (target is "java/util/Collection" or "java/lang/Iterable" && IsCollection(className) && arguments.Count == 1);
        return sub && GenericClasses.Contains(className) || className == target ? $"L{className}<{string.Concat(arguments)}>;" : null;
    }

    /// <summary>Whether a value of generic type <paramref name="known"/> goes where <paramref name="expected"/> is wanted with no cast.</summary>
    public static bool Assignable(string known, string expected)
        => known == expected || (ClassOf(known) is { } from && Specialize(from, expected) == known);

    /// <summary>Whether a generic type can declare a local the bytecode typed as <paramref name="descriptor"/>.</summary>
    private static bool Fits(string signature, string? descriptor)
        => signature.StartsWith('T') || signature.StartsWith("[T", StringComparison.Ordinal)
           || (ClassOf(signature.TrimStart('[')) is { } name && descriptor is not null && descriptor.TrimStart('[') == $"L{name};"
               && signature.Length - signature.TrimStart('[').Length == descriptor.Length - descriptor.TrimStart('[').Length);

    private sealed record Scope(ClassFile File, JvmMethod Method, bool Instance, Func<string, ClassFile?> FindClass);

    /// <summary>
    /// An expression's generic type in <paramref name="method"/> of <paramref name="file"/>, when every type variable
    /// in it is one the method can name; null when unknown.
    /// </summary>
    public static string? InScopeOf(JExpr expression, LiftedMethod lifted, JvmMethod method, ClassFile file, Func<string, ClassFile?> findClass)
        => InScope(expression, lifted, new Scope(file, method, (method.Access & JvmAccess.Static) == 0, findClass));

    /// <summary>A method as the class a call names it on declares or inherits it, with the class that declares it.</summary>
    internal static (ClassFile Declaring, JvmMethod Method)? FindMethod(string owner, string name, string descriptor, Func<string, ClassFile?> findClass)
    {
        var current = findClass(owner);
        for (int depth = 0; current is not null && depth < 16; depth++)
        {
            if (current.Methods.FirstOrDefault(m => m.Name == name && m.Descriptor == descriptor) is { } found)
            {
                return (current, found);
            }

            current = current.SuperName is { } super ? findClass(super) : null;
        }

        return null;
    }

    /// <summary>An expression's generic type when its type variables are ones this method can name, or null.</summary>
    private static string? InScope(JExpr expression, LiftedMethod lifted, Scope scope)
    {
        var (file, method, instance, findClass) = scope;
        switch (expression)
        {
            case JLocal local:
                return lifted.Locals.Signatures.GetValueOrDefault(local.Name);
            case JArrayElement element:
                return InScope(element.Array, lifted, scope) is ['[', .. var inner] ? inner : null;
            case JConditional conditional:
                return InScope(conditional.Then, lifted, scope) is { } then && (conditional.Else is JConst { Value: null } || InScope(conditional.Else, lifted, scope) == then) ? then
                    : conditional.Then is JConst { Value: null } ? InScope(conditional.Else, lifted, scope) : null;
            case JCast cast:
                return InScope(cast.Operand, lifted, scope) is { } operand && ErasureIn(operand, method, file) == cast.CastType ? operand : null;
            case JField field:
            {
                if (findClass(field.Owner)?.Fields.FirstOrDefault(f => f.Name == field.Name && f.Descriptor == field.FieldType) is not { Signature: { } signature } declared)
                {
                    return null;
                }

                bool own = field.Owner == file.Name && ((declared.Access & JvmAccess.Static) != 0 || (instance && field.Instance is JLocal { Kind: JLocalKind.This }));
                if (own || !HasTypeVariable(signature))
                {
                    return signature;
                }

                // Through an instance whose generic type is known, the field's variables are that type's arguments.
                return field.Instance is { } holder && InScope(holder, lifted, scope) is { } holderType && ClassOf(holderType) == field.Owner
                       && findClass(field.Owner)?.Signature is { } ownerSignature && TypeVariables(ownerSignature) is var variables
                       && TypeArguments(holderType) is var arguments && variables.Count == arguments.Count && arguments.All(a => !a.StartsWith('*') && !a.StartsWith('-') && !a.StartsWith('+'))
                    ? Substitute(signature, variables.Zip(arguments).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal))
                    : null;
            }

            case JCall call:
            {
                if (FindMethod(call.Owner, call.Name, call.Descriptor, findClass) is { Method.Signature: { } signature } found)
                {
                    // A method with type parameters of its own infers them at the call; only a result without them is known.
                    if (ReturnSignature(signature) is not { } returned || (signature.StartsWith('<') && HasTypeVariable(returned)))
                    {
                        return null;
                    }

                    if (!HasTypeVariable(returned))
                    {
                        return returned;
                    }

                    // This class's method, or an inherited one whose variables the extends clauses map to this class's;
                    // an enclosing instance's (Outer.this.m()), whose variables an inner class can name too.
                    if (instance && call.Receiver is JLocal { Kind: JLocalKind.This })
                    {
                        return AsOwn(returned, found.Declaring.Name, file, findClass);
                    }

                    if (instance && call.Receiver is JField { Name: ['t', 'h', 'i', 's', '$', ..], Instance: JLocal { Kind: JLocalKind.This }, FieldType: ['L', .. var outer, ';'] }
                        && findClass(outer) is { } outerFile)
                    {
                        return AsOwn(returned, found.Declaring.Name, outerFile, findClass);
                    }

                    // Any other receiver of a known generic type: the method's variables are that type's arguments.
                    return call.Receiver is { } receiver && InScope(receiver, lifted, scope) is { } receiverType
                        ? AsSeenFrom(returned, found.Declaring.Name, receiverType, findClass)
                        : null;
                }

                return JdkResult(call, e => InScope(e, lifted, scope), findClass) is { } result && !result.Contains('*') ? result : null;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// A signature of <paramref name="owner"/>'s, in terms of <paramref name="file"/>'s own type variables: walked up
    /// the extends clauses, <c>class A&lt;T&gt; extends B&lt;T, String&gt;</c> makes B's <c>E</c> A's <c>String</c>.
    /// Null when a raw extends clause or a class outside the JAR loses the mapping.
    /// </summary>
    public static string? AsOwn(string signature, string owner, ClassFile file, Func<string, ClassFile?> findClass)
        => Walk(signature, owner, file, null, findClass);

    /// <summary>
    /// A signature of <paramref name="owner"/>'s as seen through a value of generic type <paramref name="receiverType"/>:
    /// <c>List&lt;String&gt;</c>'s <c>get</c> returns <c>String</c>. Null when a wildcard or a raw type is in the way.
    /// </summary>
    public static string? AsSeenFrom(string signature, string owner, string receiverType, Func<string, ClassFile?> findClass)
    {
        if (ClassOf(receiverType) is not { } receiverClass || findClass(receiverClass) is not { Signature: { } receiverSignature } receiverFile)
        {
            return null;
        }

        var variables = TypeVariables(receiverSignature);
        var arguments = TypeArguments(receiverType);
        if (variables.Count != arguments.Count || arguments.Any(a => a.StartsWith('*') || a.StartsWith('+') || a.StartsWith('-')))
        {
            return null;
        }

        return Walk(signature, owner, receiverFile, variables.Zip(arguments).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal), findClass);
    }

    private static string? Walk(string signature, string owner, ClassFile file, Dictionary<string, string>? mapping, Func<string, ClassFile?> findClass)
    {
        var current = file;
        for (int depth = 0; depth < 16; depth++)
        {
            if (current.Name == owner)
            {
                return mapping is null ? signature : Substitute(signature, mapping);
            }

            if (current.Signature is not { } classSignature || SuperSignature(classSignature) is not { } super
                || ClassOf(super) is not { } superName || findClass(superName) is not { } superFile)
            {
                return null;
            }

            var arguments = TypeArguments(super).Select(a => mapping is null ? a : Substitute(a, mapping)).ToList();
            var variables = superFile.Signature is { } superSignature ? TypeVariables(superSignature) : [];
            if (variables.Count != arguments.Count)
            {
                return null;
            }

            mapping = variables.Zip(arguments).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal);
            current = superFile;
        }

        return null;
    }

    /// <summary>
    /// What a generic type erases to where it is used: a class type to its class, a type variable to its first
    /// bound's erasure — the method's own variables first, then its class's — and an array to an array of that.
    /// </summary>
    public static string? ErasureIn(string signature, JvmMethod method, ClassFile file)
    {
        if (signature.StartsWith('['))
        {
            return ErasureIn(signature[1..], method, file) is { } element ? "[" + element : null;
        }

        if (!signature.StartsWith('T') || !signature.EndsWith(';'))
        {
            return Erasure(signature);
        }

        string name = signature[1..^1];
        foreach (string? declared in new[] { method.Signature, file.Signature })
        {
            if (declared is not null && Bound(declared, name) is { } bound)
            {
                return bound == signature ? "Ljava/lang/Object;" : ErasureIn(bound, method, file);
            }
        }

        return null;
    }

    /// <summary>A type parameter's first bound in a signature's <c>&lt;…&gt;</c> header, or null when it declares none of that name.</summary>
    private static string? Bound(string signature, string name)
    {
        if (!signature.StartsWith('<'))
        {
            return null;
        }

        int at = 1;
        while (at < signature.Length && signature[at] != '>')
        {
            int colon = signature.IndexOf(':', at);
            if (colon < 0)
            {
                return null;
            }

            string parameter = signature[at..colon];
            at = colon + 1;

            // An empty class bound (T::Ljava/lang/Comparable;) means the first interface bound erases it.
            string? first = null;
            while (at < signature.Length && (signature[at] == ':' || first is null))
            {
                if (signature[at] == ':')
                {
                    at++;
                }

                int end = SkipType(signature, at);
                if (end < 0)
                {
                    return null;
                }

                first ??= signature[at..end];
                at = end;
                if (at >= signature.Length || signature[at] != ':')
                {
                    break;
                }
            }

            if (parameter == name)
            {
                return first ?? "Ljava/lang/Object;";
            }
        }

        return null;
    }

    /// <summary>
    /// The type arguments a generic static method with no arguments needs written out when it heads a call chain:
    /// javac infers <c>AppendableJoiner.builder()</c> from nothing there, so the source said
    /// <c>AppendableJoiner.&lt;Type&gt;builder()</c>. Found by following each call's signature down the chain to its
    /// result and matching that against the type the chain's value goes to. Null when any step is unknown.
    /// </summary>
    public static IReadOnlyList<string>? Witness(IReadOnlyList<JCall> chain, string expected, Func<string, ClassFile?> findClass)
    {
        if (chain.Count < 2 || chain[0] is not { Kind: JCallKind.Static, Args.Count: 0 } head
            || FindMethod(head.Owner, head.Name, head.Descriptor, findClass) is not { Method.Signature: ['<', ..] headSignature }
            || ReturnSignature(headSignature) is not { } current)
        {
            return null;
        }

        var names = TypeVariables(headSignature);
        foreach (var call in chain.Skip(1))
        {
            if (FindMethod(call.Owner, call.Name, call.Descriptor, findClass) is not { Method.Signature: { } signature } found || signature.StartsWith('<')
                || ClassOf(current) != found.Declaring.Name || found.Declaring.Signature is not { } classSignature
                || ReturnSignature(signature) is not { } returned)
            {
                return null;
            }

            var variables = TypeVariables(classSignature);
            var arguments = TypeArguments(current);
            if (variables.Count != arguments.Count)
            {
                return null;
            }

            current = Substitute(returned, variables.Zip(arguments).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal));
        }

        if (ClassOf(current) != ClassOf(expected) || TypeArguments(current) is not { Count: > 0 } found2 || TypeArguments(expected) is not { } wanted || found2.Count != wanted.Count)
        {
            return null;
        }

        var bound = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < found2.Count; i++)
        {
            if (found2[i] is ['T', .. var name, ';'] && names.Contains(name) && wanted[i] is not ("*" or ['+' or '-', ..]))
            {
                bound[name] = wanted[i];
            }
        }

        return names.All(bound.ContainsKey) ? names.Select(n => bound[n]).ToList() : null;
    }

    /// <summary>A class signature's superclass: what follows its type parameters.</summary>
    private static string? SuperSignature(string classSignature)
    {
        int at = 0;
        if (classSignature.StartsWith('<'))
        {
            int depth = 0;
            for (; at < classSignature.Length; at++)
            {
                if (classSignature[at] == '<')
                {
                    depth++;
                }
                else if (classSignature[at] == '>' && --depth == 0)
                {
                    at++;
                    break;
                }
            }
        }

        int end = SkipType(classSignature, at);
        return end < 0 ? null : classSignature[at..end];
    }

    /// <summary>A signature with its type variables replaced as <paramref name="mapping"/> says; any other stays.</summary>
    internal static string Substitute(string signature, IReadOnlyDictionary<string, string> mapping)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < signature.Length; i++)
        {
            if (signature[i] == 'T' && (i == 0 || signature[i - 1] is '<' or ';' or '[' or '+' or '-') && signature.IndexOf(';', i) is var semi and > 0
                && mapping.TryGetValue(signature[(i + 1)..semi], out var replacement))
            {
                sb.Append(replacement);
                i = semi;
                continue;
            }

            sb.Append(signature[i]);
        }

        return sb.ToString();
    }

    /// <summary>Whether a signature names a type variable: <c>TT;</c>, <c>[TT;</c>, <c>Ljava/util/List&lt;TT;&gt;;</c>.</summary>
    public static bool HasTypeVariable(string signature)
    {
        for (int i = 0; i < signature.Length; i++)
        {
            if (signature[i] == 'T' && (i == 0 || signature[i - 1] is '<' or ';' or '[' or '+' or '-' or ')' or '('))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsGenericClass(string internalName, Func<string, ClassFile?> findClass)
        => GenericClasses.Contains(internalName)
           || (findClass(internalName)?.Signature is { } signature && signature.StartsWith('<'));

    /// <summary>
    /// Whether <c>(T) call</c> needs its cast: false when the call returns its class's type variable and the
    /// receiver's generic type fixes that variable to <paramref name="castType"/> exactly.
    /// </summary>
    public static bool CastIsRedundant(string castType, JCall call, Func<JExpr, string?> genericOf, Func<string, ClassFile?> findClass)
    {
        if (call.Receiver is not { } receiver || genericOf(receiver) is not { } generic || ClassOf(generic) is not { } receiverClass)
        {
            return false;
        }

        if (ReturnedVariable(call, findClass) is not { } returned)
        {
            return false;
        }

        int index = returned.Index;

        // The receiver's own class, or a JDK interface it is listed under with the same arguments.
        if (receiverClass != call.Owner && !(Supertypes.TryGetValue(receiverClass, out var supers) && supers.Contains(call.Owner)))
        {
            return false;
        }

        var arguments = TypeArguments(generic);
        if (index >= arguments.Count)
        {
            return false;
        }

        // A type variable of the method's or class's: javac cast to its bound's erasure, and the call already has its type.
        string argument = returned.Array ? "[" + arguments[index] : arguments[index];
        if (argument.TrimStart('[').StartsWith('T'))
        {
            return castType is ['L' or '[', ..] && castType.Length - castType.TrimStart('[').Length == argument.Length - argument.TrimStart('[').Length;
        }

        return Erasure(argument) is { } erased && erased == castType;
    }

    /// <summary>Which of its class's type variables a method returns, from the table or the class's own signatures.</summary>
    private static (int Index, bool Array)? ReturnedVariable(JCall call, Func<string, ClassFile?> findClass)
    {
        if (Returns.TryGetValue((call.Owner, call.Name), out int index) && JCall.ReturnType(call.Descriptor) == "Ljava/lang/Object;")
        {
            return (index, false);
        }

        if (ArrayReturns.TryGetValue((call.Owner, call.Name), out int arrayIndex) && JCall.ReturnType(call.Descriptor) == "[Ljava/lang/Object;")
        {
            return (arrayIndex, true);
        }

        if (findClass(call.Owner) is not { Signature: { } classSignature } owner)
        {
            return null;
        }

        var method = owner.Methods.FirstOrDefault(m => m.Name == call.Name && m.Descriptor == call.Descriptor);
        if (method?.Signature is not { } signature)
        {
            return null;
        }

        int close = signature.LastIndexOf(')');
        if (close < 0 || close + 1 >= signature.Length || signature[close + 1] != 'T' || !signature.EndsWith(';'))
        {
            return null;
        }

        string variable = signature[(close + 2)..^1];
        var variables = TypeVariables(classSignature);
        int at = variables.IndexOf(variable);
        return at >= 0 ? (at, false) : null;
    }

    /// <summary>A class signature's type variable names, in order: <c>&lt;K:…;V:…&gt;</c> is K, V.</summary>
    internal static List<string> TypeVariables(string classSignature)
    {
        var names = new List<string>();
        if (!classSignature.StartsWith('<'))
        {
            return names;
        }

        int depth = 0;
        int start = 1;
        for (int i = 1; i < classSignature.Length; i++)
        {
            char c = classSignature[i];
            if (c == '<')
            {
                depth++;
            }
            else if (c == '>')
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
            }
            else if (c == ':' && depth == 0 && start >= 0)
            {
                names.Add(classSignature[start..i]);
                start = -1;
            }
            else if (c == ';' && depth == 0)
            {
                // After a bound: the next name starts here unless another bound (':') follows.
                start = i + 1 < classSignature.Length && classSignature[i + 1] != ':' ? i + 1 : -1;
            }
        }

        return names;
    }

    /// <summary><c>Ljava/util/List&lt;…&gt;;</c> is <c>java/util/List</c>; null for anything that is not a class type.</summary>
    public static string? ClassOf(string signature)
    {
        if (!signature.StartsWith('L'))
        {
            return null;
        }

        int end = signature.IndexOfAny(['<', ';']);
        return end < 0 ? null : signature[1..end];
    }

    /// <summary>The descriptor a type argument erases to, or null for a wildcard, a type variable or an array of one.</summary>
    public static string? Erasure(string argument)
    {
        if (argument.StartsWith('L') && ClassOf(argument) is { } name)
        {
            return $"L{name};";
        }

        return argument.StartsWith('[') && Erasure(argument[1..]) is { } element ? "[" + element : null;
    }

    /// <summary>Whether a signature carries type arguments: <c>List&lt;String&gt;</c> does, <c>List</c> does not.</summary>
    public static bool IsParameterized(string signature) => signature.StartsWith('L') && signature.Contains('<', StringComparison.Ordinal);

    /// <summary>The top-level type arguments of a class type signature, each as a signature of its own.</summary>
    public static List<string> TypeArguments(string signature)
    {
        var arguments = new List<string>();
        int open = signature.IndexOf('<');
        if (open < 0)
        {
            return arguments;
        }

        int at = open + 1;
        while (at < signature.Length && signature[at] != '>')
        {
            int start = at;
            if (signature[at] is '+' or '-')
            {
                at++;
            }

            at = SkipType(signature, at);
            if (at < 0)
            {
                return [];
            }

            arguments.Add(signature[start..at]);
        }

        return arguments;
    }

    /// <summary>Index just past the type signature starting at <paramref name="at"/>, or -1 when it is malformed.</summary>
    private static int SkipType(string s, int at)
    {
        if (at >= s.Length)
        {
            return -1;
        }

        switch (s[at])
        {
            case '*':
            case 'B' or 'C' or 'D' or 'F' or 'I' or 'J' or 'S' or 'Z':
                return at + 1;
            case '[':
                return SkipType(s, at + 1);
            case 'T':
                int semi = s.IndexOf(';', at);
                return semi < 0 ? -1 : semi + 1;
            case 'L':
                int depth = 0;
                for (int i = at; i < s.Length; i++)
                {
                    if (s[i] == '<')
                    {
                        depth++;
                    }
                    else if (s[i] == '>')
                    {
                        depth--;
                    }
                    else if (s[i] == ';' && depth == 0)
                    {
                        return i + 1;
                    }
                }

                return -1;
            default:
                return -1;
        }
    }

    /// <summary>A method signature's parameter types, each as a signature, or null when it is malformed.</summary>
    public static List<string>? ParameterSignatures(string methodSignature)
    {
        int open = methodSignature.IndexOf('(');
        if (open < 0)
        {
            return null;
        }

        var parameters = new List<string>();
        int at = open + 1;
        while (at < methodSignature.Length && methodSignature[at] != ')')
        {
            int start = at;
            at = SkipType(methodSignature, at);
            if (at < 0)
            {
                return null;
            }

            parameters.Add(methodSignature[start..at]);
        }

        return parameters;
    }

    /// <summary>A method signature's return type as a signature, or null.</summary>
    public static string? ReturnSignature(string methodSignature)
    {
        int close = methodSignature.IndexOf(')');
        if (close < 0 || close + 1 >= methodSignature.Length)
        {
            return null;
        }

        int end = SkipType(methodSignature, close + 1);
        return end < 0 ? null : methodSignature[(close + 1)..end];
    }
}
