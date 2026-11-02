using System.Text;

namespace DuckFrame;

/// <summary>终端对齐表格输出。</summary>
internal static class TablePrinter
{
    public static string Render(
        IReadOnlyList<DuckColumn> columns,
        IReadOnlyList<object?[]> rows,
        int maxRows,
        int maxWidth,
        bool complete = true)
    {
        var shown = rows.Take(maxRows).ToArray();

        // 每列的显示上限：总宽度按列均分，超出用 … 截断。
        var cap = Math.Max(8, maxWidth / Math.Max(1, columns.Count) - 3);

        var headers = columns.Select(c => Truncate(c.Name, cap)).ToArray();
        var types = columns.Select(c => Truncate(c.DuckDbType.ToLowerInvariant(), cap)).ToArray();
        var cells = shown
            .Select(r => r.Select(v => Truncate(FormatValue(v), cap)).ToArray())
            .ToArray();

        var widths = new int[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            widths[i] = DisplayWidth(headers[i]);
            widths[i] = Math.Max(widths[i], DisplayWidth(types[i]));
            foreach (var row in cells)
                widths[i] = Math.Max(widths[i], DisplayWidth(row[i]));
        }

        var sb = new StringBuilder();
        AppendBorder(sb, widths, '┌', '┬', '┐');
        AppendRow(sb, headers, widths);
        AppendRow(sb, types, widths);
        AppendBorder(sb, widths, '├', '┼', '┤');

        foreach (var row in cells)
            AppendRow(sb, row, widths);

        AppendBorder(sb, widths, '└', '┴', '┘');
        sb.AppendLine($"{(complete ? "" : "at least ")}{rows.Count} row(s) ({shown.Length} shown)");

        return sb.ToString();
    }

    private static string FormatValue(object? value) => value switch
    {
        null or DBNull => "NULL",
        string s => s,
        byte[] bytes => $"<blob {bytes.Length} bytes>",
        // decimal 按自身小数位输出，避免预览把 1.234567 显示成 1.2346 而误判精度。
        decimal d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        double d => d.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
        float f => f.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        System.Collections.IDictionary dict =>
            "{" + string.Join(", ", dict.Keys.Cast<object>().Select(k => $"{k}: {FormatValue(dict[k])}")) + "}",
        System.Collections.IEnumerable items =>
            "[" + string.Join(", ", items.Cast<object?>().Select(FormatValue)) + "]",
        _ => value.ToString() ?? "NULL",
    };

    private static string Truncate(string text, int cap)
    {
        if (DisplayWidth(text) <= cap)
            return text;

        var sb = new StringBuilder();
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var w = DisplayWidth(rune.ToString());
            if (width + w > cap - 1)
                break;
            sb.Append(rune.ToString());
            width += w;
        }

        return sb.Append('…').ToString();
    }

    private static void AppendBorder(StringBuilder sb, int[] widths, char left, char middle, char right)
    {
        sb.Append(left);
        for (var i = 0; i < widths.Length; i++)
        {
            sb.Append('─', widths[i] + 2);
            sb.Append(i == widths.Length - 1 ? right : middle);
        }
        sb.AppendLine();
    }

    private static void AppendRow(StringBuilder sb, string[] cells, int[] widths)
    {
        sb.Append('│');
        for (var i = 0; i < cells.Length; i++)
        {
            var padding = widths[i] - DisplayWidth(cells[i]);
            sb.Append(' ').Append(cells[i]).Append(' ', padding + 1).Append('│');
        }
        sb.AppendLine();
    }

    /// <summary>终端显示宽度：CJK 等宽字符按 2 计。</summary>
    public static int DisplayWidth(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            width += v is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or
                     >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF or
                     >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60 or
                     >= 0xFFE0 and <= 0xFFE6 or >= 0x20000 and <= 0x3FFFD
                ? 2 : 1;
        }
        return width;
    }
}
