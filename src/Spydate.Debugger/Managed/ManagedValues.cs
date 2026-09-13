using System.Globalization;
using System.Runtime.InteropServices;

namespace Spydate.Debugger.Managed;

/// <summary>One local or argument, as it reads.</summary>
public sealed record ManagedValue(int Index, string Kind, string Text)
{
    /// <summary><c>this</c>, or the parameter's name. Null for a local, which has none without symbols.</summary>
    public string? Name { get; init; }

    public override string ToString() => Name is null ? $"[{Index}] {Kind} = {Text}" : $"[{Index}] {Name}: {Kind} = {Text}";
}

/// <summary>
/// Reading values out of a stopped frame.
///
/// This is the thing the native debugger cannot do, and the reason for all the interop. A native
/// stop hands back registers and stack words, and turning those into "the path is
/// C:\Windows\notepad.exe" is the analyst's job, done by hand, using a calling convention they have
/// to know. Here the runtime knows what every slot is, so a stop can say it.
///
/// What it will not do is guess. A value the runtime declines to produce — optimised away, not yet
/// live at this offset — is reported as unavailable rather than as zero, because a confident zero is
/// indistinguishable from a real one and leads somewhere wrong.
/// </summary>
internal static class ManagedValues
{
    /// <summary>Longest string copied out of the debuggee. It is content from a program under study.</summary>
    private const int MaxString = 200;

    /// <summary>How many slots to ask for before giving up on a frame that keeps answering.</summary>
    private const int MaxSlots = 64;

    /// <summary>How many elements or fields to show inside a value before saying there are more.</summary>
    private const int MaxInside = 6;

    /// <summary>
    /// How far to follow references. One step from the slot, and one more for what it holds.
    ///
    /// An object graph has no natural end, and a debugger that walked one would spend a stop
    /// following a linked list into the runtime. Two levels is what a reader can take in from a
    /// line of text: this array holds these strings, this object has these fields.
    /// </summary>
    private const int MaxDepth = 2;

    /// <summary>Every local in a frame, in slot order, stopping at the first one that is not there.</summary>
    internal static List<ManagedValue> Locals(ICorDebugILFrame frame, ManagedTypes types)
        => Slots(frame, types, static (ICorDebugILFrame f, uint i, out IntPtr v) => f.GetLocalVariable(i, out v));

    /// <summary>Every argument in a frame. For an instance method, slot zero is <c>this</c>.</summary>
    internal static List<ManagedValue> Arguments(ICorDebugILFrame frame, ManagedTypes types)
        => Slots(frame, types, static (ICorDebugILFrame f, uint i, out IntPtr v) => f.GetArgument(i, out v));

    private delegate int Slot(ICorDebugILFrame frame, uint index, out IntPtr value);

    private static List<ManagedValue> Slots(ICorDebugILFrame frame, ManagedTypes types, Slot get)
    {
        var found = new List<ManagedValue>();
        for (uint i = 0; i < MaxSlots; i++)
        {
            int hr = get(frame, i, out IntPtr value);
            if (hr < 0)
            {
                // The end of the list and an unreadable slot look the same from here, so the list
                // stops. Anything else would report a made-up count.
                break;
            }

            if (value == IntPtr.Zero)
            {
                found.Add(new ManagedValue((int)i, "?", "not available"));
                continue;
            }

            found.Add(Read((int)i, value, types));
            Marshal.Release(value);
        }

        return found;
    }

    private static ManagedValue Read(int index, IntPtr value, ManagedTypes types)
    {
        int kind = Com.Borrow<ICorDebugValue, int?>(value, v => v.GetKind(out int k) == 0 ? k : null) ?? 0;
        var described = Describe(value, kind, types, MaxDepth);

        return new ManagedValue(index, described.Kind ?? Name(kind), described.Text ?? "not available");
    }

    /// <summary>What a value is and what it says, as far as it was followed.</summary>
    private readonly record struct Described(string? Kind, string? Text);

    /// <summary>
    /// What a value reads as, following a reference to what it holds.
    ///
    /// The following is the point. A slot that says <c>0x218104BF690</c> has told the reader that
    /// something is there and nothing else — it is the address of an object in a process that will
    /// have moved it by the next collection, and it cannot be looked up anywhere. The runtime knows
    /// the type, the array's length, the fields and their values, and this asks it.
    /// </summary>
    private static Described Describe(IntPtr value, int kind, ManagedTypes types, int depth)
    {
        if (kind is ElementString or ElementClass or ElementObject or ElementSzArray or ElementArray)
        {
            var reference = Com.Borrow<ICorDebugReferenceValue, Described?>(value, r =>
            {
                if (r.IsNull(out int isNull) < 0)
                {
                    return null;
                }

                if (isNull != 0)
                {
                    return new Described(null, "null");
                }

                if (depth <= 0)
                {
                    // Out of budget rather than out of information. The address is what is left,
                    // and it is at least an identity that two slots can be compared by.
                    return new Described(null, r.GetValue(out ulong at) == 0 ? $"0x{at:X}" : "an object");
                }

                if (r.Dereference(out IntPtr pointed) < 0 || pointed == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return Held(pointed, types, depth);
                }
                finally
                {
                    Marshal.Release(pointed);
                }
            });

            return reference ?? new Described(null, null);
        }

        // A struct is not behind a reference: it is the value, and its fields are read from it.
        if (kind == ElementValueType)
        {
            return Object(value, types, depth) is { Kind: not null } inside
                ? inside
                : new Described(null, Primitive(value, kind));
        }

        return new Described(null, Primitive(value, kind));
    }

    /// <summary>What a reference pointed at: an array, a string, a boxed value, or an object.</summary>
    private static Described Held(IntPtr pointed, ManagedTypes types, int depth)
    {
        if (Array(pointed, types, depth) is { Text: not null } array)
        {
            return array;
        }

        if (Com.Borrow<ICorDebugStringValue, bool>(pointed, _ => true))
        {
            return new Described("string", Text(pointed));
        }

        // A boxed value is a wrapper around the thing worth reading.
        var unboxed = Com.Borrow<ICorDebugBoxValue, Described?>(pointed, box =>
        {
            if (box.GetObject(out IntPtr inside) < 0 || inside == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Object(inside, types, depth);
            }
            finally
            {
                Marshal.Release(inside);
            }
        });

        if (unboxed is { Kind: not null } boxed)
        {
            return boxed;
        }

        return Object(pointed, types, depth);
    }

    /// <summary>An array, as its length and the first few of its elements.</summary>
    private static Described Array(IntPtr value, ManagedTypes types, int depth)
        => Com.Borrow<ICorDebugArrayValue, Described>(value, array =>
        {
            if (array.GetCount(out uint count) < 0)
            {
                return default;
            }

            int element = array.GetElementType(out int type) == 0 ? type : 0;
            string kind = $"{Name(element)}[]";

            if (count == 0)
            {
                return new Described(kind, "empty");
            }

            var inside = new List<string>();
            for (uint i = 0; i < Math.Min(count, MaxInside); i++)
            {
                inside.Add(Element(array, i, types, depth - 1));
            }

            string more = count > MaxInside ? ", …" : string.Empty;
            return new Described(kind, $"[{count}] {{ {string.Join(", ", inside)}{more} }}");
        });

    private static string Element(ICorDebugArrayValue array, uint index, ManagedTypes types, int depth)
    {
        if (array.GetElementAtPosition(index, out IntPtr element) < 0 || element == IntPtr.Zero)
        {
            return "?";
        }

        try
        {
            int kind = Com.Borrow<ICorDebugValue, int?>(element, v => v.GetKind(out int k) == 0 ? k : null) ?? 0;
            return Describe(element, kind, types, depth).Text ?? "?";
        }
        finally
        {
            Marshal.Release(element);
        }
    }

    /// <summary>An object or struct, as its type name and the first few of its fields.</summary>
    private static Described Object(IntPtr value, ManagedTypes types, int depth)
        => Com.Borrow<ICorDebugObjectValue, Described>(value, obj =>
        {
            if (obj.GetClass(out IntPtr held) < 0 || held == IntPtr.Zero)
            {
                return default;
            }

            try
            {
                return Com.Borrow<ICorDebugClass, Described>(held, type =>
                {
                    if (type.GetToken(out uint token) < 0)
                    {
                        return default;
                    }

                    string? module = type.GetModule(out IntPtr owner) == 0
                        ? Com.Owned<ICorDebugModule, string>(owner, m => Com.NameOf(m))
                        : null;

                    string name = types.Name(module, token) ?? "object";

                    // An enum reads as its member, the way the source wrote it — Angry, not
                    // { value__ = 2 }, which is what an enum is underneath and nobody wrote.
                    if (types.IsEnum(module, token)
                        && types.Fields(module, token, int.MaxValue).FirstOrDefault(f => f.Name == "value__") is { Token: not 0 } underlying
                        && obj.GetFieldValue(held, underlying.Token, out IntPtr raw) == 0
                        && raw != IntPtr.Zero)
                    {
                        try
                        {
                            ulong bits = ManagedVariables.Raw(raw, out _) ?? 0;
                            return new Described(name, types.EnumText(module, token, bits));
                        }
                        finally
                        {
                            Marshal.Release(raw);
                        }
                    }
                    var fields = depth > 0 ? types.Fields(module, token, MaxInside) : System.Array.Empty<ManagedField>();
                    if (fields.Count == 0)
                    {
                        return new Described(name, $"{{{name}}}");
                    }

                    var inside = new List<string>();
                    foreach (var field in fields)
                    {
                        inside.Add($"{field.Name} = {Field(obj, held, field.Token, types, depth - 1)}");
                    }

                    return new Described(name, $"{{ {string.Join(", ", inside)} }}");
                });
            }
            finally
            {
                Marshal.Release(held);
            }
        });

    private static string Field(ICorDebugObjectValue obj, IntPtr type, uint token, ManagedTypes types, int depth)
    {
        if (obj.GetFieldValue(type, token, out IntPtr value) < 0 || value == IntPtr.Zero)
        {
            return "?";
        }

        try
        {
            int kind = Com.Borrow<ICorDebugValue, int?>(value, v => v.GetKind(out int k) == 0 ? k : null) ?? 0;
            return Describe(value, kind, types, depth).Text ?? "?";
        }
        finally
        {
            Marshal.Release(value);
        }
    }

    /// <summary>A number or a character, copied out of the debuggee and formatted by its own type.</summary>
    internal static string? Primitive(IntPtr value, int kind)
    {
        int size = Com.Borrow<ICorDebugValue, int?>(value, v => v.GetSize(out uint s) == 0 ? (int)s : null) ?? 0;
        if (size is <= 0 or > 8)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocCoTaskMem(size);
        try
        {
            bool ok = Com.Borrow<ICorDebugGenericValue, bool?>(value, generic => generic.GetValue(buffer) == 0) ?? false;
            if (!ok)
            {
                return null;
            }

            Span<byte> bytes = stackalloc byte[8];
            for (int i = 0; i < size; i++)
            {
                bytes[i] = Marshal.ReadByte(buffer, i);
            }

            return Format(bytes, kind, size);
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    private static string Format(ReadOnlySpan<byte> bytes, int kind, int size) => kind switch
    {
        ElementBoolean => bytes[0] != 0 ? "true" : "false",
        ElementChar => $"'{(char)BitConverter.ToUInt16(bytes)}'",
        ElementI1 => ((sbyte)bytes[0]).ToString(CultureInfo.InvariantCulture),
        ElementU1 => bytes[0].ToString(CultureInfo.InvariantCulture),
        ElementI2 => BitConverter.ToInt16(bytes).ToString(CultureInfo.InvariantCulture),
        ElementU2 => BitConverter.ToUInt16(bytes).ToString(CultureInfo.InvariantCulture),
        ElementI4 => BitConverter.ToInt32(bytes).ToString(CultureInfo.InvariantCulture),
        ElementU4 => BitConverter.ToUInt32(bytes).ToString(CultureInfo.InvariantCulture),
        ElementI8 => BitConverter.ToInt64(bytes).ToString(CultureInfo.InvariantCulture),
        ElementU8 => BitConverter.ToUInt64(bytes).ToString(CultureInfo.InvariantCulture),
        ElementR4 => BitConverter.ToSingle(bytes).ToString(CultureInfo.InvariantCulture),
        ElementR8 => BitConverter.ToDouble(bytes).ToString(CultureInfo.InvariantCulture),
        ElementI or ElementU or ElementPtr or ElementFnPtr => $"0x{BitConverter.ToUInt64(bytes):X}",

        // A struct, which is its bytes and nothing this can name. Saying so beats printing the
        // first four of them as though they were a number.
        _ => $"{size} bytes",
    };

    /// <summary>The text of a string on the debuggee's heap, clipped and escaped.</summary>
    internal static string? Text(IntPtr pointed, int limit = MaxString)
    {
        return Com.Borrow<ICorDebugStringValue, string>(pointed, text =>
            {
                if (text.GetLength(out uint length) < 0)
                {
                    return null;
                }

                uint wanted = (uint)Math.Min(length, (uint)limit) + 1;
                IntPtr buffer = Marshal.AllocCoTaskMem((int)wanted * sizeof(char));
                try
                {
                    if (text.GetString(wanted, out uint written, buffer) < 0)
                    {
                        return null;
                    }

                    string read = Marshal.PtrToStringUni(buffer, (int)Math.Min(written, wanted)) ?? string.Empty;
                    read = read.TrimEnd('\0');
                    string clipped = length > limit ? read + "..." : read;
                    return $"\"{clipped.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
                }
                finally
                {
                    Marshal.FreeCoTaskMem(buffer);
                }
            });
    }

    internal static string Name(int kind) => kind switch
    {
        ElementBoolean => "bool",
        ElementChar => "char",
        ElementI1 => "sbyte",
        ElementU1 => "byte",
        ElementI2 => "short",
        ElementU2 => "ushort",
        ElementI4 => "int",
        ElementU4 => "uint",
        ElementI8 => "long",
        ElementU8 => "ulong",
        ElementR4 => "float",
        ElementR8 => "double",
        ElementString => "string",
        ElementClass or ElementObject => "object",
        ElementSzArray or ElementArray => "array",
        ElementValueType => "struct",
        ElementPtr or ElementFnPtr => "pointer",
        ElementI => "nint",
        ElementU => "nuint",
        _ => $"kind {kind}",
    };

    // CorElementType, from the metadata specification. Only the ones that can be a slot's type.
    internal const int ElementBoolean = 0x02;
    internal const int ElementChar = 0x03;
    internal const int ElementI1 = 0x04;
    internal const int ElementU1 = 0x05;
    internal const int ElementI2 = 0x06;
    internal const int ElementU2 = 0x07;
    internal const int ElementI4 = 0x08;
    internal const int ElementU4 = 0x09;
    internal const int ElementI8 = 0x0A;
    internal const int ElementU8 = 0x0B;
    internal const int ElementR4 = 0x0C;
    internal const int ElementR8 = 0x0D;
    internal const int ElementString = 0x0E;
    internal const int ElementPtr = 0x0F;
    internal const int ElementValueType = 0x11;
    internal const int ElementClass = 0x12;
    internal const int ElementArray = 0x14;
    internal const int ElementI = 0x18;
    internal const int ElementU = 0x19;
    internal const int ElementFnPtr = 0x1B;
    internal const int ElementObject = 0x1C;
    internal const int ElementSzArray = 0x1D;
}
