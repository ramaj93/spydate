using System.Text;

namespace Spydate.Core.Strings;

/// <summary>Presentation of scanned string data inside one line of code.</summary>
public static class StringLiterals
{
    public const int DefaultMaxLength = 60;

    /// <summary>
    /// Trims literal text and escapes what would break the line. Backslashes are left alone: doubling
    /// them turns every Windows path in a listing into noise.
    /// </summary>
    public static string Escape(string text, int maxLength = DefaultMaxLength)
    {
        ArgumentNullException.ThrowIfNull(text);

        string trimmed = text.Length <= maxLength ? text : text[..maxLength] + "…";
        var sb = new StringBuilder(trimmed.Length + 2);
        foreach (char c in trimmed)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    sb.Append(char.IsControl(c) ? '.' : c);
                    break;
            }
        }

        return sb.ToString();
    }
}
