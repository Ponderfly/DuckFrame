using System.Globalization;
using System.Text;
using DuckDB.NET.Data;

namespace DuckFrame;

/// <summary>把 FormattableString 转成 SQL 文本与参数列表。</summary>
internal static class SqlFormatter
{
    /// <summary>SQL 文本与按顺序生成的参数。</summary>
    public sealed record Formatted(string Sql, IReadOnlyList<DuckDBParameter> Parameters);

    /// <summary>
    /// 解析插值字符串。resolver 负责把每个插值参数渲染成 SQL 片段，并按需把参数追加进列表。
    /// </summary>
    public static Formatted Format(
        FormattableString interpolated,
        Func<object?, IList<DuckDBParameter>, string> resolver)
    {
        var format = interpolated.Format;
        var args = interpolated.GetArguments();
        var parameters = new List<DuckDBParameter>();
        var sb = new StringBuilder(format.Length + 16);

        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];

            if (c == '{')
            {
                if (i + 1 < format.Length && format[i + 1] == '{')
                {
                    sb.Append('{');
                    i++;
                    continue;
                }

                var end = format.IndexOf('}', i);
                if (end < 0)
                    throw new DuckFrameException("SQL 插值字符串中的 '{' 未闭合。");

                var token = format[(i + 1)..end];
                var sep = token.IndexOfAny([',', ':']);
                var indexText = sep < 0 ? token : token[..sep];

                if (!int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                    index < 0 || index >= args.Length)
                {
                    throw new DuckFrameException($"SQL 插值字符串中出现无法解析的占位符 {{{token}}}。");
                }

                sb.Append(resolver(args[index], parameters));
                i = end;
                continue;
            }

            if (c == '}')
            {
                if (i + 1 < format.Length && format[i + 1] == '}')
                {
                    sb.Append('}');
                    i++;
                    continue;
                }

                throw new DuckFrameException("SQL 插值字符串中出现未转义的 '}'，如需字面量请写 '}}'。");
            }

            sb.Append(c);
        }

        return new Formatted(sb.ToString(), parameters);
    }
}
