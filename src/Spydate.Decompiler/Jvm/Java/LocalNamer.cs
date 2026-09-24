using Spydate.Core.Jvm;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// Names a method's local variable slots. A slot is a register, not a variable: the compiler reuses one for
/// several variables, even of different types. The local variable table says which variable a slot holds at
/// each point, when the class kept it; without it a slot is <c>varN</c>, split by the kind of value stored
/// (<c>var3</c>, <c>var3_l</c>) so one name never holds two types. Parameters take their names from the table,
/// then <c>MethodParameters</c>, then <c>argN</c>.
/// </summary>
internal sealed class LocalNamer
{
    private readonly JvmMethod _method;
    private readonly CodeAttribute _code;
    private readonly Dictionary<string, JLocal> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<(int Slot, char Kind), string> _untabled = new();
    private readonly Dictionary<LocalVariable, string> _tabled = new();
    private readonly Dictionary<string, string?> _declared = new(StringComparer.Ordinal);
    private readonly Dictionary<int, JLocal> _parameterSlots = new();
    private readonly HashSet<string> _untabledNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _signatures = new(StringComparer.Ordinal);

    public LocalNamer(ClassFile file, JvmMethod method, CodeAttribute code, IReadOnlySet<string>? reserved = null)
    {
        _method = method;
        _code = code;

        // Names taken outside — a lambda's enclosing method's locals — are held by placeholders nothing prints.
        foreach (string name in reserved ?? (IReadOnlySet<string>)new HashSet<string>())
        {
            _byName[name] = new JLocal(name, null, JLocalKind.This);
        }

        int slot = 0;
        if ((method.Access & JvmAccess.Static) == 0)
        {
            var self = new JLocal("this", $"L{file.Name};", JLocalKind.This);
            _byName[self.Name] = self;
            _parameterSlots[0] = self;
            slot = 1;
        }

        var parameters = Descriptors.ParameterDescriptors(method.Descriptor);
        var generic = method.Signature is { } methodSignature ? JavaGenerics.ParameterSignatures(methodSignature) : null;
        for (int i = 0; i < parameters.Count; i++)
        {
            string? name = i < method.ParameterNames.Count ? method.ParameterNames[i] : null;
            name = code.Locals.FirstOrDefault(l => l.Slot == slot && l.StartPc == 0)?.Name ?? name;
            var parameter = new JLocal(Unique(Clean(name) ?? $"arg{i}"), parameters[i], JLocalKind.Parameter);
            if (!code.Locals.Any(l => l.Slot == slot && l.StartPc == 0))
            {
                _untabledNames.Add(parameter.Name);
            }

            // Parameterized, or a type variable (T, T[]): anything the descriptor alone does not say.
            if (generic is not null && generic.Count == parameters.Count && generic[i] != parameters[i])
            {
                _signatures[parameter.Name] = generic[i];
            }

            _byName[parameter.Name] = parameter;
            _parameterSlots[slot] = parameter;
            Parameters.Add(parameter);
            slot += parameters[i] is "J" or "D" ? 2 : 1;
        }
    }

    /// <summary>Generic types, as signatures, of the locals and parameters that have one: <c>Ljava/util/List&lt;Ljava/lang/String;&gt;;</c>.</summary>
    public IReadOnlyDictionary<string, string> Signatures => _signatures;

    /// <summary>A generic type found for a local the tables did not describe.</summary>
    public void Infer(string name, string signature) => _signatures.TryAdd(name, signature);

    /// <summary>The parameters in order, <c>this</c> excluded.</summary>
    public List<JLocal> Parameters { get; } = [];

    /// <summary>Every local that is not a parameter, with the type to declare it as.</summary>
    public IReadOnlyDictionary<string, string?> Declared => _declared;

    public JLocal Load(int slot, string kind, int pc) => Resolve(slot, kind, pc, null);

    /// <summary>A store's variable starts just after it, which is where the table says it is live from.</summary>
    public JLocal Store(int slot, string kind, int liveFrom, string? valueType) => Resolve(slot, kind, liveFrom, valueType);

    private readonly HashSet<string> _caught = new(StringComparer.Ordinal);

    /// <summary>Marks a local as a catch clause's variable, which the clause declares.</summary>
    public void Caught(string name) => _caught.Add(name);

    public bool IsCaught(string name) => _caught.Contains(name);

    /// <summary>A new local under a name nothing else in the method uses: <c>ex</c>, <c>ex2</c>.</summary>
    public JLocal Fresh(string name, string? type) => Local(Unique(name), type);

    /// <summary>
    /// True for a slot the local variable table does not describe — every slot of a class compiled without one, and
    /// the compiler's own slots in one compiled with it. One name then covers everything the slot ever holds, which
    /// <see cref="JavaLocals"/> splits into the variables it really is.
    /// </summary>
    public bool IsUntabled(string name) => _untabledNames.Contains(name);

    /// <summary>
    /// A new local no table describes, under a name nothing else uses: a variable a lifter told apart itself, which
    /// folds and splits like any untabled one.
    /// </summary>
    public JLocal Untabled(string name, string? type)
    {
        string unique = name;
        for (int i = 2; _byName.ContainsKey(unique) || _declared.ContainsKey(unique) || unique == "this"; i++)
        {
            unique = $"{name}_{i}";
        }

        var local = Local(unique, type);
        _untabledNames.Add(local.Name);
        return local;
    }

    /// <summary>A new local for another variable that shared a slot with <paramref name="name"/>: <c>var3_2</c>.</summary>
    public JLocal Split(string name, string? type)
    {
        for (int i = 2; ; i++)
        {
            string candidate = $"{name}_{i}";
            if (!_byName.ContainsKey(candidate) && !_declared.ContainsKey(candidate))
            {
                var local = Local(candidate, type);
                _untabledNames.Add(candidate);
                return local;
            }
        }
    }

    /// <summary>Gives a local the type its uses showed it has.</summary>
    public JLocal Retype(JLocal local, string? type)
    {
        var retyped = local with { LocalType = type };
        if (_byName.ContainsKey(local.Name))
        {
            _byName[local.Name] = retyped;
        }

        if (_declared.ContainsKey(local.Name))
        {
            _declared[local.Name] = type;
        }

        return retyped;
    }

    /// <summary>Declares a local the lifter or the region builder made up — a temporary, a stack variable — with its type.</summary>
    public void Declare(JLocal local)
    {
        if (local.Kind is not (JLocalKind.Parameter or JLocalKind.This))
        {
            _declared.TryAdd(local.Name, local.Type);
        }
    }

    private JLocal Resolve(int slot, string kind, int pc, string? valueType)
    {
        // A parameter's slot holds the parameter until the table says another variable took it over. A table entry
        // of another kind of value — a reference where an int is stored — is not this variable: D8's debug tables keep
        // a variable live a little past the register's reuse.
        var entry = Entry(slot, pc, kind);

        if (entry is null && _parameterSlots.TryGetValue(slot, out var parameter) && !Reassigned(slot) && Category(parameter.Type ?? "I") == Category(kind))
        {
            return parameter;
        }

        if (entry is not null)
        {
            if (_parameterSlots.TryGetValue(slot, out var tabledParameter) && entry.StartPc == 0)
            {
                return tabledParameter;
            }

            if (!_tabled.TryGetValue(entry, out string? tabledName))
            {
                tabledName = UniqueFor(Clean(entry.Name) ?? $"var{slot}", entry.Descriptor);
                _tabled[entry] = tabledName;
                if (entry.Signature is { } signature && JavaGenerics.IsParameterized(signature))
                {
                    _signatures.TryAdd(tabledName, signature);
                }
            }

            return Local(tabledName, entry.Descriptor);
        }

        char category = kind[0] == 'L' || kind[0] == '[' ? 'a' : char.ToLowerInvariant(kind[0]);
        if (!_untabled.TryGetValue((slot, category), out string? name))
        {
            bool first = !_untabled.Keys.Any(k => k.Slot == slot);
            name = Unique(first ? $"var{slot}" : $"var{slot}_{category}");
            _untabled[(slot, category)] = name;
            _untabledNames.Add(name);
        }

        // Without a table a reference slot is only "an object"; the first value stored in it says more.
        string type = category == 'a' ? valueType is { Length: > 0 } t && t[0] is 'L' or '[' ? t : "Ljava/lang/Object;" : kind;
        return Local(name, type);
    }

    /// <summary>A type's kind of value: a reference, a long, a double, a float, or a 32-bit integer (int, boolean, char, byte, short).</summary>
    private static char Category(string type) => type[0] switch
    {
        'L' or '[' => 'a',
        'J' => 'j',
        'D' => 'd',
        'F' => 'f',
        _ => 'i',
    };

    private JLocal Local(string name, string? type)
    {
        if (!_byName.TryGetValue(name, out var local))
        {
            local = new JLocal(name, type);
            _byName[name] = local;
            _declared[name] = type;
        }

        return local;
    }

    /// <summary>
    /// The table's entry for a slot at a point, when the class kept one, of the kind of value <paramref name="kind"/>
    /// is: where two ranges meet at the point — one variable ending as the next starts in the same register — the
    /// one holding that kind of value is the one meant.
    /// </summary>
    private LocalVariable? Entry(int slot, int pc, string kind)
    {
        foreach (var local in _code.Locals)
        {
            if (local.Slot == slot && pc >= local.StartPc && pc <= local.StartPc + local.Length && Category(local.Descriptor) == Category(kind))
            {
                return local;
            }
        }

        return null;
    }

    /// <summary>Whether the table shows a slot of a parameter reused for another variable — in which case a bare load is not the parameter.</summary>
    private bool Reassigned(int slot) => _code.Locals.Any(l => l.Slot == slot && l.StartPc > 0);

    /// <summary>A name not yet used for a different type: the same name in two scopes with one type is one variable.</summary>
    private string UniqueFor(string name, string type)
    {
        if (!_byName.TryGetValue(name, out var existing) || existing.Type == type && existing.Kind == JLocalKind.Local)
        {
            return name;
        }

        return Unique(name);
    }

    private string Unique(string name)
    {
        if (!_byName.ContainsKey(name) && name != "this")
        {
            return name;
        }

        for (int i = 2; ; i++)
        {
            string candidate = $"{name}{i}";
            if (!_byName.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>A name from the class file, if it is one Java could have: obfuscators write keywords and worse.</summary>
    private static string? Clean(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64 || !(char.IsLetter(name[0]) || name[0] is '_' or '$')
            || !name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$') || JavaNames.IsKeyword(name))
        {
            return null;
        }

        return name;
    }
}
