using System.Text;
using System.Text.RegularExpressions;
using Spydate.Core.Jvm;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// Class names in printed Java, decided once the whole class is printed. While printing, a class name is a token
/// (<see cref="Token"/>) holding its internal name; at the end every token becomes the name Java needs there:
/// the simple name for <c>java.lang</c>, the class's own package, a class nested in the one being printed, or an
/// imported class; the qualified name when two classes would claim the same simple name. A member printed on its
/// own gets no import section, so everything outside <c>java.lang</c> and its package stays qualified.
/// </summary>
internal sealed partial class JavaImports
{
    private const char Open = '\u0001';
    private const char Close = '\u0002';

    private readonly Func<string, JvmType?> _find;

    public JavaImports(Func<string, JvmType?> find) => _find = find;

    public static string Token(string internalName) => $"{Open}{internalName}{Close}";

    [GeneratedRegex("\u0001([^\u0001\u0002]*)\u0002")]
    private static partial Regex TokenPattern();

    /// <summary>
    /// The text with its tokens resolved. With <paramref name="imports"/>, classes outside <c>java.lang</c> and the
    /// package are imported when their simple name is unambiguous, and the import lines are returned.
    /// </summary>
    public (string Text, IReadOnlyList<string> Imports) Resolve(string text, string package, string? topClass, bool imports)
    {
        var names = TokenPattern().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
        var display = new Dictionary<string, string>(StringComparer.Ordinal);
        var imported = new SortedSet<string>(StringComparer.Ordinal);

        // Who claims each simple name: the class being printed and its members, then java.lang and the package, then imports.
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
        if (topClass is not null)
        {
            claimed[Simple(topClass)] = topClass;
            foreach (string name in names.Where(n => Top(n) == topClass && n != topClass))
            {
                claimed.TryAdd(Inside(name, topClass).Split('.')[0], name);
            }
        }

        foreach (string name in names.Select(Top).Distinct(StringComparer.Ordinal))
        {
            string pkg = PackageOf(name);
            if (pkg == "java/lang" || pkg == package)
            {
                claimed.TryAdd(Simple(name), name);
            }
        }

        if (imports)
        {
            foreach (var group in names.Select(Top).Distinct(StringComparer.Ordinal).GroupBy(Simple).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                if (claimed.ContainsKey(group.Key))
                {
                    continue;
                }

                // One class per simple name is imported; any other keeps its package.
                string chosen = group.OrderBy(n => n, StringComparer.Ordinal).First();
                if (PackageOf(chosen).Length > 0)
                {
                    claimed[group.Key] = chosen;
                    imported.Add(chosen.Replace('/', '.'));
                }
            }
        }

        foreach (string name in names)
        {
            string top = Top(name);
            if (topClass is not null && top == topClass)
            {
                // Inside the class being printed, its members — and its local classes, which cannot be qualified — go by their own names.
                display[name] = name == topClass ? Simple(topClass) : Inside(name, topClass);
                continue;
            }

            string relative = Simple(top) + Path(name, top)[Simple(top).Length..];
            display[name] = claimed.TryGetValue(Simple(top), out var owner) && owner == top
                ? relative
                : PackageOf(top).Length == 0 ? relative : $"{PackageOf(top).Replace('/', '.')}.{relative}";
        }

        string resolved = TokenPattern().Replace(text, m => display.TryGetValue(m.Groups[1].Value, out var d) ? d : m.Groups[1].Value);
        return (resolved, imported.ToList());
    }

    /// <summary>The top-level class a class is nested in (itself when it is top-level), from the JAR's nesting or the name.</summary>
    private string Top(string name)
    {
        if (_find(name) is { } type)
        {
            while (type.Outer is { } outer)
            {
                type = outer;
            }

            return type.File.Name;
        }

        int dollar = NestingDollar(name);
        return dollar < 0 ? name : name[..dollar];
    }

    /// <summary><c>Outer.Inner.Deeper</c> for a class nested in <paramref name="top"/>, down from the top's simple name.</summary>
    private string Path(string name, string top)
    {
        var parts = new List<string>();
        if (_find(name) is { } type)
        {
            for (var at = type; at is not null && at.File.Name != top; at = at.Outer)
            {
                parts.Insert(0, at.Nesting?.SimpleName ?? Simple(at.File.Name));
            }
        }
        else if (name.Length > top.Length)
        {
            parts.AddRange(name[(top.Length + 1)..].Split('$'));
        }

        return parts.Count == 0 ? Simple(top) : $"{Simple(top)}.{string.Join('.', parts)}";
    }

    /// <summary>The path below <paramref name="top"/>: <c>Color</c>, <c>Color.Shade</c>.</summary>
    private string Inside(string name, string top)
    {
        string path = Path(name, top);
        string prefix = Simple(top) + ".";
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
    }

    private static int NestingDollar(string name)
    {
        int slash = name.LastIndexOf('/');
        for (int i = slash + 2; i < name.Length - 1; i++)
        {
            if (name[i] == '$' && !char.IsDigit(name[i + 1]))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Simple(string name)
    {
        int slash = name.LastIndexOf('/');
        return slash < 0 ? name : name[(slash + 1)..];
    }

    private static string PackageOf(string name)
    {
        int slash = name.LastIndexOf('/');
        return slash < 0 ? string.Empty : name[..slash];
    }

    /// <summary>The import section: one line per class, in order.</summary>
    public static string Section(IReadOnlyList<string> imports)
    {
        if (imports.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (string import in imports)
        {
            sb.Append("import ").Append(import).Append(";\n");
        }

        return sb.Append('\n').ToString();
    }
}
