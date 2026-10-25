using System.Text;

namespace SwarmCore.RemoteControl;

/// <summary>Aligned plain-text tables: one row per item, no truncation, newlines rendered as ⏎.</summary>
internal static class TextTable
{
    private const string ColumnGap = "  ";

    public static string Render(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var cells = rows.Select(r => headers.Select((_, i) => Cell(i < r.Count ? r[i] : null)).ToArray()).ToList();
        var widths = headers.Select((h, i) => Math.Max(h.Length, cells.Count == 0 ? 0 : cells.Max(r => r[i].Length))).ToArray();
        var sb = new StringBuilder();
        AppendRow(sb, headers, widths);
        AppendRow(sb, widths.Select(w => new string('-', w)).ToArray(), widths);
        foreach (var row in cells) AppendRow(sb, row, widths);
        return sb.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>Single-line rendering of one cell value.</summary>
    public static string Cell(string? value) => value is null
        ? ""
        : value.Replace("\r\n", "⏎").Replace('\n', '⏎').Replace('\r', '⏎').Replace('\t', ' ');

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> cells, int[] widths)
    {
        var line = new StringBuilder();
        for (var i = 0; i < widths.Length; i++)
        {
            if (i > 0) line.Append(ColumnGap);
            line.Append(cells[i].PadRight(widths[i]));
        }
        sb.Append(line.ToString().TrimEnd()).Append('\n');
    }
}
