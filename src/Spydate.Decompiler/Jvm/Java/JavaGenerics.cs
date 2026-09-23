using Spydate.Core.Jvm;

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

        int? variable = ReturnedVariable(call, findClass);
        if (variable is not { } index)
        {
            return false;
        }

        // The receiver's own class, or a JDK interface it is listed under with the same arguments.
        if (receiverClass != call.Owner && !(Supertypes.TryGetValue(receiverClass, out var supers) && supers.Contains(call.Owner)))
        {
            return false;
        }

        var arguments = TypeArguments(generic);
        return index < arguments.Count && Erasure(arguments[index]) is { } erased && erased == castType;
    }

    /// <summary>Which of its class's type variables a method returns, from the table or the class's own signatures.</summary>
    private static int? ReturnedVariable(JCall call, Func<string, ClassFile?> findClass)
    {
        if (Returns.TryGetValue((call.Owner, call.Name), out int index) && JCall.ReturnType(call.Descriptor) == "Ljava/lang/Object;")
        {
            return index;
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
        return at >= 0 ? at : null;
    }

    /// <summary>A class signature's type variable names, in order: <c>&lt;K:…;V:…&gt;</c> is K, V.</summary>
    private static List<string> TypeVariables(string classSignature)
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
