using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Spydate.Debugger.Managed;

/// <summary>One field of a type, as the runtime will be asked for it.</summary>
internal readonly record struct ManagedField(string Name, uint Token);

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

                found.Add(new ManagedField(Readable(reader.GetString(field.Name)), (uint)MetadataTokens.GetToken(handle)));
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
