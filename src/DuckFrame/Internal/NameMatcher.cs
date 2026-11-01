namespace DuckFrame;

/// <summary>列名与 CLR 成员之间的模糊匹配规则（精确 → 忽略大小写与下划线）。</summary>
internal static class NameMatcher
{
    public static string Normalize(string name) =>
        new(name.Where(c => c != '_').Select(char.ToLowerInvariant).ToArray());

    public static void EnsureUnique(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            if (!seen.Add(name))
                throw new DuckFrameException($"存在重复列名 \"{name}\"，请使用不同的列名或 SQL 别名。");
    }

    public static int? FindOrdinal(IReadOnlyList<DuckColumn> columns, string memberName)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Name == memberName)
                return i;
        }

        var insensitive = FindUnique(columns, c => string.Equals(c, memberName, StringComparison.OrdinalIgnoreCase), memberName);
        if (insensitive is not null)
            return insensitive;

        var normalized = Normalize(memberName);
        return FindUnique(columns, c => Normalize(c) == normalized, memberName);
    }

    private static int? FindUnique(IReadOnlyList<DuckColumn> columns, Func<string, bool> matches, string memberName)
    {
        int? result = null;
        for (var i = 0; i < columns.Count; i++)
        {
            if (!matches(columns[i].Name)) continue;
            if (result is not null)
                throw new DuckFrameException($"成员 \"{memberName}\" 匹配到多个列，请用 [Column] 显式指定列名。");
            result = i;
        }
        return result;
    }
}
