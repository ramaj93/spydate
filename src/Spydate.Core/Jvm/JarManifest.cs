namespace Spydate.Core.Jvm;

/// <summary>
/// A JAR's <c>META-INF/MANIFEST.MF</c>: the main section's attributes in the order written, and how many
/// per-entry sections follow. The main section is what says how the archive runs — <c>Main-Class</c>,
/// <c>Class-Path</c> — and who built it.
/// </summary>
public sealed class JarManifest
{
    private JarManifest(IReadOnlyList<KeyValuePair<string, string>> main, int entrySections)
    {
        Main = main;
        EntrySections = entrySections;
    }

    public IReadOnlyList<KeyValuePair<string, string>> Main { get; }

    /// <summary>Sections after the main one, each naming an entry — usually signature digests.</summary>
    public int EntrySections { get; }

    /// <summary>A main attribute's value, matched case-insensitively as the JAR specification says; null when absent.</summary>
    public string? this[string name] => Main.FirstOrDefault(a => string.Equals(a.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>The class <c>java -jar</c> runs, as an internal name with slashes, or null.</summary>
    public string? MainClass => this["Main-Class"]?.Trim().Replace('.', '/') is { Length: > 0 } name ? name : null;

    /// <summary>The other JARs it expects beside it, as <c>Class-Path</c> lists them.</summary>
    public IReadOnlyList<string> ClassPath => this["Class-Path"] is { } path
        ? path.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : [];

    /// <summary>
    /// Parses a manifest. Lines longer than 72 bytes continue on lines that start with one space; a blank line
    /// ends a section. Anything that is not <c>Name: value</c> is skipped, so a damaged manifest still says what
    /// it can.
    /// </summary>
    public static JarManifest Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var main = new List<KeyValuePair<string, string>>();
        int sections = 0;
        bool inMain = true;
        bool sectionHasContent = false;
        string? key = null;
        var value = new System.Text.StringBuilder();

        void Flush()
        {
            if (key is not null && inMain)
            {
                main.Add(new KeyValuePair<string, string>(key, value.ToString()));
            }

            key = null;
            value.Clear();
        }

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith(' '))
            {
                if (key is not null)
                {
                    value.Append(line, 1, line.Length - 1);
                }

                continue;
            }

            Flush();
            if (line.Length == 0)
            {
                if (sectionHasContent)
                {
                    if (!inMain)
                    {
                        sections++;
                    }

                    inMain = false;
                    sectionHasContent = false;
                }

                continue;
            }

            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            sectionHasContent = true;
            key = line[..colon];
            value.Append(line, colon + 2, line.Length - colon - 2);
        }

        Flush();
        if (!inMain && sectionHasContent)
        {
            sections++;
        }

        return new JarManifest(main, sections);
    }
}
