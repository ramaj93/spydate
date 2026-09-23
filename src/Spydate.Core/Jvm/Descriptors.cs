using System.Text;

namespace Spydate.Core.Jvm;

/// <summary>
/// Turns the JVM's type notation into the names a Java reader expects: <c>Ljava/lang/String;</c> into
/// <c>java.lang.String</c> (or <c>String</c> when short names are asked for), <c>[I</c> into <c>int[]</c>, and a
/// method descriptor into its parameter and return types. Generic signatures — the <c>Signature</c> attribute's
/// richer form — are read too. Nothing here throws: a malformed descriptor from a hostile file comes back as
/// written, or as null where the caller must know it failed.
/// </summary>
public static class Descriptors
{
    /// <summary><c>java/lang/String</c> as <c>java.lang.String</c>, or <c>String</c> when <paramref name="simple"/>.</summary>
    public static string ClassName(string internalName, bool simple = false)
    {
        ArgumentNullException.ThrowIfNull(internalName);
        if (internalName.StartsWith('['))
        {
            return TypeName(internalName, simple);   // an array class, as checkcast and anewarray can name
        }

        if (simple)
        {
            int slash = internalName.LastIndexOf('/');
            return slash < 0 ? internalName : internalName[(slash + 1)..];
        }

        return internalName.Replace('/', '.');
    }

    /// <summary>A field descriptor as a Java type, or the descriptor itself when it is not one.</summary>
    public static string TypeName(string descriptor, bool simple = false)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        int at = 0;
        return ReadType(descriptor, ref at, simple) is { } type && at == descriptor.Length ? type : descriptor;
    }

    /// <summary>A method descriptor's parameter and return types, or null when it is not one.</summary>
    public static (IReadOnlyList<string> Parameters, string Return)? Method(string descriptor, bool simple = false)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptor.StartsWith('('))
        {
            return null;
        }

        int at = 1;
        var parameters = new List<string>();
        while (at < descriptor.Length && descriptor[at] != ')')
        {
            if (ReadType(descriptor, ref at, simple) is not { } parameter)
            {
                return null;
            }

            parameters.Add(parameter);
        }

        if (at >= descriptor.Length)
        {
            return null;
        }

        at++;
        return ReadType(descriptor, ref at, simple) is { } returns && at == descriptor.Length ? (parameters, returns) : null;
    }

    /// <summary>
    /// A method descriptor's parameter types as descriptors (<c>I</c>, <c>Ljava/lang/String;</c>, <c>[J</c>), in
    /// order. Empty when it is not a method descriptor; as far as it parses when it is broken part-way.
    /// </summary>
    public static IReadOnlyList<string> ParameterDescriptors(string descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var parameters = new List<string>();
        if (!descriptor.StartsWith('('))
        {
            return parameters;
        }

        int at = 1;
        while (at < descriptor.Length && descriptor[at] != ')')
        {
            int start = at;
            if (ReadType(descriptor, ref at, simple: true) is null)
            {
                break;
            }

            parameters.Add(descriptor[start..at]);
        }

        return parameters;
    }

    /// <summary>How many local slots a method's parameters take: a long or double takes two.</summary>
    public static int ParameterSlots(string descriptor)
    {
        int slots = 0;
        int at = 1;
        while (at < descriptor.Length && descriptor[at] != ')')
        {
            char c = descriptor[at];
            int start = at;
            if (ReadType(descriptor, ref at, simple: true) is null)
            {
                return slots;
            }

            slots += c is 'J' or 'D' && at == start + 1 ? 2 : 1;
        }

        return slots;
    }

    private static string? ReadType(string text, ref int at, bool simple)
    {
        if (at >= text.Length)
        {
            return null;
        }

        char c = text[at++];
        switch (c)
        {
            case 'B': return "byte";
            case 'C': return "char";
            case 'D': return "double";
            case 'F': return "float";
            case 'I': return "int";
            case 'J': return "long";
            case 'S': return "short";
            case 'Z': return "boolean";
            case 'V': return "void";
            case '[':
                // 255 dimensions is the JVM's own limit; more is a crafted descriptor, not an array.
                int dims = 1;
                while (at < text.Length && text[at] == '[' && dims < 255)
                {
                    dims++;
                    at++;
                }

                return ReadType(text, ref at, simple) is { } element ? element + string.Concat(Enumerable.Repeat("[]", dims)) : null;
            case 'L':
                int end = text.IndexOf(';', at);
                if (end < 0)
                {
                    return null;
                }

                string name = text[at..end];
                at = end + 1;
                return ClassName(name, simple);
            default:
                return null;
        }
    }

    // --- generic signatures -------------------------------------------------

    /// <summary>A field's generic signature as Java writes it: <c>java.util.List&lt;java.lang.String&gt;</c>. Null when malformed.</summary>
    public static string? FieldSignature(string signature, bool simple = false)
    {
        var parser = new SignatureParser(signature, simple);
        return parser.ReferenceType() is { } type && parser.AtEnd ? type : null;
    }

    /// <summary>
    /// A method's generic signature: its type parameters (<c>&lt;T extends Comparable&lt;T&gt;&gt;</c>, or empty),
    /// its parameter types, return type and thrown types. Null when malformed.
    /// </summary>
    public static (string TypeParameters, IReadOnlyList<string> Parameters, string Return, IReadOnlyList<string> Throws)? MethodSignature(string signature, bool simple = false)
    {
        var parser = new SignatureParser(signature, simple);
        string? typeParameters = parser.TypeParameters();
        if (typeParameters is null || !parser.Take('('))
        {
            return null;
        }

        var parameters = new List<string>();
        while (!parser.AtEnd && !parser.Peek(')'))
        {
            if (parser.JavaType() is not { } parameter)
            {
                return null;
            }

            parameters.Add(parameter);
        }

        if (!parser.Take(')') || parser.JavaType() is not { } returns)
        {
            return null;
        }

        var throws = new List<string>();
        while (parser.Take('^'))
        {
            if (parser.ReferenceType() is not { } thrown)
            {
                return null;
            }

            throws.Add(thrown);
        }

        return parser.AtEnd ? (typeParameters, parameters, returns, throws) : null;
    }

    /// <summary>A class's generic signature: its type parameters, superclass and interfaces. Null when malformed.</summary>
    public static (string TypeParameters, string Super, IReadOnlyList<string> Interfaces)? ClassSignature(string signature, bool simple = false)
    {
        var parser = new SignatureParser(signature, simple);
        string? typeParameters = parser.TypeParameters();
        if (typeParameters is null || parser.ReferenceType() is not { } super)
        {
            return null;
        }

        var interfaces = new List<string>();
        while (!parser.AtEnd)
        {
            if (parser.ReferenceType() is not { } type)
            {
                return null;
            }

            interfaces.Add(type);
        }

        return (typeParameters, super, interfaces);
    }

    /// <summary>A recursive-descent reader of JVMS §4.7.9.1 signatures, bounded so a crafted one cannot recurse without end.</summary>
    private sealed class SignatureParser(string text, bool simple)
    {
        private const int MaxDepth = 64;
        private int _at;
        private int _depth;

        public bool AtEnd => _at >= text.Length;

        public bool Peek(char c) => _at < text.Length && text[_at] == c;

        public bool Take(char c)
        {
            if (!Peek(c))
            {
                return false;
            }

            _at++;
            return true;
        }

        /// <summary><c>&lt;T:Ljava/lang/Object;&gt;</c> as <c>&lt;T&gt;</c>; empty when there are none; null when malformed.</summary>
        public string? TypeParameters()
        {
            if (!Take('<'))
            {
                return string.Empty;
            }

            var parts = new List<string>();
            while (!Take('>'))
            {
                int colon = text.IndexOf(':', _at);
                if (colon <= _at)
                {
                    return null;
                }

                string name = text[_at..colon];
                _at = colon;
                var bounds = new List<string>();
                while (Take(':'))
                {
                    if (Peek(':'))
                    {
                        continue;   // an empty class bound: only interface bounds follow
                    }

                    if (ReferenceType() is not { } bound)
                    {
                        return null;
                    }

                    if (bound is not ("java.lang.Object" or "Object"))
                    {
                        bounds.Add(bound);
                    }
                }

                parts.Add(bounds.Count == 0 ? name : $"{name} extends {string.Join(" & ", bounds)}");
            }

            return $"<{string.Join(", ", parts)}>";
        }

        public string? JavaType()
        {
            if (AtEnd)
            {
                return null;
            }

            char c = text[_at];
            if ("BCDFIJSZV".Contains(c, StringComparison.Ordinal))
            {
                int at = _at;
                string? type = TypeNameAt(ref at);
                _at = at;
                return type;
            }

            return ReferenceType();
        }

        public string? ReferenceType()
        {
            if (AtEnd || ++_depth > MaxDepth)
            {
                return null;
            }

            try
            {
                switch (text[_at])
                {
                    case 'T':
                        int end = text.IndexOf(';', _at);
                        if (end < 0)
                        {
                            return null;
                        }

                        string variable = text[(_at + 1)..end];
                        _at = end + 1;
                        return variable;
                    case '[':
                        _at++;
                        return JavaType() is { } element ? element + "[]" : null;
                    case 'L':
                        _at++;
                        return ClassType();
                    default:
                        return null;
                }
            }
            finally
            {
                _depth--;
            }
        }

        /// <summary>After the <c>L</c>: a name, type arguments, then any <c>.Inner&lt;...&gt;</c> parts, up to the <c>;</c>.</summary>
        private string? ClassType()
        {
            var sb = new StringBuilder();
            int start = _at;
            while (!AtEnd && text[_at] is not ('<' or ';' or '.'))
            {
                _at++;
            }

            sb.Append(Descriptors.ClassName(text[start.._at], simple));
            while (!AtEnd)
            {
                if (Take(';'))
                {
                    return sb.ToString();
                }

                if (Take('<'))
                {
                    var arguments = new List<string>();
                    while (!Take('>'))
                    {
                        if (TypeArgument() is not { } argument)
                        {
                            return null;
                        }

                        arguments.Add(argument);
                    }

                    sb.Append('<').Append(string.Join(", ", arguments)).Append('>');
                }
                else if (Take('.'))
                {
                    int inner = _at;
                    while (!AtEnd && text[_at] is not ('<' or ';' or '.'))
                    {
                        _at++;
                    }

                    sb.Append('.').Append(text, inner, _at - inner);
                }
                else
                {
                    return null;
                }
            }

            return null;
        }

        private string? TypeArgument()
        {
            if (Take('*'))
            {
                return "?";
            }

            if (Take('+'))
            {
                return ReferenceType() is { } upper ? $"? extends {upper}" : null;
            }

            if (Take('-'))
            {
                return ReferenceType() is { } lower ? $"? super {lower}" : null;
            }

            return ReferenceType();
        }

        private string? TypeNameAt(ref int at) => ReadType(text, ref at, simple);
    }
}
