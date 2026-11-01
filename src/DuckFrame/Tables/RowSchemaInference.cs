using System.Globalization;

namespace DuckFrame;

/// <summary>字典行集合的列结构推导（docs/spec.md §3.1 合并规则）。</summary>
internal static class RowSchemaInference
{
    /// <summary>从前 SampleSize 行推导列名与类型，列顺序按首次出现顺序。</summary>
    public static IReadOnlyList<ColumnDefinition> Infer(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        DuckSessionOptions options,
        TableOptions? tableOptions)
    {
        var names = new List<string>();
        var samples = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows.Take(options.SampleSize))
        {
            NameMatcher.EnsureUnique(row.Keys);
            foreach (var (key, value) in row)
            {
                if (!samples.TryGetValue(key, out var values))
                {
                    names.Add(key);
                    samples[key] = values = [];
                }

                if (value is not null and not DBNull)
                    values.Add(value);
            }
        }

        var overrides = tableOptions?.ColumnTypes;
        if (overrides is not null)
        {
            NameMatcher.EnsureUnique(overrides.Keys);
            foreach (var name in overrides.Keys)
                if (samples.TryAdd(name, [])) names.Add(name);
        }

        if (names.Count == 0)
        {
            if (overrides is { Count: > 0 })
                return overrides.Select(kv => new ColumnDefinition(kv.Key, kv.Value, typeof(object))).ToArray();

            throw new DuckFrameException(
                "无法从空的字典行集合推导列结构：请至少提供一行数据，或通过 TableOptions.ColumnTypes 显式声明列。");
        }

        return names.Select(name =>
        {
            var (duckType, clrType) = InferColumn(samples[name], options);
            if (tableOptions?.FindColumnType(name) is { } explicitType)
                duckType = explicitType;

            return new ColumnDefinition(name, duckType, clrType);
        }).ToArray();
    }

    private static (string DuckType, Type ClrType) InferColumn(List<object> values, DuckSessionOptions options)
    {
        var types = values.Select(v => v.GetType()).Distinct().ToArray();

        if (types.Length == 0)
            return ("VARCHAR", typeof(string));

        // decimal（可混整数）按采样值推精度，避免默认 DECIMAL(18,4) 截断。
        if (types.Contains(typeof(decimal)) && types.All(t => t == typeof(decimal) || IsIntegral(t)))
            return (DecimalType(values, options), typeof(decimal));

        if (types.Length == 1)
            return (ClrToDuckDb.GetColumnType(types[0], options), types[0]);

        // 数值族向上提升 → 含 string 则 VARCHAR → 否则 JSON。
        if (types.All(IsIntegral))
            return ("BIGINT", typeof(long));
        if (types.All(t => IsIntegral(t) || t == typeof(float) || t == typeof(double) || t == typeof(decimal)))
            return ("DOUBLE", typeof(double));
        if (types.Contains(typeof(string)))
            return ("VARCHAR", typeof(string));

        return ("JSON", typeof(object));
    }

    private static string DecimalType(List<object> values, DuckSessionOptions options)
    {
        var scale = options.DecimalScale;
        var integerDigits = options.DecimalPrecision - options.DecimalScale;

        foreach (var value in values)
        {
            var d = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            scale = Math.Max(scale, d.Scale);
            integerDigits = Math.Max(
                integerDigits,
                decimal.Truncate(Math.Abs(d)).ToString(CultureInfo.InvariantCulture).Length);
        }

        // 总位数超过 38 时优先保整数位：整数溢出会报错，小数位不足才会被写入器拦下。
        if (integerDigits + scale > 38)
            scale = Math.Max(0, 38 - integerDigits);

        return $"DECIMAL({Math.Min(38, integerDigits + scale)},{scale})";
    }

    private static bool IsIntegral(Type t) =>
        t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) ||
        t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong);
}
