using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace Spydate.Debugger.Managed;

/// <summary>The generic parameters a signature can refer to by position: the type's, and the method's.</summary>
internal readonly record struct GenericScope(TypeDefinitionHandle Type, MethodDefinitionHandle Method);

/// <summary>
/// Type names as a C# reader writes them, decoded from a metadata signature.
///
/// A signature is where the <em>declared</em> type lives — what a field, parameter or local was
/// written as — and that is a different fact from the runtime type of whatever it currently holds.
/// A field declared <c>IDictionary</c> holding a <c>HybridDictionary</c> is both, and the Type column
/// says both, the way dnSpy and Visual Studio do. A null has no runtime type at all, so without the
/// signature a null field could say only "object", which is how every one of them used to read.
///
/// Namespace-qualified, because a debugger's Type column is where "which <c>Window</c>?" gets
/// answered; C# keywords for the built-in types, because <c>System.Int32</c> is noise in a column of
/// them. Generic arity backticks are removed and instantiations written out: <c>List&lt;int&gt;</c>,
/// <c>int?</c>.
/// </summary>
internal sealed class ManagedTypeNames : ISignatureTypeProvider<string, GenericScope>
{
    private readonly MetadataReader _reader;

    internal ManagedTypeNames(MetadataReader reader) => _reader = reader;

    /// <summary>A type definition's name: namespace, declaring types, own name. No generic parameters.</summary>
    internal static string Definition(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        string name = Plain(reader.GetString(definition.Name));

        var declaring = definition.GetDeclaringType();
        var outermost = definition;
        while (!declaring.IsNil)
        {
            outermost = reader.GetTypeDefinition(declaring);
            name = Plain(reader.GetString(outermost.Name)) + "." + name;
            declaring = outermost.GetDeclaringType();
        }

        string space = reader.GetString(outermost.Namespace);
        return Keyword(space.Length > 0 ? space + "." + name : name);
    }

    /// <summary>
    /// A definition's name with its generic parameters written in — <c>List&lt;T&gt;</c> — for a runtime
    /// class, which is a definition and knows no instantiation.
    /// </summary>
    internal static string WithParameters(MetadataReader reader, TypeDefinitionHandle handle)
    {
        string name = Definition(reader, handle);
        var parameters = reader.GetTypeDefinition(handle).GetGenericParameters();
        if (parameters.Count == 0)
        {
            return name;
        }

        return $"{name}<{string.Join(", ", parameters.Select(p => reader.GetString(reader.GetGenericParameter(p).Name)))}>";
    }

    private static string Reference(MetadataReader reader, TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        string name = Plain(reader.GetString(reference.Name));

        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return Reference(reader, (TypeReferenceHandle)reference.ResolutionScope) + "." + name;
        }

        string space = reader.GetString(reference.Namespace);
        return Keyword(space.Length > 0 ? space + "." + name : name);
    }

    /// <summary>The C# keyword for a built-in type, which is how every reader of C# writes it.</summary>
    internal static string Keyword(string fullName) => fullName switch
    {
        "System.Object" => "object",
        "System.String" => "string",
        "System.Boolean" => "bool",
        "System.Char" => "char",
        "System.SByte" => "sbyte",
        "System.Byte" => "byte",
        "System.Int16" => "short",
        "System.UInt16" => "ushort",
        "System.Int32" => "int",
        "System.UInt32" => "uint",
        "System.Int64" => "long",
        "System.UInt64" => "ulong",
        "System.Single" => "float",
        "System.Double" => "double",
        "System.Decimal" => "decimal",
        "System.Void" => "void",
        _ => fullName,
    };

    /// <summary>Drops the backtick arity metadata adds to a generic type's name.</summary>
    internal static string Plain(string name)
    {
        int tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick > 0 ? name[..tick] : name;
    }

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.IntPtr => "System.IntPtr",
        PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.TypedReference => "System.TypedReference",
        _ => typeCode.ToString(),
    };

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        => Definition(reader, handle);

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        => Reference(reader, handle);

    public string GetTypeFromSpecification(MetadataReader reader, GenericScope genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetArrayType(string elementType, ArrayShape shape)
        => $"{elementType}[{new string(',', Math.Max(0, shape.Rank - 1))}]";

    public string GetByReferenceType(string elementType) => "ref " + elementType;

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetPinnedType(string elementType) => elementType;

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

    public string GetFunctionPointerType(MethodSignature<string> signature) => "method*";

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        => genericType == "System.Nullable" && typeArguments.Length == 1
            ? typeArguments[0] + "?"
            : $"{genericType}<{string.Join(", ", typeArguments)}>";

    public string GetGenericTypeParameter(GenericScope genericContext, int index)
    {
        if (!genericContext.Type.IsNil)
        {
            var parameters = _reader.GetTypeDefinition(genericContext.Type).GetGenericParameters();
            if (index < parameters.Count)
            {
                return _reader.GetString(_reader.GetGenericParameter(parameters[index]).Name);
            }
        }

        return "!" + index;
    }

    public string GetGenericMethodParameter(GenericScope genericContext, int index)
    {
        if (!genericContext.Method.IsNil)
        {
            var parameters = _reader.GetMethodDefinition(genericContext.Method).GetGenericParameters();
            if (index < parameters.Count)
            {
                return _reader.GetString(_reader.GetGenericParameter(parameters[index]).Name);
            }
        }

        return "!!" + index;
    }
}
