using System.Globalization;
using System.Text;

namespace Spydate.Mcp.Rendering;

/// <summary>
/// Rows of aligned text, which is how every list this server returns is shaped.
///
/// Not JSON, deliberately. JSON repeats every key on every row — fifty functions with eight fields
/// spends several hundred tokens writing the word "name" — and there is nothing an agent does with
/// these answers except read them. A header line names the columns once.
/// </summary>
public sealed class TextTable
{
    private readonly string[] _headers;
    private readonly int[] _limits;
    private readonly List<string[]> _rows = new();

    /// <param name="columns">Each column's heading and the width past which its cells are elided.</param>
    public TextTable(params (string Header, int Limit)[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        _headers = columns.Select(c => c.Header).ToArray();
        _limits = columns.Select(c => c.Limit).ToArray();
    }

    public int Count => _rows.Count;

    public void Add(params string?[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var row = new string[_headers.Length];
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = Budget.Elide(i < cells.Length ? cells[i] : null, _limits[i]);
        }

        _rows.Add(row);
    }

    /// <summary>The table, or a single line saying there was nothing — never an empty answer.</summary>
    public string Render(string whenEmpty = "nothing matched")
    {
        if (_rows.Count == 0)
        {
            return whenEmpty;
        }

        var widths = new int[_headers.Length];
        for (int i = 0; i < widths.Length; i++)
        {
            widths[i] = Math.Max(_headers[i].Length, _rows.Max(r => r[i].Length));
        }

        var sb = new StringBuilder();
        AppendRow(sb, _headers, widths);
        foreach (var row in _rows)
        {
            AppendRow(sb, row, widths);
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static void AppendRow(StringBuilder sb, string[] cells, int[] widths)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            sb.Append(cells[i]);
            if (i < cells.Length - 1)
            {
                sb.Append(' ', widths[i] - cells[i].Length + 2);
            }
        }

        sb.Append('\n');
    }

    /// <summary>
    /// The line that closes a paged list. Three numbers, and all three earn their place: how many
    /// came back, how many matched the filter, and how many exist. Without the third an agent cannot
    /// tell whether its filter did anything at all.
    /// </summary>
    public static string Meta(int returned, int matching, int total, string subject, string? next = null, string? filters = null)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"-- {returned} of {matching} matching (of {total} {subject})");
        if (!string.IsNullOrEmpty(filters))
        {
            sb.Append(CultureInfo.InvariantCulture, $", {filters}");
        }

        if (next is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $". next: {next}");
        }

        return sb.Append(" --").ToString();
    }
}
