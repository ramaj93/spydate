using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Spydate.Tests;

/// <summary>
/// Builds Java class files by hand: a constant pool, fields, methods with real bytecode and debug tables, and the
/// class attributes that say how it nests. There is no JDK on a test machine to compile one, and a hand-built
/// class says exactly what a test depends on.
/// </summary>
internal sealed class SyntheticClass
{
    private readonly List<byte[]> _pool = [];
    private readonly Dictionary<string, ushort> _interned = new(StringComparer.Ordinal);
    private readonly List<byte[]> _fields = [];
    private readonly List<byte[]> _methods = [];
    private readonly List<byte[]> _attributes = [];
    private readonly List<ushort> _interfaces = [];
    private readonly List<(ushort Inner, ushort Outer, ushort Name, ushort Access)> _inner = [];
    private readonly List<(ushort Handle, ushort[] Args)> _bootstraps = [];

    public SyntheticClass(string name, string? super = "java/lang/Object", ushort access = 0x0021, ushort major = 61)
    {
        Name = name;
        Access = access;
        Major = major;
        This = Class(name);
        Super = super is null ? (ushort)0 : Class(super);
    }

    public string Name { get; }

    public ushort Access { get; }

    public ushort Major { get; }

    public ushort This { get; }

    public ushort Super { get; }

    // --- the constant pool -------------------------------------------------

    public ushort Utf8(string text) => Intern($"U:{text}", () =>
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return [1, .. U2(bytes.Length), .. bytes];
    });

    public ushort Class(string name) => Intern($"C:{name}", () => [7, .. U2(Utf8(name))]);

    public ushort String(string text) => Intern($"S:{text}", () => [8, .. U2(Utf8(text))]);

    public ushort Integer(int value) => Intern($"I:{value}", () => [3, .. U4((uint)value)]);

    public ushort Long(long value)
    {
        ushort index = Intern($"J:{value}", () => [5, .. U4((uint)(value >> 32)), .. U4((uint)value)]);
        return index;
    }

    public ushort NameAndType(string name, string descriptor) => Intern($"N:{name}:{descriptor}", () => [12, .. U2(Utf8(name)), .. U2(Utf8(descriptor))]);

    public ushort Method(string owner, string name, string descriptor, bool isInterface = false)
        => Intern($"M:{owner}.{name}{descriptor}", () => [(byte)(isInterface ? 11 : 10), .. U2(Class(owner)), .. U2(NameAndType(name, descriptor))]);

    public ushort Field(string owner, string name, string descriptor)
        => Intern($"F:{owner}.{name}:{descriptor}", () => [9, .. U2(Class(owner)), .. U2(NameAndType(name, descriptor))]);

    /// <summary>A MethodHandle of kind 6 (invokestatic), as a lambda's bootstrap argument names its body.</summary>
    public ushort StaticHandle(string owner, string name, string descriptor)
        => Intern($"H:{owner}.{name}{descriptor}", () => [15, 6, .. U2(Method(owner, name, descriptor))]);

    public ushort MethodType(string descriptor) => Intern($"T:{descriptor}", () => [16, .. U2(Utf8(descriptor))]);

    /// <summary>An InvokeDynamic whose bootstrap method is <c>LambdaMetafactory.metafactory</c>, with the lambda body as its second argument.</summary>
    public ushort Lambda(string name, string descriptor, string bodyOwner, string bodyName, string bodyDescriptor)
    {
        ushort bootstrap = (ushort)_bootstraps.Count;
        _bootstraps.Add((
            StaticHandle("java/lang/invoke/LambdaMetafactory", "metafactory", "(Ljava/lang/invoke/MethodHandles$Lookup;Ljava/lang/String;Ljava/lang/invoke/MethodType;Ljava/lang/invoke/MethodType;Ljava/lang/invoke/MethodHandle;Ljava/lang/invoke/MethodType;)Ljava/lang/invoke/CallSite;"),
            [MethodType("()V"), StaticHandle(bodyOwner, bodyName, bodyDescriptor), MethodType("()V")]));
        return Intern($"D:{name}{descriptor}#{bootstrap}", () => [18, .. U2(bootstrap), .. U2(NameAndType(name, descriptor))]);
    }

    // --- members and attributes ---------------------------------------------

    public SyntheticClass Implements(string name)
    {
        _interfaces.Add(Class(name));
        return this;
    }

    public SyntheticClass AddField(ushort access, string name, string descriptor, ushort constantValue = 0, string? signature = null)
    {
        var attributes = new List<byte[]>();
        if (constantValue != 0)
        {
            attributes.Add(Attribute("ConstantValue", U2(constantValue)));
        }

        if (signature is not null)
        {
            attributes.Add(Attribute("Signature", U2(Utf8(signature))));
        }

        _fields.Add([.. U2(access), .. U2(Utf8(name)), .. U2(Utf8(descriptor)), .. U2(attributes.Count), .. attributes.SelectMany(a => a)]);
        return this;
    }

    public SyntheticClass AddMethod(
        ushort access,
        string name,
        string descriptor,
        byte[]? code,
        int maxStack = 4,
        int maxLocals = 4,
        (int Start, int End, int Handler, string? Type)[]? handlers = null,
        (int Pc, int Line)[]? lines = null,
        (int Start, int Length, string Name, string Descriptor, int Slot)[]? locals = null,
        string? signature = null,
        string[]? exceptions = null)
    {
        var attributes = new List<byte[]>();
        if (code is not null)
        {
            var codeAttributes = new List<byte[]>();
            if (lines is { Length: > 0 })
            {
                codeAttributes.Add(Attribute("LineNumberTable", [.. U2(lines.Length), .. lines.SelectMany(l => (byte[])[.. U2(l.Pc), .. U2(l.Line)])]));
            }

            if (locals is { Length: > 0 })
            {
                codeAttributes.Add(Attribute("LocalVariableTable", [.. U2(locals.Length), .. locals.SelectMany(l => (byte[])[.. U2(l.Start), .. U2(l.Length), .. U2(Utf8(l.Name)), .. U2(Utf8(l.Descriptor)), .. U2(l.Slot)])]));
            }

            handlers ??= [];
            byte[] body =
            [
                .. U2(maxStack), .. U2(maxLocals), .. U4((uint)code.Length), .. code,
                .. U2(handlers.Length), .. handlers.SelectMany(h => (byte[])[.. U2(h.Start), .. U2(h.End), .. U2(h.Handler), .. U2(h.Type is null ? 0 : Class(h.Type))]),
                .. U2(codeAttributes.Count), .. codeAttributes.SelectMany(a => a),
            ];
            attributes.Add(Attribute("Code", body));
        }

        if (signature is not null)
        {
            attributes.Add(Attribute("Signature", U2(Utf8(signature))));
        }

        if (exceptions is { Length: > 0 })
        {
            attributes.Add(Attribute("Exceptions", [.. U2(exceptions.Length), .. exceptions.SelectMany(e => U2(Class(e)))]));
        }

        _methods.Add([.. U2(access), .. U2(Utf8(name)), .. U2(Utf8(descriptor)), .. U2(attributes.Count), .. attributes.SelectMany(a => a)]);
        return this;
    }

    public SyntheticClass SourceFile(string name)
    {
        _attributes.Add(Attribute("SourceFile", U2(Utf8(name))));
        return this;
    }

    /// <summary>A row of InnerClasses: a member class has an outer class and a simple name; an anonymous one neither.</summary>
    public SyntheticClass InnerClass(string inner, string? outer, string? simpleName, ushort access = 0x0009)
    {
        _inner.Add((Class(inner), outer is null ? (ushort)0 : Class(outer), simpleName is null ? (ushort)0 : Utf8(simpleName), access));
        return this;
    }

    public SyntheticClass EnclosingMethod(string owner, string? name = null, string? descriptor = null)
    {
        _attributes.Add(Attribute("EnclosingMethod", [.. U2(Class(owner)), .. U2(name is null ? 0 : NameAndType(name, descriptor!))]));
        return this;
    }

    public SyntheticClass Unknown(string name, byte[] body)
    {
        _attributes.Add(Attribute(name, body));
        return this;
    }

    public byte[] Build()
    {
        var attributes = new List<byte[]>(_attributes);
        if (_inner.Count > 0)
        {
            attributes.Add(Attribute("InnerClasses", [.. U2(_inner.Count), .. _inner.SelectMany(r => (byte[])[.. U2(r.Inner), .. U2(r.Outer), .. U2(r.Name), .. U2(r.Access)])]));
        }

        if (_bootstraps.Count > 0)
        {
            attributes.Add(Attribute("BootstrapMethods", [.. U2(_bootstraps.Count), .. _bootstraps.SelectMany(b => (byte[])[.. U2(b.Handle), .. U2(b.Args.Length), .. b.Args.SelectMany(a => U2(a))])]));
        }

        // Every name is interned before the pool is written, so the count is final here.
        var tail = new List<byte>();
        tail.AddRange(U2(Access));
        tail.AddRange(U2(This));
        tail.AddRange(U2(Super));
        tail.AddRange(U2(_interfaces.Count));
        foreach (ushort i in _interfaces)
        {
            tail.AddRange(U2(i));
        }

        tail.AddRange(U2(_fields.Count));
        tail.AddRange(_fields.SelectMany(f => f));
        tail.AddRange(U2(_methods.Count));
        tail.AddRange(_methods.SelectMany(m => m));
        tail.AddRange(U2(attributes.Count));
        tail.AddRange(attributes.SelectMany(a => a));

        var head = new List<byte>();
        head.AddRange(U4(0xCAFEBABE));
        head.AddRange(U2(0));
        head.AddRange(U2(Major));
        head.AddRange(U2(PoolCount));
        head.AddRange(_pool.SelectMany(p => p));
        head.AddRange(tail);
        return [.. head];
    }

    /// <summary>One more than the highest index; a long or a double takes two.</summary>
    private int PoolCount => 1 + _pool.Sum(p => p[0] is 5 or 6 ? 2 : 1);

    private ushort Intern(string key, Func<byte[]> make)
    {
        if (_interned.TryGetValue(key, out ushort index))
        {
            return index;
        }

        byte[] entry = make();   // may intern what it refers to first
        if (_interned.TryGetValue(key, out index))
        {
            return index;
        }

        index = (ushort)PoolCount;
        _pool.Add(entry);
        _interned[key] = index;
        return index;
    }

    private byte[] Attribute(string name, byte[] body) => [.. U2(Utf8(name)), .. U4((uint)body.Length), .. body];

    public static byte[] U2(int value)
    {
        byte[] bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)value);
        return bytes;
    }

    public static byte[] U4(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    /// <summary>A JAR in memory: each entry deflated unless its name is in <paramref name="stored"/>.</summary>
    public static byte[] Jar(IEnumerable<(string Name, byte[] Bytes)> entries, params string[] stored)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                var entry = zip.CreateEntry(name, stored.Contains(name) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(bytes);
            }
        }

        return buffer.ToArray();
    }

    public static byte[] Manifest(string mainClass) => Encoding.UTF8.GetBytes($"Manifest-Version: 1.0\r\nCreated-By: 21 (Test)\r\nMain-Class: {mainClass}\r\n\r\n");
}
