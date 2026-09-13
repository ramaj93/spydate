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
    Group,
    Unavailable,
}

/// <summary>One step from a value to something inside it: a field, an element, or a static member.</summary>
/// <param name="Module">File name of the module declaring the field's class.</param>
/// <param name="Class">TypeDef token of that class — which may be a base of the value's own type.</param>
/// <param name="Field">FieldDef token, or 0 for the static-members group itself.</param>
/// <param name="Element">Element position, or -1 for a field step.</param>
/// <param name="Static">
/// The step is into the type's statics rather than into the value: with a field, that static field;
/// with none, the group that holds them. A static lives on the type, not in the object, so it is read
/// through the class and a frame rather than through the value.
/// </param>
public readonly record struct ManagedStep(string Module, uint Class, uint Field, int Element = -1, bool Static = false)
{
    public static ManagedStep ElementAt(int index) => new(string.Empty, 0, 0, index);

    /// <summary>The group row's step: the statics of this class and of every base under it.</summary>
    public static ManagedStep StaticsOf(string module, uint type) => new(module, type, 0, -1, true);

    public bool IsElement => Element >= 0;

    /// <summary>The group of statics, rather than one of them.</summary>
    public bool IsStaticGroup => Static && Field == 0;

    public override string ToString()
        => IsElement
            ? $"[{Element}]"
            : string.Create(CultureInfo.InvariantCulture, $"{(Static ? "s:" : string.Empty)}{Module}:{Class:X8}:{Field:X8}");
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
public sealed record ManagedValuePath(ManagedValueRoot Root, uint Slot, ImmutableArray<ManagedStep> Steps)
{
    public static ManagedValuePath OfArgument(uint slot) => new(ManagedValueRoot.Argument, slot, ImmutableArray<ManagedStep>.Empty);

    public static ManagedValuePath OfLocal(uint slot) => new(ManagedValueRoot.Local, slot, ImmutableArray<ManagedStep>.Empty);

    /// <summary>
    /// A path starting from a value a getter produced, kept alive by a handle the session holds.
    ///
    /// An evaluated value is reachable from no frame — it is what a method returned, not something
    /// stored anywhere a path could name — so opening one needs the value itself kept. The handle is
    /// the keeping, and this is how a row refers to it.
    /// </summary>
    public static ManagedValuePath OfEvaluated(uint handle) => new(ManagedValueRoot.Evaluated, handle, ImmutableArray<ManagedStep>.Empty);

    /// <summary>Whether the path starts at an argument slot. Locals and evaluated values do not.</summary>
    public bool Argument => Root == ManagedValueRoot.Argument;

    public ManagedValuePath Then(ManagedStep step) => this with { Steps = Steps.Add(step) };

    /// <summary>A stable spelling of the path, for remembering which nodes were open across a step.</summary>
    public string Key
        => Root switch { ManagedValueRoot.Argument => "a", ManagedValueRoot.Local => "l", _ => "e" }
           + Slot.ToString(CultureInfo.InvariantCulture)
           + string.Concat(Steps.Select(step => "/" + step));
}

/// <summary>Where a path starts: a frame's argument or local, or a value an evaluation produced.</summary>
public enum ManagedValueRoot
{
    Argument,
    Local,
    Evaluated,
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
    ManagedPropertyGetter? Getter = null,
    bool CanSet = false);

/// <summary>
/// What it takes to read a property: the module and token of its getter, and whether it is static.
///
/// A property has no value to read out of memory — it is a method, and its value is whatever running
/// that method returns. The row carries this so the caller can run the getter when a person opens the
/// object, rather than the tree running code the moment it is drawn.
/// </summary>
public sealed record ManagedPropertyGetter(string Module, uint Class, uint Token, bool IsStatic);

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
                return new ManagedVariable(name, "null", TypeText(declared, null), ManagedValueKind.Null, false, path, null, true);
            }
        }

        IntPtr held = Held(value);
        if (held == IntPtr.Zero)
        {
            return Unavailable(name, declared, path);
        }

        try
        {
            var described = Describe(held, name, declared, path, types);

            // What can be written back: a reference (to null, or to a new string) and anything the
            // runtime will copy bytes into — numbers, characters, bools, enums. A struct read in
            // place is not offered, because writing one means writing every field of it.
            bool settable = described.Kind is not (ManagedValueKind.Unavailable or ManagedValueKind.Property or ManagedValueKind.Group)
                && (IsReference(kind)
                    || described.Kind == ManagedValueKind.Enum
                    || kind is not (ManagedValues.ElementValueType or ManagedValues.ElementClass or ManagedValues.ElementObject));

            return settable ? described with { CanSet = true } : described;
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
            bool anyStatics = false;

            foreach (var link in chain)
            {
                string owner = types.FullName(link.Module, link.Token) ?? string.Empty;

                // The bottom of every hierarchy, and nothing a reader is looking for is stored there.
                if (owner is "object" or "System.ValueType" or "System.Enum")
                {
                    break;
                }

                var properties = types.Properties(link.Module, link.Token);

                // Statics belong to the type, not to this object, so they are gathered under one row
                // of their own rather than mixed in with what the object itself holds.
                anyStatics |= properties.Any(p => p.IsStatic) || types.StaticFields(link.Module, link.Token).Count > 0;

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
                foreach (var property in properties.Where(p => !p.IsStatic))
                {
                    rows.Add((
                        new ManagedVariable(
                            property.Name,
                            "…",
                            property.Type,
                            ManagedValueKind.Property,
                            false,
                            path,
                            new ManagedPropertyGetter(FileName(link.Module), link.Token, property.GetterToken, property.IsStatic)),
                        owner));
                }
            }

            if (anyStatics && chain.Count > 0)
            {
                rows.Add((
                    new ManagedVariable(
                        "Static members",
                        string.Empty,
                        string.Empty,
                        ManagedValueKind.Group,
                        true,
                        path.Then(ManagedStep.StaticsOf(FileName(chain[0].Module), chain[0].Token))),
                    string.Empty));
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

    /// <summary>
    /// One class in a value's hierarchy. <paramref name="Type"/> is the instantiated type it came
    /// from — <c>List&lt;int&gt;</c> where <paramref name="Class"/> is only <c>List&lt;T&gt;</c> — and
    /// is what a method on a generic type has to be called with. Zero when the runtime gave no type.
    /// </summary>
    internal readonly record struct Link(IntPtr Class, string? Module, uint Token, IntPtr Type);

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
                chain.Add(new Link(only, module, token, IntPtr.Zero));
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

            if (cls != IntPtr.Zero)
            {
                var (module, token) = Identity(cls);

                // The type is kept rather than released: it is the instantiated one, and a method on
                // a generic type cannot be called without its arguments.
                chain.Add(new Link(cls, module, token, type));
            }
            else
            {
                Marshal.Release(type);
            }

            type = next;
        }

        if (type != IntPtr.Zero)
        {
            Marshal.Release(type);
        }

        return chain;
    }

    internal static void Release(List<Link> chain)
    {
        foreach (var link in chain)
        {
            Marshal.Release(link.Class);
            if (link.Type != IntPtr.Zero)
            {
                Marshal.Release(link.Type);
            }
        }
    }

    /// <summary>The classes of a value, most derived first. The caller gives them back with Release.</summary>
    internal static List<Link> ChainOf(IntPtr held) => Chain(held);

    /// <summary>
    /// The type arguments of a link's type — what <c>CallParameterizedFunction</c> needs to call a
    /// method on a generic type. Owned pointers; empty for a type that is not generic.
    /// </summary>
    internal static List<IntPtr> TypeArguments(Link link)
    {
        var found = new List<IntPtr>();
        if (link.Type == IntPtr.Zero)
        {
            return found;
        }

        IntPtr enumerator = Com.Borrow<ICorDebugType, IntPtr?>(link.Type, t =>
            t.EnumerateTypeParameters(out IntPtr e) == 0 ? e : null) ?? IntPtr.Zero;
        if (enumerator == IntPtr.Zero)
        {
            return found;
        }

        try
        {
            var types = Com.Keep<ICorDebugTypeEnum>(enumerator);
            if (types is null)
            {
                return found;
            }

            try
            {
                while (found.Count < MaxChain
                       && types.Next(1, out IntPtr one, out uint got) == 0 && got == 1 && one != IntPtr.Zero)
                {
                    found.Add(one);
                }
            }
            finally
            {
                Com.Drop(types);
            }
        }
        finally
        {
            Marshal.Release(enumerator);
        }

        return found;
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

    /// <summary>
    /// The statics of a value's whole hierarchy: fields read through the class, and properties left
    /// for the caller to run. A static needs a frame as well as a class — which app domain and which
    /// thread it belongs to is decided by where execution is, and thread statics differ per thread.
    /// </summary>
    internal static IReadOnlyList<ManagedVariable> StaticMembers(IntPtr held, ManagedValuePath path, ManagedTypes types, IntPtr frame)
    {
        var chain = Chain(held);
        try
        {
            var rows = new List<ManagedVariable>();

            foreach (var link in chain)
            {
                string owner = types.FullName(link.Module, link.Token) ?? string.Empty;
                if (owner is "object" or "System.ValueType" or "System.Enum")
                {
                    break;
                }

                var properties = types.Properties(link.Module, link.Token).Where(p => p.IsStatic).ToList();
                var covered = properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

                foreach (var field in types.StaticFields(link.Module, link.Token))
                {
                    if (covered.Contains(field.Name))
                    {
                        continue;
                    }

                    var at = path.Then(new ManagedStep(FileName(link.Module), link.Token, field.Token, -1, true));
                    rows.Add(StaticField(link, field, at, types, frame));
                }

                foreach (var property in properties)
                {
                    rows.Add(new ManagedVariable(
                        property.Name,
                        "…",
                        property.Type,
                        ManagedValueKind.Property,
                        false,
                        path,
                        new ManagedPropertyGetter(FileName(link.Module), link.Token, property.GetterToken, true)));
                }
            }

            return rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally
        {
            Release(chain);
        }
    }

    private static ManagedVariable StaticField(Link link, ManagedField field, ManagedValuePath at, ManagedTypes types, IntPtr frame)
        => Com.Borrow<ICorDebugClass, ManagedVariable>(link.Class, cls =>
        {
            if (cls.GetStaticFieldValue(field.Token, frame, out IntPtr value) < 0 || value == IntPtr.Zero)
            {
                // A static of a class the runtime has not initialised yet has no storage to read.
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

    /// <summary>
    /// Takes a static step: the named static field of the class in the step. Owned, or zero.
    /// </summary>
    internal static IntPtr FollowStatic(IntPtr value, ManagedStep step, IntPtr frame)
    {
        IntPtr held = Held(value);
        if (held == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            var chain = Chain(held);
            try
            {
                foreach (var link in chain)
                {
                    if (link.Token == step.Class
                        && string.Equals(FileName(link.Module), step.Module, StringComparison.OrdinalIgnoreCase))
                    {
                        return Com.Borrow<ICorDebugClass, IntPtr?>(link.Class, cls =>
                            cls.GetStaticFieldValue(step.Field, frame, out IntPtr found) == 0 ? found : null) ?? IntPtr.Zero;
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

    /// <summary>
    /// Writes a value back into the debuggee. Null when it was written, a sentence when it was not.
    ///
    /// Numbers, characters, bools and enums are written as bytes into the slot the runtime points at.
    /// A reference can be set to null here; setting one to a new string means making the string in
    /// the debuggee first, which is an evaluation and so is the session's to do.
    /// </summary>
    internal static string? Set(IntPtr value, string text, ManagedTypes types)
    {
        string wanted = text.Trim();
        int kind = KindOf(value);

        if (IsReference(kind))
        {
            if (!string.Equals(wanted, "null", StringComparison.OrdinalIgnoreCase))
            {
                return "only null can be written into a reference here";
            }

            int hr = Com.Borrow<ICorDebugReferenceValue, int?>(value, r => r.SetValue(0)) ?? -1;
            return hr < 0 ? $"the runtime refused it: 0x{hr:X8}" : null;
        }

        int size = Com.Borrow<ICorDebugValue, int?>(value, v => v.GetSize(out uint s) == 0 ? (int)s : null) ?? 0;
        if (size is <= 0 or > 8)
        {
            return "that is not a value this can write";
        }

        ulong bits;
        if (kind == ManagedValues.ElementValueType)
        {
            // An enum, written by member name or by number. Anything else of this shape is a struct,
            // and writing one means writing every field of it.
            var chain = Chain(value);
            try
            {
                if (chain.Count == 0 || types.EnumValue(chain[0].Module, chain[0].Token, wanted) is not { } member)
                {
                    return "that is not a member of this enum";
                }

                bits = member;
            }
            finally
            {
                Release(chain);
            }
        }
        else if (!Bits(kind, wanted, out bits))
        {
            return $"that is not a {ManagedValues.Name(kind)}";
        }

        IntPtr buffer = Marshal.AllocCoTaskMem(8);
        try
        {
            Marshal.WriteInt64(buffer, unchecked((long)bits));
            int hr = Com.Borrow<ICorDebugGenericValue, int?>(value, g => g.SetValue(buffer)) ?? -1;
            return hr < 0 ? $"the runtime refused it: 0x{hr:X8}" : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    /// <summary>Text as the bytes of its type, little-endian in the low end of a ulong.</summary>
    private static bool Bits(int kind, string text, out ulong bits)
    {
        bits = 0;
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        string digits = hex ? text[2..] : text;
        var culture = CultureInfo.InvariantCulture;
        var style = hex ? NumberStyles.HexNumber : NumberStyles.Integer;

        switch (kind)
        {
            case ManagedValues.ElementBoolean:
                if (bool.TryParse(text, out bool flag))
                {
                    bits = flag ? 1UL : 0UL;
                    return true;
                }

                return ulong.TryParse(digits, style, culture, out bits);

            case ManagedValues.ElementChar:
                string bare = text.Length >= 2 && text[0] == '\'' && text[^1] == '\'' ? text[1..^1] : text;
                if (bare.Length == 1)
                {
                    bits = bare[0];
                    return true;
                }

                return ulong.TryParse(digits, style, culture, out bits);

            case ManagedValues.ElementR4:
                if (float.TryParse(text, NumberStyles.Float, culture, out float single))
                {
                    bits = BitConverter.SingleToUInt32Bits(single);
                    return true;
                }

                return false;

            case ManagedValues.ElementR8:
                if (double.TryParse(text, NumberStyles.Float, culture, out double real))
                {
                    bits = BitConverter.DoubleToUInt64Bits(real);
                    return true;
                }

                return false;

            case ManagedValues.ElementI1 or ManagedValues.ElementI2 or ManagedValues.ElementI4 or ManagedValues.ElementI8:
                if (long.TryParse(digits, style, culture, out long signed))
                {
                    bits = unchecked((ulong)signed);
                    return true;
                }

                return false;

            default:
                return ulong.TryParse(digits, style, culture, out bits);
        }
    }
}
