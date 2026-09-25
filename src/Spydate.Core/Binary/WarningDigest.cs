using System.Globalization;
using System.Text;

namespace Spydate.Core.Binary;

/// <summary>
/// A parser's warnings, cut down to what a person can read. A damaged or unusual file can produce the same complaint
/// for every method it holds — tens of thousands of lines that say one thing — and a view that lays out a line for
/// each of them stalls the window. The digest keeps the first warning of each kind and says how many more there were.
/// </summary>
public static class WarningDigest
{
    /// <summary>
    /// At most <paramref name="limit"/> lines: warnings in the order they came, one per kind — the same text once the
    /// numbers and the member it is about are set aside — each followed by the count of the others like it, and a last
    /// line for the kinds left out.
    /// </summary>
    public static IReadOnlyList<string> Summarize(IReadOnlyList<string> warnings, int limit = 100)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        if (warnings.Count <= limit)
        {
            return warnings;
        }

        var order = new List<string>();
        var groups = new Dictionary<string, (string First, int Count)>(StringComparer.Ordinal);
        foreach (string warning in warnings)
        {
            string kind = Kind(warning);
            if (groups.TryGetValue(kind, out var group))
            {
                groups[kind] = (group.First, group.Count + 1);
            }
            else
            {
                groups[kind] = (warning, 1);
                order.Add(kind);
            }
        }

        var lines = new List<string>();
        foreach (string kind in order.Take(limit - 1))
        {
            var (first, count) = groups[kind];
            lines.Add(count == 1 ? first : string.Create(CultureInfo.InvariantCulture, $"{first}  (and {count - 1:N0} more like it)"));
        }

        if (order.Count > limit - 1)
        {
            int rest = order.Skip(limit - 1).Sum(k => groups[k].Count);
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"… and {rest:N0} more warnings of {order.Count - (limit - 1):N0} other kinds."));
        }

        return lines;
    }

    /// <summary>
    /// What a warning says with its particulars set aside: the text after its last location prefix
    /// (<c>classes.dex: A0.a: </c>), with every number replaced.
    /// </summary>
    private static string Kind(string warning)
    {
        int colon = warning.LastIndexOf(": ", StringComparison.Ordinal);
        string message = colon >= 0 && colon + 2 < warning.Length ? warning[(colon + 2)..] : warning;
        var sb = new StringBuilder(message.Length);
        bool inNumber = false;
        foreach (char c in message)
        {
            if (char.IsAsciiDigit(c))
            {
                if (!inNumber)
                {
                    sb.Append('#');
                }

                inNumber = true;
            }
            else
            {
                sb.Append(c);
                inNumber = false;
            }
        }

        return sb.ToString();
    }
}
