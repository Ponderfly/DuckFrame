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

    /// <summary>
    /// 按 DuckDB 的规则给重复列名（忽略大小写）加后缀：重复者依次尝试 name_1、name_2…，
    /// 直到与已占用的名字都不冲突；名字保留各自原来的大小写。
    /// </summary>
    public static string[] Deduplicate(IReadOnlyList<string> names)
    {
        var result = new string[names.Count];
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            if (used.TryAdd(name, 0))
            {
                result[i] = name;
                continue;
            }

            string candidate;
            do candidate = $"{name}_{++used[name]}";
            while (used.ContainsKey(candidate));

            used.Add(candidate, 0);
            result[i] = candidate;
        }
        return result;
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
