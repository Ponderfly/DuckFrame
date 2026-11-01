namespace DuckFrame;

/// <summary>列名与 CLR 成员之间的模糊匹配规则（精确 → 忽略大小写与下划线）。</summary>
internal static class NameMatcher
{
    public static string Normalize(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static int? FindOrdinal(IReadOnlyList<DuckColumn> columns, string memberName)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Name == memberName)
                return i;
        }

        var normalized = Normalize(memberName);
        for (var i = 0; i < columns.Count; i++)
        {
            if (Normalize(columns[i].Name) == normalized)
                return i;
        }

        return null;
    }
}
