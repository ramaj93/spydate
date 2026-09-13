using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Spydate.Debugger.Managed;

/// <summary>What sort of thing a value's text is, so a view can colour it the way an editor would.</summary>
public enum ManagedValueKind
{
    Plain,
    Null,
    Keyword,
    Number,
    Text,
    Character,
    Object,
    Enum,
    Property,
    Unavailable,
}

/// <summary>One step from a value to something inside it: a field, or an element.</summary>
/// <param name="Module">File name of the module declaring the field's class.</param>
/// <param name="Class">TypeDef token of that class — which may be a base of the value's own type.</param>
/// <param name="Field">FieldDef token.</param>
/// <param name="Element">Element position, or -1 for a field step.</param>
public readonly record struct ManagedStep(string Module, uint Class, uint Field, int Element = -1)
{
    public static ManagedStep ElementAt(int index) => new(string.Empty, 0, 0, index);

    public bool IsElement => Element >= 0;

    public override string ToString()
        => IsElement
            ? $"[{Element}]"
            : string.Create(CultureInfo.InvariantCulture, $"{Module}:{Class:X8}:{Field:X8}");
}

/// <summary>
/// How to get from a frame to a value: which argument or local, then which fields and elements.
///
/// A path rather than the runtime's value object, and that is the design rather than a convenience.
/// An <c>ICorDebugValue</c> is valid for as long as the process stays stopped and not a step longer,
/// so a tree that held them would be a tree of dangling pointers after the first F10. A path is
/// walked again from the frame each time a node is opened, which is cheap, always current, and
/// survives a step — so a node that was open stays open and shows what it holds now.
/// </summary>
public sealed record ManagedValuePath(bool Argument, uint Slot, ImmutableArray<ManagedStep> Steps)
{
    public static ManagedValuePath OfArgument(uint slot) => new(true, slot, ImmutableArray<ManagedStep>.Empty);

    public static ManagedValuePath OfLocal(uint slot) => new(false, slot, ImmutableArray<ManagedStep>.Empty);

    public ManagedValuePath Then(ManagedStep step) => this with { Steps = Steps.Add(step) };

    /// <summary>A stable spelling of the path, for remembering which nodes were open across a step.</summary>
    public string Key
        => (Argument ? "a" : "l") + Slot.ToString(CultureInfo.InvariantCulture)
           + string.Concat(Steps.Select(step => "/" + step));
}

/// <summary>
/// One row of a value tree: a name, what it holds, and what type it is.
///
/// <paramref name="Type"/> is the declared type, followed by the runtime type in braces when they
/// differ — <c>System.Collections.IDictionary {System.Collections.Specialized.HybridDictionary}</c> —
/// because both are true and the second is usually the one worth knowing.
/// </summary>
public sealed record ManagedVariable(
    string Name,
    string Value,
    string Type,
    ManagedValueKind Kind,
    bool Expandable,
    ManagedValuePath Path,
    ManagedPropertyGetter? Getter = null);

/// <summary>
/// What it takes to read a property: the module and token of its getter, and whether it is static.
///
/// A property has no value to read out of memory — it is a method, and its value is whatever running
/// that method returns. The row carries this so the caller can run the getter when a person opens the
/// object, rather than the tree running code the moment it is drawn.
/// </summary>
public sealed record ManagedPropertyGetter(string Module, uint Token, bool IsStatic);

/// <summary>
/// A value as a tree, one level at a time.
///
/// The flat preview this replaces had to decide in advance how deep to look — two levels, six wide —
/// and a line of text is the wrong shape for anything bigger than a small object: an <c>App</c> read
/// as forty fields run together with most of them cut off. A tree asks for a level when somebody
/// opens it, so it has no depth limit to choose and nothing to cut.
///
/// Fields rather than properties. A property is a method, and calling one means running code in the
/// stopped process — function evaluation, which is its own piece of work with its own hazards. A
/// field is memory the runtime can simply read. Auto-properties show under the name they were
/// written with, since their backing field carries it.
/// </summary>
internal static class ManagedVariables
{
    /// <summary>Longest string copied out for a tree row. Long enough for a path or a message.</summary>
    private const int MaxText = 1024;

    /// <summary>Most elements listed under one array before saying how many more there are.</summary>
    private const int MaxElements = 1000;

    /// <summary>A base chain longer than this is not a real type hierarchy.</summary>
    private const int MaxChain = 32;

    /// <summary>
    /// One value as a row. The value pointer is borrowed: it is the caller's to release.
    /// </summary>
    internal static ManagedVariable Present(IntPtr value, string name, string declared, ManagedValuePath path, ManagedTypes types)
    {
        if (value == IntPtr.Zero)
        {
            return Unavailable(name, declared, path);
        }

        int kind = KindOf(value);
        if (IsReference(kind))
        {
            bool? isNull = Com.Borrow<ICorDebugReferenceValue, bool?>(value, r => r.IsNull(out int n) == 0 ? n != 0 : null);
            if (isNull == true)
            {
                return new ManagedVariable(name, "null", TypeText(declared, null), ManagedValueKind.Null, false, path);
            }
        }

        IntPtr held = Held(value);
        if (held == IntPtr.Zero)
        {
            return Unavailable(name, declared, path);
        }

        try
        {
            return Describe(held, name, declared, path, types);
        }
        finally
        {
            Marshal.Release(held);
        }
    }

    /// <summary>
    /// What is inside a value: an array's elements, or an object's fields including every field its
    /// base classes declare. Borrowed pointer, as above.
    /// </summary>
    internal static IReadOnlyList<ManagedVariable> Children(IntPtr value, ManagedValuePath path, ManagedTypes types)
    {
        IntPtr held = Held(value);
        if (held == IntPtr.Zero)
        {
            return Array.Empty<ManagedVariable>();
        }

        try
        {
            if (Com.Borrow<ICorDebugStringValue, bool>(held, _ => true))
            {
                return Array.Empty<ManagedVariable>();
            }

            return Elements(held, path, types) ?? Fields(held, path, types);
        }
        finally
        {
            Marshal.Release(held);
        }
    }

    /// <summary>
    /// Takes one step along a path. Borrows <paramref name="value"/>; the result is owned, or zero
    /// when the step leads nowhere — a field of a null, an element past the end.
    /// </summary>
    internal static IntPtr Follow(IntPtr value, ManagedStep step)
    {
        IntPtr held = Held(value);
        if (held == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            if (step.IsElement)
            {
                return Com.Borrow<ICorDebugArrayValue, IntPtr?>(held, array =>
                    array.GetElementAtPosition((uint)step.Element, out IntPtr element) == 0 ? element : null) ?? IntPtr.Zero;
            }

            var chain = Chain(held);
            try
            {
                foreach (var link in chain)
                {
                    if (link.Token == step.Class
                        && string.Equals(FileName(link.Module), step.Module, StringComparison.OrdinalIgnoreCase))
                    {
                        return Com.Borrow<ICorDebugObjectValue, IntPtr?>(held, obj =>
                            obj.GetFieldValue(link.Class, step.Field, out IntPtr field) == 0 ? field : null) ?? IntPtr.Zero;
                    }
                }

                return IntPtr.Zero;
            }
            finally
            {
                Release(chain);
            }
        }
        finally
        {
            Marshal.Release(held);
        }
    }

    // ------------------------------------------------------------------

    private static ManagedVariable Describe(IntPtr held, string name, string declared, ManagedValuePath path, ManagedTypes types)
    {
        if (Com.Borrow<ICorDebugStringValue, bool>(held, _ => true))
        {
            return new ManagedVariable(
                name, ManagedValues.Text(held, MaxText) ?? "?", TypeText(declared, "string"), ManagedValueKind.Text, false, path);
        }

        if (Com.Borrow<ICorDebugArrayValue, uint?>(held, a => a.GetCount(out uint n) == 0 ? n : null) is { } count)
        {
            string element = ElementTypeName(held, types)
                             ?? ManagedValues.Name(Com.Borrow<ICorDebugArrayValue, int?>(held, a => a.GetElementType(out int k) == 0 ? k : null) ?? 0);
            return new ManagedVariable(
                name,
                string.Create(CultureInfo.InvariantCulture, $"{{{element}[{count}]}}"),
                TypeText(declared, element + "[]"),
                ManagedValueKind.Object,
                count > 0,
                path);
        }

        int kind = KindOf(held);
        if (kind is not (ManagedValues.ElementValueType or ManagedValues.ElementClass or ManagedValues.ElementObject))
        {
            return Primitive(held, kind, name, declared, ManagedValues.Name(kind), path);
        }

        IntPtr cls = Com.Borrow<ICorDebugObjectValue, IntPtr?>(held, o => o.GetClass(out IntPtr c) == 0 ? c : null) ?? IntPtr.Zero;
        if (cls == IntPtr.Zero)
        {
            return Unavailable(name, declared, path);
        }

        try
        {
            var (module, token) = Identity(cls);
            string actual = types.FullName(module, token) ?? "?";

            // A boxed int unboxes to a value whose class is System.Int32. It is a number, and
            // offering to expand it into its one private field would be a debugger showing off.
            if (KeywordKind(actual) is { } primitive)
            {
                return Primitive(held, primitive, name, declared, actual, path);
            }

            if (types.IsEnum(module, token))
            {
                ulong raw = Raw(held, out _) ?? 0;
                return new ManagedVariable(
                    name, types.EnumText(module, token, raw) ?? raw.ToString(CultureInfo.InvariantCulture),
                    TypeText(declared, actual), ManagedValueKind.Enum, false, path);
            }

            if (actual.StartsWith("System.Nullable<", StringComparison.Ordinal))
            {
                return Nullable(held, cls, module, token, name, declared, path, types);
            }

            return new ManagedVariable(
                name, "{" + actual + "}", TypeText(declared, actual), ManagedValueKind.Object, actual != "object", path);
        }
        finally
        {
            Marshal.Release(cls);
        }
    }

    /// <summary>
    /// <c>int?</c> as what it holds, or null. Its two fields are an implementation detail, and a row
    /// that opens to <c>hasValue = true, value = 5</c> makes the reader do the runtime's job.
    /// </summary>
    private static ManagedVariable Nullable(
        IntPtr held, IntPtr cls, string? module, uint token, string name, string declared, ManagedValuePath path, ManagedTypes types)
    {
        var fields = types.Fields(module, token, int.MaxValue);
        var has = fields.FirstOrDefault(f => f.Name == "hasValue");
        var inside = fields.FirstOrDefault(f => f.Name == "value");
        if (has.Token == 0 || inside.Token == 0)
        {
            return new ManagedVariable(name, "{" + (types.FullName(module, token) ?? "?") + "}", declared, ManagedValueKind.Object, true, path);
        }

        bool present = Com.Borrow<ICorDebugObjectValue, bool?>(held, obj =>
        {
            if (obj.GetFieldValue(cls, has.Token, out IntPtr flag) < 0 || flag == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return (Raw(flag, out _) ?? 0) != 0;
            }
            finally
            {
                Marshal.Release(flag);
            }
        }) ?? false;

        if (!present)
        {
            return new ManagedVariable(name, "null", declared, ManagedValueKind.Null, false, path);
        }

        var step = new ManagedStep(FileName(module), token, inside.Token);
        return Com.Borrow<ICorDebugObjectValue, ManagedVariable>(held, obj =>
        {
            if (obj.GetFieldValue(cls, inside.Token, out IntPtr value) < 0 || value == IntPtr.Zero)
            {
                return Unavailable(name, declared, path);
            }

            try
            {
                return Present(value, name, declared, path.Then(step), types);
            }
            finally
            {
                Marshal.Release(value);
            }
        }) ?? Unavailable(name, declared, path);
    }

    private static ManagedVariable Primitive(IntPtr held, int kind, string name, string declared, string actual, ManagedValuePath path)
    {
        string? text;
        ManagedValueKind style;

        switch (kind)
        {
            case ManagedValues.ElementBoolean:
                text = ManagedValues.Primitive(held, kind);
                style = ManagedValueKind.Keyword;
                break;

            case ManagedValues.ElementChar:
                text = ManagedValues.Primitive(held, kind);
                style = ManagedValueKind.Character;
                break;

            case ManagedValues.ElementI or ManagedValues.ElementU or ManagedValues.ElementPtr or ManagedValues.ElementFnPtr:
                // Padded to the width of the thing, the way a pointer is always written: a handle of
                // zero reads 0x0000000000000000, which is visibly a pointer and visibly empty.
                ulong? raw = Raw(held, out int size);
                text = raw is { } bits ? "0x" + bits.ToString("X" + (size * 2), CultureInfo.InvariantCulture) : null;
                style = ManagedValueKind.Number;
                break;

            default:
                text = ManagedValues.Primitive(held, kind);
                style = ManagedValueKind.Number;
                break;
        }

        return text is null
            ? Unavailable(name, declared, path)
            : new ManagedVariable(name, text, TypeText(declared, actual), style, false, path);
    }

    private static IReadOnlyList<ManagedVariable>? Elements(IntPtr held, ManagedValuePath path, ManagedTypes types)
        => Com.Borrow<ICorDebugArrayValue, IReadOnlyList<ManagedVariable>>(held, array =>
        {
            if (array.GetCount(out uint count) < 0)
            {
                return null;
            }

            string element = ElementTypeName(held, types) ?? string.Empty;
            int shown = (int)Math.Min(count, MaxElements);
            var rows = new List<ManagedVariable>(shown + 1);

            for (int i = 0; i < shown; i++)
            {
                string label = string.Create(CultureInfo.InvariantCulture, $"[{i}]");
                var at = path.Then(ManagedStep.ElementAt(i));
                if (array.GetElementAtPosition((uint)i, out IntPtr item) < 0 || item == IntPtr.Zero)
                {
                    rows.Add(Unavailable(label, element, at));
                    continue;
                }

                try
                {
                    rows.Add(Present(item, label, element, at, types));
                }
                finally
                {
                    Marshal.Release(item);
                }
            }

            if (count > shown)
            {
                rows.Add(new ManagedVariable(
                    "…", string.Create(CultureInfo.InvariantCulture, $"{count - shown} more not listed"),
                    string.Empty, ManagedValueKind.Unavailable, false, path));
            }

            return rows;
        });

    private static IReadOnlyList<ManagedVariable> Fields(IntPtr held, ManagedValuePath path, ManagedTypes types)
    {
        var chain = Chain(held);
        try
        {
            var rows = new List<(ManagedVariable Row, string Owner)>();

            foreach (var link in chain)
            {
                string owner = types.FullName(link.Module, link.Token) ?? string.Empty;

                // The bottom of every hierarchy, and nothing a reader is looking for is stored there.
                if (owner is "object" or "System.ValueType" or "System.Enum")
                {
                    break;
                }

                var properties = types.Properties(link.Module, link.Token);

                // An auto-property's backing field carries the same value under the same name, so the
                // property row stands for it and the field is not listed a second time. Only in the
                // tree — the flat preview has no property rows, so there it keeps the field.
                var covered = properties
                    .Select(p => p.Name)
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var field in types.Fields(link.Module, link.Token, int.MaxValue))
                {
                    if (covered.Contains(field.Name))
                    {
                        continue;
                    }

                    var at = path.Then(new ManagedStep(FileName(link.Module), link.Token, field.Token));
                    var row = Com.Borrow<ICorDebugObjectValue, ManagedVariable>(held, obj =>
                    {
                        if (obj.GetFieldValue(link.Class, field.Token, out IntPtr value) < 0 || value == IntPtr.Zero)
                        {
                            return Unavailable(field.Name, field.Type, at);
                        }

                        try
                        {
                            return Present(value, field.Name, field.Type, at, types);
                        }
                        finally
                        {
                            Marshal.Release(value);
                        }
                    }) ?? Unavailable(field.Name, field.Type, at);

                    rows.Add((row, owner));
                }

                // Properties are rows too, but their values are not read here — a property is a
                // method, and running it means running code in the process, which cannot be done
                // while a frame is borrowed. Each row carries what it takes to run the getter later;
                // the caller evaluates them one at a time. Its value stays "…" until it does.
                foreach (var property in properties)
                {
                    rows.Add((
                        new ManagedVariable(
                            property.Name,
                            "…",
                            property.Type,
                            ManagedValueKind.Property,
                            false,
                            path,
                            new ManagedPropertyGetter(FileName(link.Module), property.GetterToken, property.IsStatic)),
                        owner));
                }
            }

            // Alphabetical, as every debugger lists members, with a field a base class also declares
            // under the same name told apart by who declared it — otherwise two rows called `name`
            // look like a mistake.
            var repeated = rows.GroupBy(r => r.Row.Name, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.Ordinal);

            return rows
                .Select(r => repeated.Contains(r.Row.Name)
                    ? r.Row with { Name = $"{r.Row.Name} ({Short(r.Owner)})" }
                    : r.Row)
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            Release(chain);
        }
    }

    private readonly record struct Link(IntPtr Class, string? Module, uint Token);

    /// <summary>
    /// The class of a value and of every base under it, most derived first. Each class pointer is
    /// owned and has to be given back with <see cref="Release"/>.
    ///
    /// Through the exact type when the runtime offers it, which is the only thing that knows the
    /// base types. The object's own class is the fallback, and with it only the fields the most
    /// derived type declared can be read.
    /// </summary>
    private static List<Link> Chain(IntPtr held)
    {
        var chain = new List<Link>();

        IntPtr type = ExactType(held);
        if (type == IntPtr.Zero)
        {
            IntPtr only = Com.Borrow<ICorDebugObjectValue, IntPtr?>(held, o => o.GetClass(out IntPtr c) == 0 ? c : null) ?? IntPtr.Zero;
            if (only != IntPtr.Zero)
            {
                var (module, token) = Identity(only);
                chain.Add(new Link(only, module, token));
            }

            return chain;
        }

        while (type != IntPtr.Zero && chain.Count < MaxChain)
        {
            var (cls, next) = Com.Borrow<ICorDebugType, (IntPtr, IntPtr)?>(type, t =>
            {
                IntPtr c = t.GetClass(out IntPtr found) == 0 ? found : IntPtr.Zero;
                IntPtr b = t.GetBase(out IntPtr below) == 0 ? below : IntPtr.Zero;
                return (c, b);
            }) ?? (IntPtr.Zero, IntPtr.Zero);

            Marshal.Release(type);

            if (cls != IntPtr.Zero)
            {
                var (module, token) = Identity(cls);
                chain.Add(new Link(cls, module, token));
            }

            type = next;
        }

        if (type != IntPtr.Zero)
        {
            Marshal.Release(type);
        }

        return chain;
    }

    private static void Release(List<Link> chain)
    {
        foreach (var link in chain)
        {
            Marshal.Release(link.Class);
        }
    }

    /// <summary>A class's module path and TypeDef token.</summary>
    private static (string? Module, uint Token) Identity(IntPtr cls)
        => Com.Borrow<ICorDebugClass, (string?, uint)?>(cls, c =>
        {
            uint token = c.GetToken(out uint t) == 0 ? t : 0;
            string? module = c.GetModule(out IntPtr owner) == 0
                ? Com.Owned<ICorDebugModule, string>(owner, m => Com.NameOf(m))
                : null;
            return (module, token);
        }) ?? (null, 0);

    /// <summary>The runtime type of a value, owned, or zero when the runtime will not say.</summary>
    private static IntPtr ExactType(IntPtr value)
        => Com.Borrow<ICorDebugValue2, IntPtr?>(value, v => v.GetExactType(out IntPtr t) == 0 ? t : null) ?? IntPtr.Zero;

    /// <summary>What an array holds, from its exact type: <c>Point</c> for a <c>Point[]</c>.</summary>
    private static string? ElementTypeName(IntPtr array, ManagedTypes types)
    {
        IntPtr type = ExactType(array);
        if (type == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Com.Borrow<ICorDebugType, string>(type, t =>
                t.GetFirstTypeParameter(out IntPtr element) == 0 && element != IntPtr.Zero
                    ? Com.Owned<ICorDebugType, string>(element, e => TypeName(e, types, 0))
                    : null);
        }
        finally
        {
            Marshal.Release(type);
        }
    }

    private static string? TypeName(ICorDebugType type, ManagedTypes types, int depth)
    {
        if (depth > 8 || type.GetType(out int kind) < 0)
        {
            return null;
        }

        if (kind is ManagedValues.ElementClass or ManagedValues.ElementValueType)
        {
            return type.GetClass(out IntPtr cls) == 0 && cls != IntPtr.Zero
                ? Com.Owned<ICorDebugClass, string>(cls, c =>
                {
                    uint token = c.GetToken(out uint t) == 0 ? t : 0;
                    string? module = c.GetModule(out IntPtr owner) == 0
                        ? Com.Owned<ICorDebugModule, string>(owner, m => Com.NameOf(m))
                        : null;
                    return types.FullName(module, token);
                })
                : null;
        }

        if (kind is ManagedValues.ElementSzArray or ManagedValues.ElementArray)
        {
            return type.GetFirstTypeParameter(out IntPtr element) == 0 && element != IntPtr.Zero
                ? Com.Owned<ICorDebugType, string>(element, e => TypeName(e, types, depth + 1)) + "[]"
                : null;
        }

        return ManagedValues.Name(kind);
    }

    /// <summary>A value that is not behind a reference, or what a reference points at; boxes opened. Owned.</summary>
    private static IntPtr Held(IntPtr value)
    {
        IntPtr held;
        if (IsReference(KindOf(value)))
        {
            held = Com.Borrow<ICorDebugReferenceValue, IntPtr?>(value, r =>
                r.IsNull(out int isNull) == 0 && isNull == 0 && r.Dereference(out IntPtr pointed) == 0 ? pointed : null) ?? IntPtr.Zero;
        }
        else
        {
            Marshal.AddRef(value);
            held = value;
        }

        if (held == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr unboxed = Com.Borrow<ICorDebugBoxValue, IntPtr?>(held, box =>
            box.GetObject(out IntPtr inside) == 0 ? inside : null) ?? IntPtr.Zero;
        if (unboxed == IntPtr.Zero)
        {
            return held;
        }

        Marshal.Release(held);
        return unboxed;
    }

    /// <summary>Up to eight bytes of a value, zero-extended. Null when it is larger or unreadable.</summary>
    internal static ulong? Raw(IntPtr value, out int size)
    {
        int bytes = Com.Borrow<ICorDebugValue, int?>(value, v => v.GetSize(out uint s) == 0 ? (int)s : null) ?? 0;
        size = bytes;
        if (bytes is <= 0 or > 8)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocCoTaskMem(8);
        try
        {
            Marshal.WriteInt64(buffer, 0);
            bool ok = Com.Borrow<ICorDebugGenericValue, bool?>(value, g => g.GetValue(buffer) == 0) ?? false;
            if (!ok)
            {
                return null;
            }

            ulong all = (ulong)Marshal.ReadInt64(buffer);
            return bytes == 8 ? all : all & ((1UL << (bytes * 8)) - 1);
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    private static int KindOf(IntPtr value)
        => Com.Borrow<ICorDebugValue, int?>(value, v => v.GetKind(out int k) == 0 ? k : null) ?? 0;

    private static bool IsReference(int kind)
        => kind is ManagedValues.ElementString or ManagedValues.ElementClass or ManagedValues.ElementObject
            or ManagedValues.ElementSzArray or ManagedValues.ElementArray;

    /// <summary>The element type a built-in type's keyword stands for, when it is one.</summary>
    private static int? KeywordKind(string name) => name switch
    {
        "bool" => ManagedValues.ElementBoolean,
        "char" => ManagedValues.ElementChar,
        "sbyte" => ManagedValues.ElementI1,
        "byte" => ManagedValues.ElementU1,
        "short" => ManagedValues.ElementI2,
        "ushort" => ManagedValues.ElementU2,
        "int" => ManagedValues.ElementI4,
        "uint" => ManagedValues.ElementU4,
        "long" => ManagedValues.ElementI8,
        "ulong" => ManagedValues.ElementU8,
        "float" => ManagedValues.ElementR4,
        "double" => ManagedValues.ElementR8,
        "System.IntPtr" => ManagedValues.ElementI,
        "System.UIntPtr" => ManagedValues.ElementU,
        _ => null,
    };

    private static ManagedVariable Unavailable(string name, string declared, ManagedValuePath path)
        => new(name, "not available", declared, ManagedValueKind.Unavailable, false, path);

    /// <summary>The declared type, and the runtime type in braces when it says something the declared one does not.</summary>
    private static string TypeText(string declared, string? actual)
    {
        if (string.IsNullOrEmpty(actual))
        {
            return declared;
        }

        if (string.IsNullOrEmpty(declared))
        {
            return actual;
        }

        return Same(declared, actual) ? declared : $"{declared} {{{actual}}}";
    }

    /// <summary>
    /// Whether two spellings are the same type. A runtime class is a definition and knows no type
    /// arguments, so <c>List&lt;int&gt;</c> declared and <c>List&lt;T&gt;</c> held are the same thing.
    /// </summary>
    private static bool Same(string declared, string actual) => Bare(declared) == Bare(actual);

    private static string Bare(string name)
    {
        var bare = new System.Text.StringBuilder(name.Length);
        int depth = 0;
        foreach (char c in name)
        {
            if (c == '<')
            {
                depth++;
            }
            else if (c == '>')
            {
                depth--;
            }
            else if (depth == 0 && c != '?')
            {
                bare.Append(c);
            }
        }

        return bare.ToString() switch
        {
            "nint" => "System.IntPtr",
            "nuint" => "System.UIntPtr",
            var other => other,
        };
    }

    private static string Short(string fullName)
    {
        int dot = fullName.LastIndexOf('.');
        return dot >= 0 ? fullName[(dot + 1)..] : fullName;
    }

    private static string FileName(string? module) => string.IsNullOrEmpty(module) ? string.Empty : Path.GetFileName(module);
}
