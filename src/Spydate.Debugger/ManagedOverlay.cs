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
    private readonly object _gate = new();
    private DataTarget? _target;
    private ClrRuntime? _runtime;
    private volatile bool _moved;
    private bool _disposed;

    public ManagedOverlay(uint pid) => _pid = (int)pid;

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
                if (_moved)
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
