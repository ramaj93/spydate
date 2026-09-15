using System.IO;
using Microsoft.Diagnostics.Runtime;

namespace Spydate.Debugger;

/// <summary>One managed thread and the frames on it, as the DAC sees them at a stop.</summary>
public sealed record OverlayThread(uint OsId, int ManagedId, bool Alive, IReadOnlyList<OverlayFrame> Frames);

/// <summary>
/// One frame of a managed stack walk. <paramref name="Kind"/> tells a real managed method from the
/// runtime and native frames the walk passes through, so a caller can show only the managed ones.
/// </summary>
public sealed record OverlayFrame(ulong InstructionPointer, ulong StackPointer, string Kind, string? Method, string? Module);

/// <summary>
/// Where a native address falls in managed code: the method it is in, and the IL offset if the
/// JIT map has one for that address. This is the managed half of a native stop — the same instruction
/// pointer the registers show, said in the terms the decompiled listing is in.
/// </summary>
public sealed record OverlayLocation(string Method, string? Module, int MethodToken, int IlOffset, ulong MethodStart);

/// <summary>A managed field or static, read straight out of memory, with its value rendered flat.</summary>
public sealed record OverlayField(string Name, string Type, string Value, ulong Reference);

/// <summary>A managed object on the heap: its type and its instance fields.</summary>
public sealed record OverlayObject(ulong Address, string Type, IReadOnlyList<OverlayField> Fields);

/// <summary>
/// What the code at a native address is, for a managed step: the IL offset, the native range of that
/// offset (<paramref name="RangeStart"/>..<paramref name="RangeEnd"/>, half-open), and the method's
/// whole native extent (<paramref name="MethodStart"/>..<paramref name="MethodEnd"/>).
/// </summary>
public sealed record OverlayStep(int IlOffset, ulong RangeStart, ulong RangeEnd, ulong MethodStart, ulong MethodEnd, string Method);

/// <summary>
/// The native address to break at for a managed method and IL offset, or the reason there is none.
///
/// <see cref="Ok"/> is the whole question a caller asks: a real address it can plant an int3 at, or a
/// <see cref="Problem"/> to report — a cold method that has not been JITted, a name that does not
/// resolve, an offset that maps to no code. The refusal is as much the point as the address: a managed
/// breakpoint that fails quietly is worse than one that says it cannot be set yet.
/// </summary>
public sealed record OverlayResolution(ulong Address, int MethodToken, string? Method, string? Problem)
{
    public bool Ok => Problem is null && Address != 0;

    /// <summary>
    /// Whether the refusal is specifically "the method exists but has not been JITted" — the case a
    /// pending breakpoint holds for, as against a name that does not resolve at all.
    /// </summary>
    public bool NotCompiled { get; init; }
}

/// <summary>
/// The managed overlay: ClrMD attached to the debuggee <em>passively</em>, layered on the native loop
/// that owns the one OS debug port.
///
/// It never writes and never needs a debug port — <see cref="DataTarget.AttachToProcess(int, bool)"/>
/// with <c>suspend: false</c> is <c>OpenProcess</c> and <c>ReadProcessMemory</c> through the DAC, which
/// is why it can sit beside a native debugger rather than compete with it. It answers "what managed
/// thing is here" — threads, stacks, objects, fields, statics, and the IL-to-native map — while
/// <see cref="DebugSession"/> answers "stop, continue, step" and writes the int3s.
///
/// Reads are only meaningful while the process is stopped, which is exactly when the native loop has
/// it. When the loop lets the process run again the DAC's cached view goes stale, so the loop calls
/// <see cref="MarkMoved"/>; the next read flushes rather than re-attaching, which is far cheaper.
///
/// The CLR is not up the instant a process starts, so the attach is lazy: the first query after a
/// runtime is present succeeds, and until then every query answers empty. A native debuggee with no
/// CLR answers empty forever, which is correct.
/// </summary>
public sealed class ManagedOverlay : IDisposable
{
    private readonly int _pid;
    private readonly Func<bool>? _running;
    private readonly object _gate = new();
    private DataTarget? _target;
    private ClrRuntime? _runtime;
    private volatile bool _moved;
    private bool _disposed;

    /// <param name="pid">The process to attach to.</param>
    /// <param name="running">
    /// Whether the debuggee is running rather than stopped, if the owner can say. A read of a running
    /// process is always of live, moving state — a method may have JITted since the last read with no
    /// stop in between to announce it — so every such read flushes. A read at a stop is consistent and
    /// is cached until <see cref="MarkMoved"/>. Null means treat every read as a stopped read.
    /// </param>
    public ManagedOverlay(uint pid, Func<bool>? running = null)
    {
        _pid = (int)pid;
        _running = running;
    }

    /// <summary>
    /// The debuggee has run since the last read, so the DAC's cached view is stale. Set by the loop
    /// when it continues or steps; honoured on the next query by a flush rather than a re-attach.
    /// </summary>
    public void MarkMoved() => _moved = true;

    /// <summary>Whether a CLR has been found in the process. Attaches on first ask.</summary>
    public bool HasClr => Runtime() is not null;

    /// <summary>
    /// The runtime, attached lazily and flushed when the debuggee has moved. Null when there is no CLR
    /// in the process yet, or the attach failed — both of which are ordinary, not exceptional.
    /// </summary>
    private ClrRuntime? Runtime()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            if (_runtime is not null)
            {
                // Flush when the loop said the process moved, or whenever it is running: a running
                // process's DAC view is live and changing, so anything cached from a previous read
                // may already be wrong — a method that has JITted since, most of all.
                if (_moved || (_running?.Invoke() ?? false))
                {
                    _moved = false;
                    try { _runtime.FlushCachedData(); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { }
                }

                return _runtime;
            }

            try
            {
                _target = DataTarget.AttachToProcess(_pid, suspend: false);
                if (_target.ClrVersions.Length == 0)
                {
                    _target.Dispose();
                    _target = null;
                    return null;
                }

                _runtime = _target.ClrVersions[0].CreateRuntime();
                _moved = false;
                return _runtime;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _target?.Dispose();
                _target = null;
                _runtime = null;
                return null;
            }
        }
    }

    /// <summary>
    /// Every managed thread and the frames the DAC can walk on it. Empty until a CLR is up. The walk
    /// is capped so a corrupt or recursive stack cannot spin forever.
    /// </summary>
    public IReadOnlyList<OverlayThread> Threads()
    {
        if (Runtime() is not { } runtime)
        {
            return Array.Empty<OverlayThread>();
        }

        var threads = new List<OverlayThread>();
        try
        {
            foreach (var thread in runtime.Threads)
            {
                var frames = new List<OverlayFrame>();
                foreach (var frame in thread.EnumerateStackTrace())
                {
                    frames.Add(new OverlayFrame(
                        frame.InstructionPointer,
                        frame.StackPointer,
                        frame.Kind.ToString(),
                        frame.Method?.Signature ?? frame.Method?.Name,
                        ModuleOf(frame.Method)));

                    if (frames.Count >= 1024)
                    {
                        break;
                    }
                }

                threads.Add(new OverlayThread(thread.OSThreadId, thread.ManagedThreadId, thread.IsAlive, frames));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A read that lands on the process mid-move can throw; the caller gets what was walked so
            // far rather than an exception, and the next stop reads clean.
        }

        return threads;
    }

    /// <summary>
    /// The managed method a native address is in, and the IL offset there when the JIT map has one.
    /// Null when the address is native, or in a method the DAC cannot resolve. This is what puts a
    /// managed name beside a native register dump.
    /// </summary>
    /// <summary>
    /// The method a MethodDesc handle belongs to, named the way a managed breakpoint names one — its
    /// declaring type's full name and the method's metadata token. The JIT's shared prestub takes the
    /// MethodDesc as its argument, so this is how the loop tells, at that stub, which method is being
    /// compiled — and so which pending first-call breakpoint (if any) this JIT is for. Null when there is
    /// no CLR, or the handle is not a method. The mapping is fixed, so this needs no cache flush.
    /// </summary>
    public (string Type, int Token)? MethodByHandle(ulong methodDesc)
    {
        if (Runtime() is not { } runtime)
        {
            return null;
        }

        try
        {
            var method = runtime.GetMethodByHandle(methodDesc);
            if (method?.Type is not { } type)
            {
                return null;
            }

            return (type.Name ?? string.Empty, (int)method.MetadataToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    public OverlayLocation? LocationOf(ulong instructionPointer)
    {
        if (Runtime() is not { } runtime)
        {
            return null;
        }

        try
        {
            var method = runtime.GetMethodByInstructionPointer(instructionPointer);
            if (method is null)
            {
                return null;
            }

            return new OverlayLocation(
                method.Signature ?? method.Name ?? "?",
                ModuleOf(method),
                (int)method.MetadataToken,
                IlOffsetAt(method, instructionPointer),
                method.NativeCode);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// The native address to break at for a method and IL offset — the whole of what a managed
    /// breakpoint needs from the DAC, since the native loop does the planting.
    ///
    /// A method that has not been JITted has no address: <c>NativeCode</c> is a sentinel and the IL
    /// map is empty, and that is refused with a reason rather than a zero the caller has to guess at.
    /// A compiled method's IL offset is turned into a native address through its own map, exact where
    /// the offset is a mapped boundary and otherwise the start of the statement that contains it.
    /// </summary>
    public OverlayResolution Resolve(string typeName, string methodName, int ilOffset)
        => Resolve(typeName, ilOffset, m => m.Name == methodName, methodName, $"has no method {methodName}");

    /// <summary>
    /// The same, but naming the method by its metadata token rather than its name — which is what the
    /// window has when a line is clicked, and which an overload cannot make ambiguous the way a bare
    /// name can. The type is still found by name, since a token is only meaningful inside its module.
    /// </summary>
    public OverlayResolution Resolve(string typeName, int methodToken, int ilOffset)
        => Resolve(typeName, ilOffset, m => (int)m.MetadataToken == methodToken, $"0x{methodToken:X8}",
            $"has no method with token 0x{methodToken:X8}");

    private OverlayResolution Resolve(string typeName, int ilOffset, Func<ClrMethod, bool> match, string methodLabel, string missing)
    {
        if (Runtime() is not { } runtime)
        {
            return new OverlayResolution(0, 0, null, "there is no CLR in the process yet");
        }

        try
        {
            var type = FindType(runtime, typeName);
            if (type is null)
            {
                return new OverlayResolution(0, 0, null, $"no type {typeName} is loaded");
            }

            var method = type.Methods.FirstOrDefault(match);
            if (method is null)
            {
                return new OverlayResolution(0, 0, null, $"{typeName} {missing}");
            }

            int token = (int)method.MetadataToken;
            var map = method.ILOffsetMap;
            if (!IsCompiled(method.NativeCode) || map.Length == 0)
            {
                return new OverlayResolution(0, token, method.Signature,
                    $"{methodLabel} is not compiled yet, so there is no native code to break in; it JITs on its first call")
                {
                    NotCompiled = true,
                };
            }

            ulong address = NativeForIl(map, ilOffset);
            if (address == 0)
            {
                return new OverlayResolution(0, token, method.Signature,
                    $"IL offset {ilOffset} of {methodLabel} maps to no native code");
            }

            return new OverlayResolution(address, token, method.Signature, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new OverlayResolution(0, 0, null, "the DAC could not resolve the method");
        }
    }

    /// <summary>
    /// The IL offsets a method's JIT map has code for, sorted and distinct — the offsets a breakpoint
    /// can actually be put at. Empty for a method that is not compiled. Lets a caller pick an interior
    /// offset without knowing the method's IL layout in advance.
    /// </summary>
    public IReadOnlyList<int> IlOffsets(string typeName, string methodName)
    {
        if (Runtime() is not { } runtime)
        {
            return Array.Empty<int>();
        }

        try
        {
            var method = FindType(runtime, typeName)?.Methods.FirstOrDefault(m => m.Name == methodName);
            if (method is null)
            {
                return Array.Empty<int>();
            }

            return method.ILOffsetMap
                .Where(e => e.ILOffset >= 0 && e.StartAddress != 0)
                .Select(e => e.ILOffset)
                .Distinct()
                .OrderBy(o => o)
                .ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Array.Empty<int>();
        }
    }

    /// <summary>Whether a <c>NativeCode</c> is a real address rather than the not-jitted sentinel.</summary>
    private static bool IsCompiled(ulong nativeCode) => nativeCode is not 0 and not 0xFFFFFFFFFFFFFFFF;

    /// <summary>
    /// What a managed step needs to know about the code at a native address: which IL offset it is in,
    /// the native range of that IL offset, and the whole method's native extent. Null when the address
    /// is native or in a method with no map.
    ///
    /// A step reads this once at the start and then single-steps: while the instruction pointer stays
    /// inside <see cref="RangeStart"/>..<see cref="RangeEnd"/> it is still on the same IL offset, and
    /// when it leaves that range the offset has changed — which is the whole of "step one IL offset",
    /// done without a DAC read per instruction.
    /// </summary>
    public OverlayStep? StepInfoAt(ulong ip)
    {
        if (Runtime() is not { } runtime)
        {
            return null;
        }

        try
        {
            var method = runtime.GetMethodByInstructionPointer(ip);
            if (method is null)
            {
                return null;
            }

            ulong rangeStart = 0;
            ulong rangeEnd = 0;
            int il = -1;
            ulong methodStart = ulong.MaxValue;
            ulong methodEnd = 0;

            foreach (var entry in method.ILOffsetMap)
            {
                if (entry.StartAddress == 0 || entry.EndAddress <= entry.StartAddress)
                {
                    continue;
                }

                if (entry.StartAddress < methodStart)
                {
                    methodStart = entry.StartAddress;
                }

                if (entry.EndAddress > methodEnd)
                {
                    methodEnd = entry.EndAddress;
                }

                if (ip >= entry.StartAddress && ip < entry.EndAddress)
                {
                    rangeStart = entry.StartAddress;
                    rangeEnd = entry.EndAddress;
                    il = entry.ILOffset;
                }
            }

            if (rangeEnd == 0 || methodEnd == 0)
            {
                return null;
            }

            return new OverlayStep(il, rangeStart, rangeEnd, methodStart, methodEnd,
                method.Signature ?? method.Name ?? "?");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// The native address for an IL offset: the exact map entry when the offset is a boundary,
    /// otherwise the start of the statement that contains it — the greatest mapped offset at or below
    /// it. Zero when nothing maps at or below the offset.
    /// </summary>
    private static ulong NativeForIl(System.Collections.Immutable.ImmutableArray<ILToNativeMap> map, int ilOffset)
    {
        foreach (var entry in map)
        {
            if (entry.ILOffset == ilOffset && entry.StartAddress != 0)
            {
                return entry.StartAddress;
            }
        }

        ulong address = 0;
        int best = -1;
        foreach (var entry in map)
        {
            if (entry.ILOffset >= 0 && entry.ILOffset <= ilOffset && entry.StartAddress != 0 && entry.ILOffset > best)
            {
                best = entry.ILOffset;
                address = entry.StartAddress;
            }
        }

        return address;
    }

    /// <summary>
    /// A heap object's type and instance fields, read out of memory. Null for a null or unreadable
    /// address. Fields are rendered flat: a primitive as its value, a string as its text, a reference
    /// as its type with the referent's address in <see cref="OverlayField.Reference"/> to follow.
    /// </summary>
    public OverlayObject? ObjectAt(ulong address)
    {
        if (address == 0 || Runtime() is not { } runtime)
        {
            return null;
        }

        try
        {
            var obj = runtime.Heap.GetObject(address);
            if (obj.IsNull || obj.Type is null)
            {
                return null;
            }

            var fields = new List<OverlayField>();
            foreach (var field in obj.Type.Fields)
            {
                fields.Add(ReadInstanceField(obj, field));
            }

            return new OverlayObject(address, obj.Type.Name ?? "?", fields);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// The static fields of a type, by full name, read from the first application domain. Empty when
    /// the type is not loaded or has no statics. Statics are where a lot of a program's real state
    /// lives, and they are reachable without an object to start from.
    /// </summary>
    public IReadOnlyList<OverlayField> Statics(string typeName)
    {
        if (Runtime() is not { } runtime)
        {
            return Array.Empty<OverlayField>();
        }

        try
        {
            var type = FindType(runtime, typeName);
            var domain = runtime.AppDomains.Length > 0 ? runtime.AppDomains[0] : null;
            if (type is null || domain is null)
            {
                return Array.Empty<OverlayField>();
            }

            var fields = new List<OverlayField>();
            foreach (var field in type.StaticFields)
            {
                fields.Add(ReadStaticField(field, domain));
            }

            return fields;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Array.Empty<OverlayField>();
        }
    }

    private static ClrType? FindType(ClrRuntime runtime, string typeName)
    {
        foreach (var module in runtime.EnumerateModules())
        {
            if (module.GetTypeByName(typeName) is { } type)
            {
                return type;
            }
        }

        return null;
    }

    private OverlayField ReadInstanceField(ClrObject obj, ClrInstanceField field)
    {
        string name = field.Name ?? "?";
        string type = field.Type?.Name ?? "?";

        try
        {
            // Primitive first: a primitive is also a value type, so testing IsValueType before this
            // would swallow every int and bool and render it as its type name rather than its value.
            if (field.IsPrimitive)
            {
                return new OverlayField(name, type, ReadPrimitiveInstance(obj, field), 0);
            }

            if (field.IsObjectReference)
            {
                if (field.Type?.IsString == true)
                {
                    string? text = obj.ReadStringField(name);
                    return new OverlayField(name, type, text is null ? "null" : Quote(text), 0);
                }

                var referent = obj.ReadObjectField(name);
                return referent.IsNull
                    ? new OverlayField(name, type, "null", 0)
                    : new OverlayField(name, type, referent.Type?.Name ?? "object", referent.Address);
            }

            // A non-primitive value type (a struct): named rather than read deep, which a flat view
            // cannot do usefully anyway.
            return new OverlayField(name, type, type, 0);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new OverlayField(name, type, "?", 0);
        }
    }

    private OverlayField ReadStaticField(ClrStaticField field, ClrAppDomain domain)
    {
        string name = field.Name ?? "?";
        string type = field.Type?.Name ?? "?";

        try
        {
            // Primitive before value type, for the same reason as the instance path: an int is a
            // value type, and checked the other way round every static number reads as "System.Int32".
            if (field.IsPrimitive)
            {
                return new OverlayField(name, type, ReadPrimitiveStatic(field, domain), 0);
            }

            if (field.IsObjectReference)
            {
                if (field.Type?.IsString == true)
                {
                    string? text = field.ReadString(domain);
                    return new OverlayField(name, type, text is null ? "null" : Quote(text), 0);
                }

                var referent = field.ReadObject(domain);
                return referent.IsNull
                    ? new OverlayField(name, type, "null", 0)
                    : new OverlayField(name, type, referent.Type?.Name ?? "object", referent.Address);
            }

            return new OverlayField(name, type, type, 0);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new OverlayField(name, type, "?", 0);
        }
    }

    private static string ReadPrimitiveInstance(ClrObject obj, ClrInstanceField field) => field.ElementType switch
    {
        ClrElementType.Boolean => obj.ReadField<bool>(field.Name!).ToString(),
        ClrElementType.Char => ((int)obj.ReadField<char>(field.Name!)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.Int8 => obj.ReadField<sbyte>(field.Name!).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.UInt8 => obj.ReadField<byte>(field.Name!).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.Int16 => obj.ReadField<short>(field.Name!).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.UInt16 => obj.ReadField<ushort>(field.Name!).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.Int32 => obj.ReadField<int>(field.Name!).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.UInt32 => obj.ReadField<uint>(field.Name!).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.Int64 => obj.ReadField<long>(field.Name!).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.UInt64 => obj.ReadField<ulong>(field.Name!).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.Float => obj.ReadField<float>(field.Name!).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.Double => obj.ReadField<double>(field.Name!).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.NativeInt => $"0x{obj.ReadField<long>(field.Name!):X}",
        ClrElementType.NativeUInt => $"0x{obj.ReadField<ulong>(field.Name!):X}",
        ClrElementType.Pointer => $"0x{obj.ReadField<ulong>(field.Name!):X}",
        _ => "?",
    };

    private static string ReadPrimitiveStatic(ClrStaticField field, ClrAppDomain domain) => field.ElementType switch
    {
        ClrElementType.Boolean => field.Read<bool>(domain).ToString(),
        ClrElementType.Char => ((int)field.Read<char>(domain)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.Int8 => field.Read<sbyte>(domain).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.UInt8 => field.Read<byte>(domain).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.Int16 => field.Read<short>(domain).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.UInt16 => field.Read<ushort>(domain).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.Int32 => field.Read<int>(domain).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.UInt32 => field.Read<uint>(domain).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.Int64 => field.Read<long>(domain).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.UInt64 => field.Read<ulong>(domain).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.Float => field.Read<float>(domain).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.Double => field.Read<double>(domain).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ClrElementType.NativeInt => $"0x{field.Read<long>(domain):X}",
        ClrElementType.NativeUInt => $"0x{field.Read<ulong>(domain):X}",
        ClrElementType.Pointer => $"0x{field.Read<ulong>(domain):X}",
        _ => "?",
    };

    private static string? ModuleOf(ClrMethod? method)
        => method?.Type?.Module?.Name is { Length: > 0 } path ? Path.GetFileName(path) : null;

    private static int IlOffsetAt(ClrMethod method, ulong ip)
    {
        foreach (var entry in method.ILOffsetMap)
        {
            if (ip >= entry.StartAddress && ip < entry.EndAddress && entry.ILOffset >= 0)
            {
                return entry.ILOffset;
            }
        }

        return -1;
    }

    private static string Quote(string text)
    {
        const int max = 120;
        string shown = text.Length > max ? text[..max] + "…" : text;
        return $"\"{shown}\"";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            try { _runtime?.Dispose(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            try { _target?.Dispose(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            _runtime = null;
            _target = null;
        }
    }
}
