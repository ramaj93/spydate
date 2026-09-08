using System.Globalization;
using System.Text;

namespace Spydate.Mcp.Rendering;

/// <summary>
/// Bytes as address, hex and ASCII — the shape every hex viewer has used for fifty years, because a
/// reader can find a string in it and count an offset along a row without being told how.
/// </summary>
public static class HexDump
{
    /// <summary>Sixteen to a row, short rows padded so the ASCII column stays where it is.</summary>
    public static string Render(ReadOnlySpan<byte> bytes, ulong address)
    {
        var sb = new StringBuilder();
        for (int offset = 0; offset < bytes.Length; offset += 16)
        {
            int run = Math.Min(16, bytes.Length - offset);
            sb.Append(CultureInfo.InvariantCulture, $"0x{address + (ulong)offset:X}  ");
            for (int i = 0; i < 16; i++)
            {
                sb.Append(i < run ? bytes[offset + i].ToString("X2", CultureInfo.InvariantCulture) : "  ").Append(' ');
            }

            sb.Append(' ');
            for (int i = 0; i < run; i++)
            {
                byte b = bytes[offset + i];
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }
}
