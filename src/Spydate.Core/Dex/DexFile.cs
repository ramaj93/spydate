using System.Text;

namespace Spydate.Core.Dex;

/// <summary>
/// A parsed DEX file: the Dalvik executable an APK carries its code in. One file holds many classes, which share
/// its tables of strings, types, prototypes, fields and methods; instructions refer to those tables by index.
///
/// Parsing is strict about structure — the header, the id tables, each class's layout — and lenient about the
/// rest: a class whose data does not parse, or a method whose debug table or annotations do not, is kept as far as
/// it goes and reported in <see cref="Warnings"/>. Every count and offset is checked against the file, so hostile
/// input is a <see cref="DexFormatException"/> or a warning, never a crash.
/// </summary>
public sealed class DexFile
{
    public const uint NoIndex = 0xFFFFFFFF;

    private const int HeaderSize = 0x70;

    /// <summary>How deep annotations and arrays may nest in one encoded value.</summary>
    private const int MaxValueDepth = 32;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly List<string> _warnings = [];

    private DexFile(ReadOnlyMemory<byte> data)
    {
        _data = data;
    }

    /// <summary>The format version from the magic: 35 for Android 1.0–7, 37 (default methods), 38 (invoke-custom), 39, 40, 41.</summary>
    public int Version { get; private set; }

    public uint Checksum { get; private set; }

    /// <summary>Whether the header's Adler-32 checksum matches the bytes after it.</summary>
    public bool ChecksumMatches { get; private set; }

    public IReadOnlyList<string> Strings { get; private set; } = [];

    /// <summary>Type descriptors by type index: <c>I</c>, <c>Ljava/lang/String;</c>, <c>[B</c>.</summary>
    public IReadOnlyList<string> Types { get; private set; } = [];

    public IReadOnlyList<DexProto> Protos { get; private set; } = [];

    public IReadOnlyList<DexFieldRef> FieldRefs { get; private set; } = [];

    public IReadOnlyList<DexMethodRef> MethodRefs { get; private set; } = [];

    public IReadOnlyList<DexMethodHandle> MethodHandles { get; private set; } = [];

    public IReadOnlyList<DexCallSite> CallSites { get; private set; } = [];

    public IReadOnlyList<DexClass> Classes { get; private set; } = [];

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>The Android release a DEX version first came with.</summary>
    public string AndroidVersion => Version switch
    {
        35 => "Android 1.0+",
        37 => "Android 7.0+",
        38 => "Android 8.0+",
        39 => "Android 9.0+",
        40 => "Android 15+",
        41 => "Android 16+",
        _ => $"DEX {Version:D3}",
    };

    /// <summary>Whether bytes start like a DEX file: <c>dex\n</c>, three digits, a zero.</summary>
    public static bool IsDex(ReadOnlySpan<byte> head)
        => head.Length >= 8 && head[0] == 'd' && head[1] == 'e' && head[2] == 'x' && head[3] == '\n'
           && char.IsAsciiDigit((char)head[4]) && char.IsAsciiDigit((char)head[5]) && char.IsAsciiDigit((char)head[6]) && head[7] == 0;

    /// <summary><c>Lcom/example/Greeter;</c> is <c>com/example/Greeter</c>; any other descriptor comes back as it is.</summary>
    public static string InternalName(string descriptor)
        => descriptor.Length > 2 && descriptor[0] == 'L' && descriptor[^1] == ';' ? descriptor[1..^1] : descriptor;

    public static DexFile Parse(ReadOnlyMemory<byte> data)
    {
        var dex = new DexFile(data);
        dex.Read();
        return dex;
    }

    private void Read()
    {
        var span = _data.Span;
        if (!IsDex(span))
        {
            throw new DexFormatException("Not a DEX file: it does not start with \"dex\\n\" and a version.");
        }

        if (span.Length < HeaderSize)
        {
            throw new DexFormatException($"The file is {span.Length} bytes, less than a DEX header.");
        }

        Version = ((span[4] - '0') * 100) + ((span[5] - '0') * 10) + (span[6] - '0');
        var r = new DexReader(span, 8);
        Checksum = r.U4();
        r.Skip(20); // SHA-1 signature
        uint fileSize = r.U4();
        uint headerSize = r.U4();
        uint endian = r.U4();
        if (endian != 0x12345678)
        {
            throw new DexFormatException(endian == 0x78563412 ? "The DEX file is byte-swapped, which Android never shipped." : $"The endian tag is 0x{endian:X8}.");
        }

        if (fileSize != span.Length)
        {
            _warnings.Add($"The header gives the file as {fileSize} bytes; it is {span.Length}.");
        }

        if (headerSize != HeaderSize)
        {
            _warnings.Add($"The header size is 0x{headerSize:X}, not 0x70.");
        }

        ChecksumMatches = Adler32(span[12..]) == Checksum;
        if (!ChecksumMatches)
        {
            _warnings.Add("The checksum does not match: the file was changed after it was built.");
        }

        r.Skip(8); // link section
        uint mapOff = r.U4();
        var (stringsSize, stringsOff) = (r.U4(), r.U4());
        var (typesSize, typesOff) = (r.U4(), r.U4());
        var (protosSize, protosOff) = (r.U4(), r.U4());
        var (fieldsSize, fieldsOff) = (r.U4(), r.U4());
        var (methodsSize, methodsOff) = (r.U4(), r.U4());
        var (classesSize, classesOff) = (r.U4(), r.U4());

        Strings = ReadStrings(stringsSize, stringsOff);
        Types = ReadTable(typesSize, typesOff, 4, (ref DexReader t) => String(t.U4()));
        Protos = ReadTable(protosSize, protosOff, 12, (ref DexReader t) =>
        {
            string shorty = String(t.U4());
            string returns = Type(t.U4());
            uint parameters = t.U4();
            return new DexProto(shorty, returns, parameters == 0 ? [] : TypeList(parameters));
        });
        FieldRefs = ReadTable(fieldsSize, fieldsOff, 8, (ref DexReader t) =>
        {
            string owner = Type(t.U2());
            string type = Type(t.U2());
            return new DexFieldRef(owner, type, String(t.U4()));
        });
        MethodRefs = ReadTable(methodsSize, methodsOff, 8, (ref DexReader t) =>
        {
            string owner = Type(t.U2());
            var proto = Proto(t.U2());
            return new DexMethodRef(owner, String(t.U4()), proto);
        });

        if (mapOff != 0)
        {
            ReadMap(mapOff);
        }

        var classes = new List<DexClass>();
        var reader = new DexReader(span);
        int count = CountAt(classesSize, classesOff, 32);
        for (int i = 0; i < count; i++)
        {
            reader.Seek(classesOff + ((long)i * 32));
            uint classIdx = reader.U4();
            uint access = reader.U4();
            uint superIdx = reader.U4();
            uint interfacesOff = reader.U4();
            uint sourceIdx = reader.U4();
            uint annotationsOff = reader.U4();
            uint dataOff = reader.U4();
            uint staticValuesOff = reader.U4();

            string descriptor = Type(classIdx);
            try
            {
                classes.Add(ReadClass(descriptor, access, superIdx, interfacesOff, sourceIdx, annotationsOff, dataOff, staticValuesOff));
            }
            catch (DexFormatException ex)
            {
                _warnings.Add($"{InternalName(descriptor)}: {ex.Message}");
                classes.Add(new DexClass(descriptor, (DexAccess)access, superIdx == NoIndex ? null : TryType(superIdx), [], null));
            }
        }

        Classes = classes;
    }

    // --- tables ---------------------------------------------------------------------------------

    private delegate T Item<T>(ref DexReader reader);

    private int CountAt(uint size, uint offset, int itemSize)
    {
        if (size == 0)
        {
            return 0;
        }

        if ((long)offset + ((long)size * itemSize) > _data.Length)
        {
            throw new DexFormatException($"A table of {size} items at 0x{offset:X} runs past the end of the file.");
        }

        return (int)size;
    }

    private List<T> ReadTable<T>(uint size, uint offset, int itemSize, Item<T> read)
    {
        int count = CountAt(size, offset, itemSize);
        var items = new List<T>(count);
        var reader = new DexReader(_data.Span, count == 0 ? 0 : (int)offset);
        for (int i = 0; i < count; i++)
        {
            reader.Seek(offset + ((long)i * itemSize));
            items.Add(read(ref reader));
        }

        return items;
    }

    private List<string> ReadStrings(uint size, uint offset)
    {
        int count = CountAt(size, offset, 4);
        var strings = new List<string>(count);
        var span = _data.Span;
        var reader = new DexReader(span, count == 0 ? 0 : (int)offset);
        for (int i = 0; i < count; i++)
        {
            reader.Seek(offset + ((long)i * 4));
            var data = new DexReader(span, (int)Math.Min(reader.U4(), (uint)span.Length));
            int length = data.Count(data.Uleb128());
            strings.Add(Mutf8(span, data.Position, length));
        }

        return strings;
    }

    /// <summary>Modified UTF-8, as DEX and class files store text: surrogates encoded one by one, NUL as two bytes.</summary>
    private static string Mutf8(ReadOnlySpan<byte> span, int at, int utf16Length)
    {
        var sb = new StringBuilder(Math.Min(utf16Length, 1 << 16));
        while (at < span.Length && span[at] != 0)
        {
            int b = span[at++];
            if (b < 0x80)
            {
                sb.Append((char)b);
            }
            else if ((b & 0xE0) == 0xC0 && at < span.Length)
            {
                sb.Append((char)(((b & 0x1F) << 6) | (span[at++] & 0x3F)));
            }
            else if ((b & 0xF0) == 0xE0 && at + 1 < span.Length)
            {
                sb.Append((char)(((b & 0x0F) << 12) | ((span[at] & 0x3F) << 6) | (span[at + 1] & 0x3F)));
                at += 2;
            }
            else
            {
                sb.Append('�');
            }
        }

        return sb.ToString();
    }

    private string String(uint index)
        => index < Strings.Count ? Strings[(int)index] : throw new DexFormatException($"String index {index} is past the {Strings.Count} strings.");

    private string Type(uint index)
        => index < Types.Count ? Types[(int)index] : throw new DexFormatException($"Type index {index} is past the {Types.Count} types.");

    private string? TryType(uint index) => index < Types.Count ? Types[(int)index] : null;

    private DexProto Proto(uint index)
        => index < Protos.Count ? Protos[(int)index] : throw new DexFormatException($"Prototype index {index} is past the {Protos.Count} prototypes.");

    private DexFieldRef FieldRef(uint index)
        => index < FieldRefs.Count ? FieldRefs[(int)index] : throw new DexFormatException($"Field index {index} is past the {FieldRefs.Count} fields.");

    private DexMethodRef MethodRef(uint index)
        => index < MethodRefs.Count ? MethodRefs[(int)index] : throw new DexFormatException($"Method index {index} is past the {MethodRefs.Count} methods.");

    private List<string> TypeList(uint offset)
    {
        var r = new DexReader(_data.Span, (int)Math.Min(offset, (uint)_data.Length));
        int size = r.Count(r.U4(), 2);
        var types = new List<string>(size);
        for (int i = 0; i < size; i++)
        {
            types.Add(Type(r.U2()));
        }

        return types;
    }

    /// <summary>The map list names the sections the header does not: method handles and call sites (DEX 038+).</summary>
    private void ReadMap(uint offset)
    {
        try
        {
            var r = new DexReader(_data.Span, (int)Math.Min(offset, (uint)_data.Length));
            int size = r.Count(r.U4(), 12);
            uint handlesSize = 0, handlesOff = 0, sitesSize = 0, sitesOff = 0;
            for (int i = 0; i < size; i++)
            {
                ushort type = r.U2();
                r.Skip(2);
                uint count = r.U4();
                uint at = r.U4();
                switch (type)
                {
                    case 0x0007:
                        (sitesSize, sitesOff) = (count, at);
                        break;
                    case 0x0008:
                        (handlesSize, handlesOff) = (count, at);
                        break;
                }
            }

            MethodHandles = ReadTable(handlesSize, handlesOff, 8, (ref DexReader t) =>
            {
                int kind = t.U2();
                t.Skip(2);
                uint member = t.U2();
                return kind <= 3 ? new DexMethodHandle(kind, FieldRef(member), null) : new DexMethodHandle(kind, null, MethodRef(member));
            });
            CallSites = ReadTable(sitesSize, sitesOff, 4, (ref DexReader t) =>
            {
                var values = EncodedArray(t.U4());
                DexMethodHandle? bootstrap = values.Count > 0 && values[0].Value is DexMethodHandle handle ? handle : null;
                string name = values.Count > 1 && values[1].Value is string text ? text : "?";
                DexProto? type = values.Count > 2 && values[2].Value is DexProto proto ? proto : null;
                return new DexCallSite(bootstrap, name, type, values.Skip(3).ToList());
            });
        }
        catch (DexFormatException ex)
        {
            _warnings.Add($"The map list: {ex.Message}");
        }
    }

    // --- classes ------------------------------------------------------------------------------------

    private DexClass ReadClass(string descriptor, uint access, uint superIdx, uint interfacesOff, uint sourceIdx, uint annotationsOff, uint dataOff, uint staticValuesOff)
    {
        string name = InternalName(descriptor);
        var directory = annotationsOff == 0 ? null : ReadDirectory(annotationsOff, name);

        var staticFields = new List<DexField>();
        var instanceFields = new List<DexField>();
        var directMethods = new List<DexMethod>();
        var virtualMethods = new List<DexMethod>();
        if (dataOff != 0)
        {
            var r = new DexReader(_data.Span, (int)Math.Min(dataOff, (uint)_data.Length));
            int statics = r.Count(r.Uleb128(), 2);
            int instances = r.Count(r.Uleb128(), 2);
            int directs = r.Count(r.Uleb128(), 3);
            int virtuals = r.Count(r.Uleb128(), 3);
            ReadFields(ref r, statics, staticFields, directory);
            ReadFields(ref r, instances, instanceFields, directory);
            ReadMethods(ref r, directs, directMethods, directory, name);
            ReadMethods(ref r, virtuals, virtualMethods, directory, name);
        }

        if (staticValuesOff != 0)
        {
            try
            {
                var values = EncodedArray(staticValuesOff);
                for (int i = 0; i < staticFields.Count && i < values.Count; i++)
                {
                    staticFields[i] = staticFields[i] with { StaticValue = values[i] };
                }
            }
            catch (DexFormatException ex)
            {
                _warnings.Add($"{name}: static values: {ex.Message}");
            }
        }

        return new DexClass(
            descriptor,
            (DexAccess)access,
            superIdx == NoIndex ? null : Type(superIdx),
            interfacesOff == 0 ? [] : TypeList(interfacesOff),
            sourceIdx == NoIndex ? null : String(sourceIdx))
        {
            StaticFields = staticFields,
            InstanceFields = instanceFields,
            DirectMethods = directMethods,
            VirtualMethods = virtualMethods,
            Annotations = directory?.Class ?? [],
        };
    }

    private void ReadFields(ref DexReader r, int count, List<DexField> into, Directory? directory)
    {
        uint index = 0;
        for (int i = 0; i < count; i++)
        {
            index += r.Uleb128();
            var access = (DexAccess)r.Uleb128();
            into.Add(new DexField(access, FieldRef(index)) { Annotations = directory?.Fields.GetValueOrDefault(index) ?? [] });
        }
    }

    private void ReadMethods(ref DexReader r, int count, List<DexMethod> into, Directory? directory, string owner)
    {
        uint index = 0;
        for (int i = 0; i < count; i++)
        {
            index += r.Uleb128();
            var access = (DexAccess)r.Uleb128();
            uint codeOff = r.Uleb128();
            var method = MethodRef(index);
            DexCode? code = null;
            if (codeOff != 0)
            {
                try
                {
                    code = ReadCode(codeOff, $"{owner}.{method.Name}");
                }
                catch (DexFormatException ex)
                {
                    _warnings.Add($"{owner}.{method.Name}: {ex.Message}");
                }
            }

            into.Add(new DexMethod(access, method)
            {
                Code = code,
                Annotations = directory?.Methods.GetValueOrDefault(index) ?? [],
                ParameterAnnotations = directory?.Parameters.GetValueOrDefault(index) ?? [],
            });
        }
    }

    private DexCode ReadCode(uint offset, string where)
    {
        var span = _data.Span;
        var r = new DexReader(span, (int)Math.Min(offset, (uint)span.Length));
        int registers = r.U2();
        int ins = r.U2();
        int outs = r.U2();
        int triesSize = r.U2();
        uint debugOff = r.U4();
        int units = r.Count(r.U4(), 2);
        var insns = new ushort[units];
        for (int i = 0; i < units; i++)
        {
            insns[i] = r.U2();
        }

        var tries = new List<DexTry>(triesSize);
        if (triesSize > 0)
        {
            if (units % 2 == 1)
            {
                r.Skip(2);
            }

            int triesAt = r.Position;
            int handlersAt = triesAt + (triesSize * 8);
            for (int i = 0; i < triesSize; i++)
            {
                r.Seek(triesAt + (i * 8));
                int start = (int)r.U4();
                int length = r.U2();
                int handlerOff = r.U2();
                var h = new DexReader(span, handlersAt);
                h.Seek(handlersAt + handlerOff);
                int size = h.Sleb128();
                int typed = Math.Abs(size);
                if (typed > units)
                {
                    throw new DexFormatException($"A try names {typed} handlers in a method of {units} code units.");
                }

                var handlers = new List<(string, int)>(typed);
                for (int k = 0; k < typed; k++)
                {
                    string type = Type(h.Uleb128());
                    handlers.Add((type, (int)h.Uleb128()));
                }

                int? catchAll = size <= 0 ? (int)h.Uleb128() : null;
                tries.Add(new DexTry(start, length, handlers, catchAll));
            }
        }

        DexDebugInfo? debug = null;
        if (debugOff != 0)
        {
            try
            {
                debug = ReadDebug(debugOff, registers, ins, units);
            }
            catch (DexFormatException ex)
            {
                _warnings.Add($"{where}: debug info: {ex.Message}");
            }
        }

        return new DexCode(registers, ins, outs, insns, tries, debug) { File = this };
    }

    /// <summary>The debug table is a little state machine: it moves an address and a line, and starts and ends locals.</summary>
    private DexDebugInfo ReadDebug(uint offset, int registers, int ins, int units)
    {
        var r = new DexReader(_data.Span, (int)Math.Min(offset, (uint)_data.Length));
        int line = (int)r.Uleb128();
        int parameterCount = r.Count(r.Uleb128());
        var parameters = new List<string?>(parameterCount);
        for (int i = 0; i < parameterCount; i++)
        {
            int name = r.Uleb128p1();
            parameters.Add(name >= 0 && name < Strings.Count ? Strings[name] : null);
        }

        var lines = new List<(int, int)>();
        var locals = new List<DexLocal>();
        var open = new Dictionary<int, DexLocal>();
        var last = new Dictionary<int, DexLocal>();
        int address = 0;
        void End(int register)
        {
            if (open.Remove(register, out var local))
            {
                locals.Add(local with { End = address });
                last[register] = local;
            }
        }

        string? Name(int index) => index >= 0 && index < Strings.Count ? Strings[index] : null;
        string? TypeName(int index) => index >= 0 && index < Types.Count ? Types[index] : null;

        for (int steps = 0; steps < 1 << 20; steps++)
        {
            byte op = r.U1();
            switch (op)
            {
                case 0x00:
                    // A local still open when the table ends is live to the end of the method.
                    locals.AddRange(open.Values.OrderBy(l => l.Register).Select(l => l with { End = units }));
                    return new DexDebugInfo(line, parameters, lines, locals);
                case 0x01:
                    address += (int)r.Uleb128();
                    break;
                case 0x02:
                    line += r.Sleb128();
                    break;
                case 0x03:
                case 0x04:
                {
                    int register = (int)r.Uleb128();
                    string? name = Name(r.Uleb128p1());
                    string? type = TypeName(r.Uleb128p1());
                    string? signature = op == 0x04 ? Name(r.Uleb128p1()) : null;
                    End(register);
                    open[register] = new DexLocal(register, address, units, name, type, signature);
                    break;
                }

                case 0x05:
                    End((int)r.Uleb128());
                    break;
                case 0x06:
                {
                    int register = (int)r.Uleb128();
                    if (last.TryGetValue(register, out var previous) && !open.ContainsKey(register))
                    {
                        open[register] = previous with { Start = address, End = units };
                    }

                    break;
                }

                case 0x07:
                case 0x08:
                    break;
                case 0x09:
                    r.Uleb128p1();
                    break;
                default:
                {
                    int adjusted = op - 0x0A;
                    line += -4 + (adjusted % 15);
                    address += adjusted / 15;
                    lines.Add((address, line));
                    break;
                }
            }

            if (address > units)
            {
                throw new DexFormatException($"The line table moves to {address}, past the {units} code units.");
            }
        }

        throw new DexFormatException("The debug table does not end.");
    }

    // --- annotations and values ---------------------------------------------------------------------

    private sealed record Directory(
        IReadOnlyList<DexAnnotation> Class,
        Dictionary<uint, IReadOnlyList<DexAnnotation>> Fields,
        Dictionary<uint, IReadOnlyList<DexAnnotation>> Methods,
        Dictionary<uint, IReadOnlyList<IReadOnlyList<DexAnnotation>>> Parameters);

    private Directory? ReadDirectory(uint offset, string owner)
    {
        try
        {
            var r = new DexReader(_data.Span, (int)Math.Min(offset, (uint)_data.Length));
            uint classOff = r.U4();
            int fields = r.Count(r.U4(), 8);
            int methods = r.Count(r.U4(), 8);
            int parameters = r.Count(r.U4(), 8);
            var directory = new Directory(classOff == 0 ? [] : AnnotationSet(classOff), [], [], []);
            for (int i = 0; i < fields; i++)
            {
                directory.Fields[r.U4()] = AnnotationSet(r.U4());
            }

            for (int i = 0; i < methods; i++)
            {
                directory.Methods[r.U4()] = AnnotationSet(r.U4());
            }

            for (int i = 0; i < parameters; i++)
            {
                uint method = r.U4();
                var list = new DexReader(_data.Span, (int)Math.Min(r.U4(), (uint)_data.Length));
                int count = list.Count(list.U4(), 4);
                var sets = new List<IReadOnlyList<DexAnnotation>>(count);
                for (int k = 0; k < count; k++)
                {
                    uint set = list.U4();
                    sets.Add(set == 0 ? [] : AnnotationSet(set));
                }

                directory.Parameters[method] = sets;
            }

            return directory;
        }
        catch (DexFormatException ex)
        {
            _warnings.Add($"{owner}: annotations: {ex.Message}");
            return null;
        }
    }

    private List<DexAnnotation> AnnotationSet(uint offset)
    {
        var r = new DexReader(_data.Span, (int)Math.Min(offset, (uint)_data.Length));
        int size = r.Count(r.U4(), 4);
        var annotations = new List<DexAnnotation>(size);
        for (int i = 0; i < size; i++)
        {
            var item = new DexReader(_data.Span, (int)Math.Min(r.U4(), (uint)_data.Length));
            var visibility = (DexVisibility)item.U1();
            annotations.Add(Annotation(ref item, visibility, 0));
        }

        return annotations;
    }

    private DexAnnotation Annotation(ref DexReader r, DexVisibility visibility, int depth)
    {
        string type = Type(r.Uleb128());
        int size = r.Count(r.Uleb128(), 2);
        var elements = new List<(string, DexValue)>(size);
        for (int i = 0; i < size; i++)
        {
            string name = String(r.Uleb128());
            elements.Add((name, Value(ref r, depth + 1)));
        }

        return new DexAnnotation(visibility, type, elements);
    }

    private List<DexValue> EncodedArray(uint offset)
    {
        var r = new DexReader(_data.Span, (int)Math.Min(offset, (uint)_data.Length));
        return Array(ref r, 0);
    }

    private List<DexValue> Array(ref DexReader r, int depth)
    {
        int size = r.Count(r.Uleb128());
        var values = new List<DexValue>(size);
        for (int i = 0; i < size; i++)
        {
            values.Add(Value(ref r, depth + 1));
        }

        return values;
    }

    private DexValue Value(ref DexReader r, int depth)
    {
        if (depth > MaxValueDepth)
        {
            throw new DexFormatException($"Encoded values nest more than {MaxValueDepth} deep.");
        }

        byte head = r.U1();
        int type = head & 0x1F;
        int arg = head >> 5;
        switch (type)
        {
            case 0x00: return new DexValue(DexValueKind.Byte, Signed(ref r, arg + 1));
            case 0x02: return new DexValue(DexValueKind.Short, Signed(ref r, arg + 1));
            case 0x03: return new DexValue(DexValueKind.Char, Unsigned(ref r, arg + 1));
            case 0x04: return new DexValue(DexValueKind.Int, Signed(ref r, arg + 1));
            case 0x06: return new DexValue(DexValueKind.Long, Signed(ref r, arg + 1));
            case 0x10: return new DexValue(DexValueKind.Float, BitConverter.Int32BitsToSingle((int)(Unsigned(ref r, arg + 1) << ((3 - arg) * 8))));
            case 0x11: return new DexValue(DexValueKind.Double, BitConverter.Int64BitsToDouble((long)((ulong)Unsigned(ref r, arg + 1) << ((7 - arg) * 8))));
            case 0x15: return new DexValue(DexValueKind.MethodType, Proto((uint)Unsigned(ref r, arg + 1)));
            case 0x16:
            {
                long index = Unsigned(ref r, arg + 1);
                return new DexValue(DexValueKind.MethodHandle, index < MethodHandles.Count ? MethodHandles[(int)index] : null);
            }

            case 0x17: return new DexValue(DexValueKind.String, String((uint)Unsigned(ref r, arg + 1)));
            case 0x18: return new DexValue(DexValueKind.Type, Type((uint)Unsigned(ref r, arg + 1)));
            case 0x19: return new DexValue(DexValueKind.Field, FieldRef((uint)Unsigned(ref r, arg + 1)));
            case 0x1A: return new DexValue(DexValueKind.Method, MethodRef((uint)Unsigned(ref r, arg + 1)));
            case 0x1B: return new DexValue(DexValueKind.Enum, FieldRef((uint)Unsigned(ref r, arg + 1)));
            case 0x1C: return new DexValue(DexValueKind.Array, Array(ref r, depth));
            case 0x1D: return new DexValue(DexValueKind.Annotation, Annotation(ref r, DexVisibility.Build, depth));
            case 0x1E: return new DexValue(DexValueKind.Null, null);
            case 0x1F: return new DexValue(DexValueKind.Boolean, arg != 0);
            default: throw new DexFormatException($"Encoded value type 0x{type:X2} is not one DEX defines.");
        }
    }

    private static long Unsigned(ref DexReader r, int size)
    {
        ulong value = 0;
        for (int i = 0; i < size; i++)
        {
            value |= (ulong)r.U1() << (i * 8);
        }

        return (long)value;
    }

    private static long Signed(ref DexReader r, int size)
    {
        long value = Unsigned(ref r, size);
        int shift = 64 - (size * 8);
        return (value << shift) >> shift;
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint Mod = 65521;
        uint a = 1, b = 0;
        int i = 0;
        while (i < data.Length)
        {
            int end = Math.Min(data.Length, i + 5552);
            for (; i < end; i++)
            {
                a += data[i];
                b += a;
            }

            a %= Mod;
            b %= Mod;
        }

        return (b << 16) | a;
    }
}
