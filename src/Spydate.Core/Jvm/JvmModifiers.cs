namespace Spydate.Core.Jvm;

/// <summary>
/// Access flags as the Java keywords they stand for, per kind of thing — the same bit is <c>synchronized</c> on a
/// method and <c>ACC_SUPER</c> on a class — plus the flags no keyword spells (synthetic, bridge) as words for a
/// listing to note.
/// </summary>
public static class JvmModifiers
{
    public static string Class(JvmAccess access, bool nested)
    {
        var words = new List<string>();
        Visibility(access, words);
        if (nested && (access & JvmAccess.Static) != 0)
        {
            words.Add("static");
        }

        bool isInterface = (access & JvmAccess.Interface) != 0;
        if ((access & JvmAccess.Abstract) != 0 && !isInterface)
        {
            words.Add("abstract");
        }

        if ((access & JvmAccess.Final) != 0 && (access & JvmAccess.Enum) == 0)
        {
            words.Add("final");
        }

        return string.Join(' ', words);
    }

    /// <summary>The declaration keyword: <c>class</c>, <c>interface</c>, <c>@interface</c>, <c>enum</c>, <c>record</c>.</summary>
    public static string ClassKeyword(JvmAccess access, string? superName) =>
        (access & JvmAccess.Annotation) != 0 ? "@interface"
        : (access & JvmAccess.Interface) != 0 ? "interface"
        : (access & JvmAccess.Enum) != 0 ? "enum"
        : superName == "java/lang/Record" ? "record"
        : (access & JvmAccess.ModuleOrMandated) != 0 ? "module"
        : "class";

    public static string Field(JvmAccess access)
    {
        var words = new List<string>();
        Visibility(access, words);
        Add(access, JvmAccess.Static, "static", words);
        Add(access, JvmAccess.Final, "final", words);
        Add(access, JvmAccess.VolatileOrBridge, "volatile", words);
        Add(access, JvmAccess.TransientOrVarargs, "transient", words);
        return string.Join(' ', words);
    }

    public static string Method(JvmAccess access)
    {
        var words = new List<string>();
        Visibility(access, words);
        Add(access, JvmAccess.Static, "static", words);
        Add(access, JvmAccess.Final, "final", words);
        Add(access, JvmAccess.SuperOrSynchronized, "synchronized", words);
        Add(access, JvmAccess.Native, "native", words);
        Add(access, JvmAccess.Abstract, "abstract", words);
        Add(access, JvmAccess.Strict, "strictfp", words);
        return string.Join(' ', words);
    }

    /// <summary>What the compiler added that no keyword says: <c>synthetic</c>, <c>bridge</c>, <c>varargs</c>, <c>enum constant</c>.</summary>
    public static IReadOnlyList<string> Notes(JvmAccess access, bool isMethod)
    {
        var words = new List<string>();
        Add(access, JvmAccess.Synthetic, "synthetic", words);
        if (isMethod)
        {
            Add(access, JvmAccess.VolatileOrBridge, "bridge", words);
            Add(access, JvmAccess.TransientOrVarargs, "varargs", words);
            Add(access, JvmAccess.ModuleOrMandated, "mandated", words);
        }
        else
        {
            Add(access, JvmAccess.Enum, "enum constant", words);
        }

        return words;
    }

    private static void Visibility(JvmAccess access, List<string> words)
    {
        Add(access, JvmAccess.Public, "public", words);
        Add(access, JvmAccess.Protected, "protected", words);
        Add(access, JvmAccess.Private, "private", words);
    }

    private static void Add(JvmAccess access, JvmAccess flag, string word, List<string> words)
    {
        if ((access & flag) != 0)
        {
            words.Add(word);
        }
    }
}
