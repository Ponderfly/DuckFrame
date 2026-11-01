namespace DuckFrame;

/// <summary>表名 / 列名的规范化与加引号。</summary>
internal static class NameResolver
{
    /// <summary>给标识符加双引号，内部 " 转义为 ""。</summary>
    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    /// <summary>解析最终表名。返回 null 表示交给调用方生成兜底名。</summary>
    public static string? Resolve(string? explicitName, string? expression, Type elementType)
    {
        if (!string.IsNullOrWhiteSpace(explicitName))
            return Sanitize(explicitName);

        var attr = elementType.GetCustomAttributes(typeof(TableAttribute), inherit: false);
        if (attr.Length == 1 && attr[0] is TableAttribute table && !string.IsNullOrWhiteSpace(table.Name))
            return Sanitize(table.Name);

        return CleanExpression(expression);
    }

    /// <summary>从 CallerArgumentExpression 中提取变量名。</summary>
    public static string? CleanExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var s = expression.Trim();

        // this._orders / a.b.orders → orders
        var dot = s.LastIndexOf('.');
        if (dot >= 0)
            s = s[(dot + 1)..];

        // GetOrders() → GetOrders；dict["x"] → dict
        var cut = s.IndexOfAny(['(', '[']);
        if (cut >= 0)
            s = s[..cut];

        s = s.TrimStart('@', '_');

        return s.Length > 0 && IsValidIdentifier(s) ? s : null;
    }

    /// <summary>把任意字符串转成合法标识符。</summary>
    public static string Sanitize(string name)
    {
        var chars = name.Trim().Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        var result = new string(chars).Trim('_');
        if (result.Length == 0)
            result = "tbl";
        if (char.IsDigit(result[0]))
            result = "_" + result;
        return result;
    }

    /// <summary>重名时追加 _2、_3。</summary>
    public static string MakeUnique(string candidate, IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(candidate))
            return candidate;

        for (var i = 2; ; i++)
        {
            var next = $"{candidate}_{i}";
            if (!taken.Contains(next))
                return next;
        }
    }

    private static bool IsValidIdentifier(string s) =>
        s.Length > 0 &&
        (char.IsLetter(s[0]) || s[0] == '_') &&
        s.All(c => char.IsLetterOrDigit(c) || c == '_');
}
