using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Spydate.Debugger.Managed;

/// <summary>One field of a type, as the runtime will be asked for it.</summary>
internal readonly record struct ManagedField(string Name, uint Token, string Type = "");

/// <summary>One readable property of a type: its getter is run to find its value.</summary>
internal readonly record struct ManagedProperty(string Name, uint GetterToken, bool IsStatic, string Type);

/// <summary>A method as its metadata declares it: whether it has a <c>this</c>, and what its parameters are called.</summary>
internal sealed record ManagedMethodShape(
    bool IsStatic,
    string DeclaringType,
    string Name,
    IReadOnlyList<string> ParameterNames,
    IReadOnlyList<string> ParameterTypes)
{
    /// <summary><c>CSProApp.Main.App.OnStartup(System.Windows.StartupEventArgs)</c>.</summary>
    internal string Display => $"{DeclaringType}.{Name}({string.Join(", ", ParameterTypes)})";
}

/// <summary>
/// Names for the types the debuggee is holding.
///
/// The runtime says what a value <em>is</em> only as a module and a metadata token: an object is
/// "token 0x02000007 of spydate-mcp.dll", which is exactly as useful to a reader as the pointer it
/// replaces. The name lives in that module's metadata, and the ordinary way to read it from a
/// debugger is <c>IMetaDataImport</c> — sixty-odd methods that have to be declared in vtable order
/// to call four of them, each with its own wide-character buffer protocol.
///
/// The module is a file on disk, though, and the file has the same metadata in it. So this opens it
/// and reads the name with <c>System.Reflection.Metadata</c>, which is in the framework, correct by
/// construction, and about thirty lines. A module with no file behind it — built in memory, or
/// bundled into a single-file host — simply has no name here, and a value of that type keeps saying
/// what it said before.
/// </summary>
internal sealed class ManagedTypes : IDisposable
{
    private readonly Dictionary<string, MetadataReader?> _byModule = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PEReader> _open = new();
    private readonly Lock _gate = new();

    /// <summary>The name of a type, or null when this module's metadata is not readable.</summary>
    internal string? Name(string? module, uint typeDefToken)
    {
        if (Reader(module) is not { } reader)
        {
            return null;
        }

        try
        {
            return Named(reader, typeDefToken);
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The instance fields a type declares, in metadata order.
    ///
    /// Its own only: a field inherited from a base class has to be read through the class that
    /// declares it, and asking the wrong class for it is an error rather than a wrong answer. What
    /// the reader wants first is what this type added, which is what this returns.
    /// </summary>
    /// <summary>
    /// The readable properties a type declares: name, the getter's token, whether it is static, and
    /// its type. Its own only, like <see cref="Fields"/>.
    ///
    /// Getters that take a parameter — indexers — are left out: there is no single value to show for
    /// <c>this[int i]</c>, only a value per index nobody has asked for. A property with no getter (a
    /// set-only one, which is rare) has nothing to read and is left out too.
    /// </summary>
    internal IReadOnlyList<ManagedProperty> Properties(string? module, uint typeDefToken)
        => Reading<IReadOnlyList<ManagedProperty>>(module, reader =>
        {
            var definition = reader.GetTypeDefinition(Handle(typeDefToken));
            var scope = new GenericScope(Handle(typeDefToken), default);
            var found = new List<ManagedProperty>();

            foreach (var handle in definition.GetProperties())
            {
                var property = reader.GetPropertyDefinition(handle);
                var getter = property.GetAccessors().Getter;
                if (getter.IsNil)
                {
                    continue;
                }

                var method = reader.GetMethodDefinition(getter);
                var signature = method.DecodeSignature(new ManagedTypeNames(reader), scope);
                if (signature.ParameterTypes.Length > 0)
                {
                    continue;
                }

                found.Add(new ManagedProperty(
                    reader.GetString(property.Name),
                    (uint)MetadataTokens.GetToken(getter),
                    !signature.Header.IsInstance,
                    signature.ReturnType));
            }

            return found;
        }) ?? Array.Empty<ManagedProperty>();

    internal IReadOnlyList<ManagedField> Fields(string? module, uint typeDefToken, int limit)
    {
        if (Reader(module) is not { } reader)
        {
            return Array.Empty<ManagedField>();
        }

        try
        {
            var definition = reader.GetTypeDefinition(Handle(typeDefToken));
            var found = new List<ManagedField>();

            foreach (var handle in definition.GetFields())
            {
                var field = reader.GetFieldDefinition(handle);
                if ((field.Attributes & FieldAttributes.Static) != 0)
                {
                    continue;
                }

                found.Add(new ManagedField(
                    Readable(reader.GetString(field.Name)),
                    (uint)MetadataTokens.GetToken(handle),
                    Declared(() => field.DecodeSignature(new ManagedTypeNames(reader), new GenericScope(Handle(typeDefToken), default)))));
                if (found.Count == limit)
                {
                    break;
                }
            }

            return found;
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return Array.Empty<ManagedField>();
        }
    }

    /// <summary>
    /// A type's full name — namespace, declaring types, generic parameters — or null when the
    /// module's metadata is not readable. What a Type column shows for a runtime class.
    /// </summary>
    internal string? FullName(string? module, uint typeDefToken)
        => Reading(module, reader => ManagedTypeNames.WithParameters(reader, Handle(typeDefToken)));

    /// <summary>
    /// A method's shape: static or not, its declaring type, and its parameters' names and declared
    /// types. The names are what turn "arg 1" into <c>why</c>; whether it is static is what says
    /// argument zero is <c>this</c> — an instance method's <c>this</c> has no row in the Param table
    /// at all, so counting from the table puts every name one slot out.
    /// </summary>
    internal ManagedMethodShape? Method(string? module, uint methodToken)
        => Reading(module, reader =>
        {
            var handle = MetadataTokens.MethodDefinitionHandle((int)(methodToken & 0x00FFFFFF));
            var method = reader.GetMethodDefinition(handle);
            var owner = method.GetDeclaringType();
            var signature = method.DecodeSignature(new ManagedTypeNames(reader), new GenericScope(owner, handle));

            var names = new string[signature.ParameterTypes.Length];
            foreach (var parameterHandle in method.GetParameters())
            {
                var parameter = reader.GetParameter(parameterHandle);
                if (parameter.SequenceNumber >= 1 && parameter.SequenceNumber <= names.Length)
                {
                    names[parameter.SequenceNumber - 1] = reader.GetString(parameter.Name);
                }
            }

            for (int i = 0; i < names.Length; i++)
            {
                // Unnamed — obfuscators do this — gets the name ILSpy gives it, so this pane and the
                // C# view agree on what to call it.
                if (string.IsNullOrEmpty(names[i]))
                {
                    names[i] = "A_" + (i + (signature.Header.IsInstance ? 1 : 0)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            string name = reader.GetString(method.Name);
            if (name == ".ctor")
            {
                name = ManagedTypeNames.Plain(reader.GetString(reader.GetTypeDefinition(owner).Name));
            }

            return new ManagedMethodShape(
                !signature.Header.IsInstance,
                ManagedTypeNames.Definition(reader, owner),
                name,
                names,
                signature.ParameterTypes);
        });

    /// <summary>
    /// The declared types of a method's locals, from its local signature — which the runtime hands
    /// over as a token, and which is the only place a null local's type is written down.
    /// </summary>
    internal IReadOnlyList<string> LocalTypes(string? module, uint methodToken, uint localSignatureToken)
    {
        if ((localSignatureToken & 0x00FFFFFF) == 0)
        {
            return Array.Empty<string>();
        }

        return Reading<IReadOnlyList<string>>(module, reader =>
        {
            var method = MetadataTokens.MethodDefinitionHandle((int)(methodToken & 0x00FFFFFF));
            var signature = MetadataTokens.StandaloneSignatureHandle((int)(localSignatureToken & 0x00FFFFFF));
            var scope = new GenericScope(reader.GetMethodDefinition(method).GetDeclaringType(), method);
            return reader.GetStandaloneSignature(signature).DecodeLocalSignature(new ManagedTypeNames(reader), scope);
        }) ?? Array.Empty<string>();
    }

    /// <summary>Whether a type is an enum, which reads as a member name rather than as a struct.</summary>
    internal bool IsEnum(string? module, uint typeDefToken)
        => Reading<object>(module, reader => IsEnum(reader, reader.GetTypeDefinition(Handle(typeDefToken))) ? true : null) is not null;

    /// <summary>
    /// An enum value as its member name — <c>OnLastWindowClose</c>, not <c>1</c> — or several joined
    /// with <c>|</c> for a [Flags] enum, or the number when no member fits. Null when it is not an enum.
    /// </summary>
    internal string? EnumText(string? module, uint typeDefToken, ulong value)
        => Reading(module, reader =>
        {
            var definition = reader.GetTypeDefinition(Handle(typeDefToken));
            if (!IsEnum(reader, definition))
            {
                return null;
            }

            var members = new List<(string Name, ulong Value)>();
            foreach (var handle in definition.GetFields())
            {
                var field = reader.GetFieldDefinition(handle);
                if ((field.Attributes & FieldAttributes.Literal) == 0 || field.GetDefaultValue().IsNil)
                {
                    continue;
                }

                if (Constant(reader, field.GetDefaultValue()) is { } constant)
                {
                    members.Add((reader.GetString(field.Name), constant));
                }
            }

            foreach (var member in members)
            {
                if (member.Value == value)
                {
                    return member.Name;
                }
            }

            if (IsFlags(reader, definition) && value != 0)
            {
                var parts = new List<string>();
                ulong left = value;
                foreach (var member in members.Where(m => m.Value != 0).OrderByDescending(m => m.Value))
                {
                    if ((left & member.Value) == member.Value)
                    {
                        parts.Add(member.Name);
                        left ^= member.Value;
                    }
                }

                if (left == 0 && parts.Count > 0)
                {
                    parts.Reverse();
                    return string.Join(" | ", parts);
                }
            }

            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        });

    private static bool IsEnum(MetadataReader reader, TypeDefinition definition)
    {
        var baseType = definition.BaseType;
        switch (baseType.Kind)
        {
            case HandleKind.TypeReference:
                var reference = reader.GetTypeReference((TypeReferenceHandle)baseType);
                return reader.GetString(reference.Name) == "Enum" && reader.GetString(reference.Namespace) == "System";

            case HandleKind.TypeDefinition:
                var local = reader.GetTypeDefinition((TypeDefinitionHandle)baseType);
                return reader.GetString(local.Name) == "Enum" && reader.GetString(local.Namespace) == "System";

            default:
                return false;
        }
    }

    private static bool IsFlags(MetadataReader reader, TypeDefinition definition)
    {
        foreach (var handle in definition.GetCustomAttributes())
        {
            var constructor = reader.GetCustomAttribute(handle).Constructor;
            if (constructor.Kind != HandleKind.MemberReference)
            {
                continue;
            }

            var parent = reader.GetMemberReference((MemberReferenceHandle)constructor).Parent;
            if (parent.Kind == HandleKind.TypeReference
                && reader.GetString(reader.GetTypeReference((TypeReferenceHandle)parent).Name) == "FlagsAttribute")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>An enum member's constant, zero-extended from its own width so it compares with a value read the same way.</summary>
    private static ulong? Constant(MetadataReader reader, ConstantHandle handle)
    {
        var constant = reader.GetConstant(handle);
        var blob = reader.GetBlobReader(constant.Value);
        return constant.TypeCode switch
        {
            ConstantTypeCode.Boolean or ConstantTypeCode.Byte or ConstantTypeCode.SByte => blob.ReadByte(),
            ConstantTypeCode.Char or ConstantTypeCode.Int16 or ConstantTypeCode.UInt16 => blob.ReadUInt16(),
            ConstantTypeCode.Int32 or ConstantTypeCode.UInt32 => blob.ReadUInt32(),
            ConstantTypeCode.Int64 or ConstantTypeCode.UInt64 => blob.ReadUInt64(),
            _ => null,
        };
    }

    /// <summary>A declared type, or empty when its signature will not decode.</summary>
    private static string Declared(Func<string> decode)
    {
        try
        {
            return decode();
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    /// <summary>Runs a read against a module's metadata, turning a malformed image into "no answer".</summary>
    private T? Reading<T>(string? module, Func<MetadataReader, T?> read)
        where T : class
    {
        if (Reader(module) is not { } reader)
        {
            return null;
        }

        try
        {
            return read(reader);
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentOutOfRangeException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// A field name as the source had it.
    ///
    /// An auto-property is a field called <c>&lt;Root&gt;k__BackingField</c>, and a closure's
    /// captured variable is <c>&lt;&gt;4__this</c>: the compiler picks names that cannot collide
    /// with anything a person could write, and the part between the angle brackets is what the
    /// person did write. A row of backing-field names reads as noise and hides the five characters
    /// that matter.
    /// </summary>
    private static string Readable(string name)
    {
        if (name.Length == 0 || name[0] != '<')
        {
            return name;
        }

        int close = name.IndexOf('>', StringComparison.Ordinal);
        return close > 1 ? name[1..close] : name;
    }

    private static TypeDefinitionHandle Handle(uint token)
        => MetadataTokens.TypeDefinitionHandle((int)(token & 0x00FFFFFF));

    /// <summary>A type's name, with its declaring types in front of it and the arity taken off.</summary>
    private static string Named(MetadataReader reader, uint token)
    {
        var definition = reader.GetTypeDefinition(Handle(token));
        string name = Plain(reader.GetString(definition.Name));

        // A nested type's own name says nothing on its own: `Enumerator` could be anybody's.
        var declaring = definition.GetDeclaringType();
        while (!declaring.IsNil)
        {
            var outer = reader.GetTypeDefinition(declaring);
            name = Plain(reader.GetString(outer.Name)) + "." + name;
            declaring = outer.GetDeclaringType();
        }

        return name;
    }

    /// <summary>Drops the backtick arity metadata adds to a generic type's name.</summary>
    private static string Plain(string name)
    {
        int tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick > 0 ? name[..tick] : name;
    }

    /// <summary>
    /// The metadata of a module, opened once and kept.
    ///
    /// A failure is cached as well as a success. Values are read a slot at a time and the same
    /// module comes up for most of them, so a module without a file would otherwise be opened,
    /// failed and thrown away on every local of every stop.
    /// </summary>
    private MetadataReader? Reader(string? module)
    {
        if (string.IsNullOrWhiteSpace(module))
        {
            return null;
        }

        lock (_gate)
        {
            if (_byModule.TryGetValue(module, out var already))
            {
                return already;
            }

            MetadataReader? reader = null;
            try
            {
                if (File.Exists(module))
                {
                    var pe = new PEReader(File.OpenRead(module), PEStreamOptions.PrefetchMetadata);
                    _open.Add(pe);
                    reader = pe.HasMetadata ? pe.GetMetadataReader() : null;
                }
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                reader = null;
            }

            _byModule[module] = reader;
            return reader;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var pe in _open)
            {
                pe.Dispose();
            }

            _open.Clear();
            _byModule.Clear();
        }
    }
}
