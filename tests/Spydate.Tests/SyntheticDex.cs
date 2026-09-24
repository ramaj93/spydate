using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Spydate.Tests;

/// <summary>
/// Builds DEX files by hand: the id tables, classes with fields and methods, code with try ranges and debug
/// tables, annotations, and the header's checksum and signature. Like <see cref="SyntheticClass"/>, it says
/// exactly what a test depends on and needs no Android toolchain. Indices are handed out in the order things are
/// first named, so a test can put them into instructions before the file is built.
/// </summary>
internal sealed class SyntheticDex
{
    private readonly List<string> _strings = [];
    private readonly Dictionary<string, int> _stringIndex = new(StringComparer.Ordinal);
    private readonly List<int> _types = [];
    private readonly Dictionary<string, int> _typeIndex = new(StringComparer.Ordinal);
    private readonly List<(int Shorty, int Return, int[] Parameters)> _protos = [];
    private readonly Dictionary<string, int> _protoIndex = new(StringComparer.Ordinal);
    private readonly List<(int Owner, int Type, int Name)> _fields = [];
    private readonly Dictionary<string, int> _fieldIndex = new(StringComparer.Ordinal);
    private readonly List<(int Owner, int Proto, int Name)> _methods = [];
    private readonly Dictionary<string, int> _methodIndex = new(StringComparer.Ordinal);
    private readonly List<ClassBuilder> _classes = [];

    public string Version { get; set; } = "035";

    public int String(string text)
    {
        if (!_stringIndex.TryGetValue(text, out int index))
        {
            index = _strings.Count;
            _strings.Add(text);
            _stringIndex[text] = index;
        }

        return index;
    }

    public int Type(string descriptor)
    {
        if (!_typeIndex.TryGetValue(descriptor, out int index))
        {
            index = _types.Count;
            _types.Add(String(descriptor));
            _typeIndex[descriptor] = index;
        }

        return index;
    }

    public int Proto(string returns, params string[] parameters)
    {
        string key = $"({string.Concat(parameters)}){returns}";
        if (!_protoIndex.TryGetValue(key, out int index))
        {
            string shorty = string.Concat(new[] { returns }.Concat(parameters).Select(t => t[0] is 'L' or '[' ? 'L' : t[0]));
            index = _protos.Count;
            _protos.Add((String(shorty), Type(returns), parameters.Select(Type).ToArray()));
            _protoIndex[key] = index;
        }

        return index;
    }

    public int Field(string owner, string name, string type)
    {
        string key = $"{owner}->{name}:{type}";
        if (!_fieldIndex.TryGetValue(key, out int index))
        {
            index = _fields.Count;
            _fields.Add((Type(owner), Type(type), String(name)));
            _fieldIndex[key] = index;
        }

        return index;
    }

    public int Method(string owner, string name, string returns, params string[] parameters)
    {
        string key = $"{owner}->{name}({string.Concat(parameters)}){returns}";
        if (!_methodIndex.TryGetValue(key, out int index))
        {
            index = _methods.Count;
            _methods.Add((Type(owner), Proto(returns, parameters), String(name)));
            _methodIndex[key] = index;
        }

        return index;
    }

    public ClassBuilder Class(string descriptor, uint access = 0x0001, string? super = "Ljava/lang/Object;", string? source = null, params string[] interfaces)
    {
        var builder = new ClassBuilder(this, Type(descriptor), access, super is null ? -1 : Type(super), source is null ? -1 : String(source), interfaces.Select(Type).ToArray());
        _classes.Add(builder);
        return builder;
    }

    /// <summary>An encoded value: a number, string, type, boolean, null, array or nested annotation.</summary>
    public sealed record Value(int Type, long Bits = 0, int Arg = -1, Value[]? Items = null, Annotation? Nested = null)
    {
        public static Value Int(int v) => new(0x04, v);

        public static Value Str(SyntheticDex dex, string s) => new(0x17, dex.String(s));

        public static Value TypeOf(SyntheticDex dex, string descriptor) => new(0x18, dex.Type(descriptor));

        public static Value Bool(bool v) => new(0x1F, Arg: v ? 1 : 0);

        public static Value Null() => new(0x1E);

        public static Value Array(params Value[] items) => new(0x1C, Items: items);
    }

    public sealed record Annotation(int Visibility, int Type, (int Name, Value Value)[] Elements);

    public Annotation Annotate(string type, int visibility = 2, params (string Name, Value Value)[] elements)
        => new(visibility, Type(type), elements.Select(e => (String(e.Name), e.Value)).ToArray());

    public sealed record Code(int Registers, int Ins, int Outs, ushort[] Insns)
    {
        public (int Start, int Length, (string Type, int Address)[] Handlers, int? CatchAll)[] Tries { get; init; } = [];

        public int LineStart { get; init; } = 1;

        public string?[] ParameterNames { get; init; } = [];

        /// <summary>Debug opcodes after the parameter names, before the end: see the DEX debug state machine.</summary>
        public byte[] DebugProgram { get; init; } = [];

        public bool HasDebug { get; init; }
    }

    public sealed class ClassBuilder(SyntheticDex dex, int type, uint access, int super, int source, int[] interfaces)
    {
        internal int Type { get; } = type;

        internal uint Access { get; } = access;

        internal int Super { get; } = super;

        internal int Source { get; } = source;

        internal int[] Interfaces { get; } = interfaces;

        internal List<(int Field, uint Access, Value? Initial, Annotation[] Annotations)> StaticFields { get; } = [];

        internal List<(int Field, uint Access, Annotation[] Annotations)> InstanceFields { get; } = [];

        internal List<(int Method, uint Access, Code? Code, Annotation[] Annotations)> Direct { get; } = [];

        internal List<(int Method, uint Access, Code? Code, Annotation[] Annotations)> Virtual { get; } = [];

        internal List<Annotation> Annotations { get; } = [];

        private string Descriptor => dex._strings[dex._types[Type]];

        public ClassBuilder StaticField(string name, string fieldType, uint flags = 0x0008, Value? initial = null, params Annotation[] annotations)
        {
            StaticFields.Add((dex.Field(Descriptor, name, fieldType), flags, initial, annotations));
            return this;
        }

        public ClassBuilder InstanceField(string name, string fieldType, uint flags = 0x0002, params Annotation[] annotations)
        {
            InstanceFields.Add((dex.Field(Descriptor, name, fieldType), flags, annotations));
            return this;
        }

        /// <summary>A method; static, private and constructors are direct, the rest virtual, as DEX lists them.</summary>
        public ClassBuilder Method(string name, string returns, string[] parameters, uint flags, Code? code, params Annotation[] annotations)
        {
            int index = dex.Method(Descriptor, name, returns, parameters);
            bool direct = (flags & (0x0008 | 0x0002 | 0x10000)) != 0 || name is "<init>" or "<clinit>";
            (direct ? Direct : Virtual).Add((index, flags, code, annotations));
            return this;
        }

        public ClassBuilder Annotate(Annotation annotation)
        {
            Annotations.Add(annotation);
            return this;
        }
    }

    // --- layout -------------------------------------------------------------------------------------

    public byte[] Build()
    {
        // Every index is fixed before layout — the names code refers to included — and class data sorts members by
        // index as DEX requires.
        foreach (var code in _classes.SelectMany(k => k.Direct.Concat(k.Virtual)).Select(m => m.Code).OfType<Code>())
        {
            foreach (var (type, _) in code.Tries.SelectMany(t => t.Handlers))
            {
                Type(type);
            }

            foreach (string? name in code.ParameterNames.OfType<string>())
            {
                String(name);
            }
        }

        var o = new List<byte>(new byte[0x70]);
        int stringIdsOff = o.Count;
        o.AddRange(new byte[_strings.Count * 4]);
        int typeIdsOff = o.Count;
        foreach (int s in _types)
        {
            U4(o, (uint)s);
        }

        int protoIdsOff = o.Count;
        o.AddRange(new byte[_protos.Count * 12]);
        int fieldIdsOff = o.Count;
        foreach (var f in _fields)
        {
            U2(o, f.Owner);
            U2(o, f.Type);
            U4(o, (uint)f.Name);
        }

        int methodIdsOff = o.Count;
        foreach (var m in _methods)
        {
            U2(o, m.Owner);
            U2(o, m.Proto);
            U4(o, (uint)m.Name);
        }

        int classDefsOff = o.Count;
        o.AddRange(new byte[_classes.Count * 32]);
        int dataOff = o.Count;

        // Strings.
        for (int i = 0; i < _strings.Count; i++)
        {
            Set4(o, stringIdsOff + (i * 4), (uint)o.Count);
            Uleb(o, (uint)_strings[i].Length);
            o.AddRange(Encoding.UTF8.GetBytes(_strings[i]));
            o.Add(0);
        }

        // Prototypes and their parameter lists.
        for (int i = 0; i < _protos.Count; i++)
        {
            var p = _protos[i];
            uint parameters = 0;
            if (p.Parameters.Length > 0)
            {
                Align(o);
                parameters = (uint)o.Count;
                U4(o, (uint)p.Parameters.Length);
                foreach (int t in p.Parameters)
                {
                    U2(o, t);
                }
            }

            Set4(o, protoIdsOff + (i * 12), (uint)p.Shorty);
            Set4(o, protoIdsOff + (i * 12) + 4, (uint)p.Return);
            Set4(o, protoIdsOff + (i * 12) + 8, parameters);
        }

        for (int c = 0; c < _classes.Count; c++)
        {
            var k = _classes[c];
            int at = classDefsOff + (c * 32);
            uint interfacesOff = 0;
            if (k.Interfaces.Length > 0)
            {
                Align(o);
                interfacesOff = (uint)o.Count;
                U4(o, (uint)k.Interfaces.Length);
                foreach (int t in k.Interfaces)
                {
                    U2(o, t);
                }
            }

            // Code items first, so class data can point at them.
            var codeOffsets = new Dictionary<int, uint>();
            foreach (var m in k.Direct.Concat(k.Virtual).Where(m => m.Code is not null))
            {
                codeOffsets[m.Method] = WriteCode(o, m.Code!);
            }

            uint annotationsOff = WriteDirectory(o, k);
            uint staticValuesOff = 0;
            var statics = k.StaticFields.OrderBy(f => f.Field).ToList();
            if (statics.Any(f => f.Initial is not null))
            {
                staticValuesOff = (uint)o.Count;
                int last = statics.FindLastIndex(f => f.Initial is not null);
                Uleb(o, (uint)(last + 1));
                foreach (var f in statics.Take(last + 1))
                {
                    WriteValue(o, f.Initial ?? Value.Null());
                }
            }

            uint classDataOff = (uint)o.Count;
            Uleb(o, (uint)k.StaticFields.Count);
            Uleb(o, (uint)k.InstanceFields.Count);
            Uleb(o, (uint)k.Direct.Count);
            Uleb(o, (uint)k.Virtual.Count);
            WriteMembers(o, statics.Select(f => (f.Field, f.Access, 0u)), withCode: false);
            WriteMembers(o, k.InstanceFields.OrderBy(f => f.Field).Select(f => (f.Field, f.Access, 0u)), withCode: false);
            WriteMembers(o, k.Direct.OrderBy(m => m.Method).Select(m => (m.Method, m.Access, codeOffsets.GetValueOrDefault(m.Method))), withCode: true);
            WriteMembers(o, k.Virtual.OrderBy(m => m.Method).Select(m => (m.Method, m.Access, codeOffsets.GetValueOrDefault(m.Method))), withCode: true);

            Set4(o, at, (uint)k.Type);
            Set4(o, at + 4, k.Access);
            Set4(o, at + 8, k.Super < 0 ? 0xFFFFFFFF : (uint)k.Super);
            Set4(o, at + 12, interfacesOff);
            Set4(o, at + 16, k.Source < 0 ? 0xFFFFFFFF : (uint)k.Source);
            Set4(o, at + 20, annotationsOff);
            Set4(o, at + 24, classDataOff);
            Set4(o, at + 28, staticValuesOff);
        }

        Align(o);
        int mapOff = o.Count;
        U4(o, 1);
        U2(o, 0x0000);
        U2(o, 0);
        U4(o, 1);
        U4(o, 0);

        byte[] file = [.. o];
        Encoding.ASCII.GetBytes($"dex\n{Version}\0").CopyTo(file, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x20), (uint)file.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x24), 0x70);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x28), 0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x34), (uint)mapOff);
        uint[] header = [(uint)_strings.Count, (uint)stringIdsOff, (uint)_types.Count, (uint)typeIdsOff, (uint)_protos.Count, (uint)protoIdsOff,
            (uint)_fields.Count, (uint)fieldIdsOff, (uint)_methods.Count, (uint)methodIdsOff, (uint)_classes.Count, (uint)classDefsOff,
            (uint)(file.Length - dataOff), (uint)dataOff];
        for (int i = 0; i < header.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x38 + (i * 4)), header[i]);
        }

        SHA1.HashData(file.AsSpan(0x20)).CopyTo(file, 0x0C);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x08), Adler32(file.AsSpan(0x0C)));
        return file;
    }

    private uint WriteCode(List<byte> o, Code code)
    {
        uint debugOff = 0;
        if (code.HasDebug)
        {
            debugOff = (uint)o.Count;
            Uleb(o, (uint)code.LineStart);
            Uleb(o, (uint)code.ParameterNames.Length);
            foreach (string? name in code.ParameterNames)
            {
                Uleb(o, name is null ? 0 : (uint)String(name) + 1);
            }

            o.AddRange(code.DebugProgram);
            o.Add(0x00);
        }

        Align(o);
        uint at = (uint)o.Count;
        U2(o, code.Registers);
        U2(o, code.Ins);
        U2(o, code.Outs);
        U2(o, code.Tries.Length);
        U4(o, debugOff);
        U4(o, (uint)code.Insns.Length);
        foreach (ushort unit in code.Insns)
        {
            U2(o, unit);
        }

        if (code.Tries.Length > 0)
        {
            if (code.Insns.Length % 2 == 1)
            {
                U2(o, 0);
            }

            var handlers = new List<byte>();
            Uleb(handlers, (uint)code.Tries.Length);
            var handlerOffsets = new List<int>();
            foreach (var t in code.Tries)
            {
                handlerOffsets.Add(handlers.Count);
                Sleb(handlers, t.CatchAll is null ? t.Handlers.Length : -t.Handlers.Length);
                foreach (var (type, address) in t.Handlers)
                {
                    Uleb(handlers, (uint)Type(type));
                    Uleb(handlers, (uint)address);
                }

                if (t.CatchAll is { } all)
                {
                    Uleb(handlers, (uint)all);
                }
            }

            for (int i = 0; i < code.Tries.Length; i++)
            {
                U4(o, (uint)code.Tries[i].Start);
                U2(o, code.Tries[i].Length);
                U2(o, handlerOffsets[i]);
            }

            o.AddRange(handlers);
        }

        return at;
    }

    private uint WriteDirectory(List<byte> o, ClassBuilder k)
    {
        var fields = k.StaticFields.Select(f => (f.Field, f.Annotations)).Concat(k.InstanceFields.Select(f => (f.Field, f.Annotations))).Where(f => f.Annotations.Length > 0).OrderBy(f => f.Field).ToList();
        var methods = k.Direct.Concat(k.Virtual).Where(m => m.Annotations.Length > 0).OrderBy(m => m.Method).ToList();
        if (k.Annotations.Count == 0 && fields.Count == 0 && methods.Count == 0)
        {
            return 0;
        }

        uint classSet = k.Annotations.Count > 0 ? WriteSet(o, k.Annotations) : 0;
        var fieldSets = fields.Select(f => ((uint)f.Field, WriteSet(o, f.Annotations))).ToList();
        var methodSets = methods.Select(m => ((uint)m.Method, WriteSet(o, m.Annotations))).ToList();
        Align(o);
        uint at = (uint)o.Count;
        U4(o, classSet);
        U4(o, (uint)fieldSets.Count);
        U4(o, (uint)methodSets.Count);
        U4(o, 0);
        foreach (var (index, set) in fieldSets.Concat(methodSets))
        {
            U4(o, index);
            U4(o, set);
        }

        return at;
    }

    private uint WriteSet(List<byte> o, IReadOnlyList<Annotation> annotations)
    {
        var items = new List<uint>();
        foreach (var a in annotations)
        {
            items.Add((uint)o.Count);
            o.Add((byte)a.Visibility);
            WriteAnnotation(o, a);
        }

        Align(o);
        uint at = (uint)o.Count;
        U4(o, (uint)items.Count);
        foreach (uint item in items)
        {
            U4(o, item);
        }

        return at;
    }

    private static void WriteAnnotation(List<byte> o, Annotation a)
    {
        Uleb(o, (uint)a.Type);
        Uleb(o, (uint)a.Elements.Length);
        foreach (var (name, value) in a.Elements)
        {
            Uleb(o, (uint)name);
            WriteValue(o, value);
        }
    }

    private static void WriteValue(List<byte> o, Value v)
    {
        switch (v.Type)
        {
            case 0x1C:
                o.Add(0x1C);
                Uleb(o, (uint)v.Items!.Length);
                foreach (var item in v.Items)
                {
                    WriteValue(o, item);
                }

                return;
            case 0x1D:
                o.Add(0x1D);
                WriteAnnotation(o, v.Nested!);
                return;
            case 0x1E:
                o.Add(0x1E);
                return;
            case 0x1F:
                o.Add((byte)(0x1F | (v.Arg << 5)));
                return;
            default:
                // Always four bytes (value_arg 3), which every integral and index form allows.
                o.Add((byte)(v.Type | (3 << 5)));
                U4(o, (uint)v.Bits);
                return;
        }
    }

    private static void WriteMembers(List<byte> o, IEnumerable<(int Index, uint Access, uint Code)> members, bool withCode)
    {
        int previous = 0;
        foreach (var (index, access, code) in members)
        {
            Uleb(o, (uint)(index - previous));
            previous = index;
            Uleb(o, access);
            if (withCode)
            {
                Uleb(o, code);
            }
        }
    }

    private static void Align(List<byte> o)
    {
        while (o.Count % 4 != 0)
        {
            o.Add(0);
        }
    }

    private static void U2(List<byte> o, int v)
    {
        o.Add((byte)v);
        o.Add((byte)(v >> 8));
    }

    private static void U4(List<byte> o, uint v)
    {
        o.Add((byte)v);
        o.Add((byte)(v >> 8));
        o.Add((byte)(v >> 16));
        o.Add((byte)(v >> 24));
    }

    private static void Set4(List<byte> o, int at, uint v)
    {
        o[at] = (byte)v;
        o[at + 1] = (byte)(v >> 8);
        o[at + 2] = (byte)(v >> 16);
        o[at + 3] = (byte)(v >> 24);
    }

    internal static void Uleb(List<byte> o, uint v)
    {
        do
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            o.Add(v != 0 ? (byte)(b | 0x80) : b);
        }
        while (v != 0);
    }

    internal static void Sleb(List<byte> o, int v)
    {
        bool more = true;
        while (more)
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            more = !((v == 0 && (b & 0x40) == 0) || (v == -1 && (b & 0x40) != 0));
            o.Add(more ? (byte)(b | 0x80) : b);
        }
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (byte x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }
}
