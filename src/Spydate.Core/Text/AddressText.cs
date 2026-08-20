using System.Globalization;

namespace Spydate.Core.Text;

/// <summary>
/// Reading an address back out of the text Spydate produced. Listings put the address first, pseudo-C puts
/// it in a trailing comment, and both use names with the address in them (<c>sub_140001260</c>), so a
/// caret anywhere useful can be turned back into the address it is about.
/// </summary>
public static class AddressText
{
    /// <summary>Characters that make up an identifier in either view.</summary>
    public static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '?' or '@' or '$' or '!' or '.' or ':';

    /// <summary>Parses hex, with or without the <c>0x</c>.</summary>
    public static ulong? ParseHex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        return ulong.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value) ? value : null;
    }

    /// <summary>
    /// The address a generated name carries: <c>sub_140001260</c>, <c>loc_40DFA8</c>, <c>data_14003A100</c>,
    /// <c>unk_401000</c>. Anything else - including a name the user chose - returns null.
    /// </summary>
    public static ulong? FromGeneratedName(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return null;
        }

        foreach (string prefix in GeneratedPrefixes)
        {
            if (identifier.StartsWith(prefix, StringComparison.Ordinal))
            {
                return ParseHex(identifier[prefix.Length..].TrimEnd(':'));
            }
        }

        return null;
    }

    private static readonly string[] GeneratedPrefixes = { "sub_", "loc_", "data_", "unk_", "off_" };

    /// <summary>
    /// The address a line is about: the one a listing starts with, or the one pseudo-C leaves in the
    /// trailing <c>// XXXX</c> comment.
    /// </summary>
    public static ulong? FromLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        // A listing line begins with the address, padded to the image's pointer width.
        string trimmed = line.TrimStart();
        int end = 0;
        while (end < trimmed.Length && Uri.IsHexDigit(trimmed[end]))
        {
            end++;
        }

        if (end is 8 or 16 && (end == trimmed.Length || trimmed[end] == ' '))
        {
            return ParseHex(trimmed[..end]);
        }

        // Pseudo-C keeps the address in a trailing comment, possibly followed by a user note.
        int comment = line.LastIndexOf("// ", StringComparison.Ordinal);
        if (comment >= 0)
        {
            string rest = line[(comment + 3)..].TrimStart();
            int digits = 0;
            while (digits < rest.Length && Uri.IsHexDigit(rest[digits]))
            {
                digits++;
            }

            if (digits >= 4)
            {
                return ParseHex(rest[..digits]);
            }
        }

        return null;
    }

    /// <summary>The identifier surrounding <paramref name="offset"/>, or null when the caret is not on one.</summary>
    public static string? WordAt(string? text, int offset)
    {
        if (string.IsNullOrEmpty(text) || offset < 0 || offset > text.Length)
        {
            return null;
        }

        int start = Math.Min(offset, text.Length - 1);
        if (start < 0 || !IsIdentifierChar(text[start]))
        {
            // The caret often sits just past the word it is on.
            start = offset - 1;
            if (start < 0 || start >= text.Length || !IsIdentifierChar(text[start]))
            {
                return null;
            }
        }

        int from = start;
        while (from > 0 && IsIdentifierChar(text[from - 1]))
        {
            from--;
        }

        int to = start;
        while (to + 1 < text.Length && IsIdentifierChar(text[to + 1]))
        {
            to++;
        }

        return text[from..(to + 1)];
    }
}
