using System.Reflection;

namespace DuckFrame;

/// <summary>CLR 类型到 DuckDB 列类型的映射（docs/spec.md §3.1，已按实测修订）。</summary>
internal static class ClrToDuckDb
{
    public static string GetColumnType(Type type, DuckSessionOptions options, PropertyInfo? property = null)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;

        if (t == typeof(decimal))
        {
            var attribute = property?.GetCustomAttribute<DuckPrecisionAttribute>();
            return DecimalType(attribute?.Precision ?? options.DecimalPrecision, attribute?.Scale ?? options.DecimalScale);
        }

        // 以下三项受存储选项影响，LIST 元素不受（见 ScalarType）。
        if (t == typeof(DateTime))
            return options.UtcTimestamps ? "TIMESTAMPTZ" : "TIMESTAMP";
        if (t == typeof(TimeSpan))
            return options.TimeSpanAsMicros ? "BIGINT" : "INTERVAL";
        if (t.IsEnum)
            return options.EnumsAsInt ? GetColumnType(Enum.GetUnderlyingType(t), options) : "VARCHAR";

        if (t == typeof(char)) return "VARCHAR";
        if (t == typeof(byte[])) return "BLOB";

        if (ScalarType(t, options) is { } scalar)
            return scalar;

        // 标量集合 → 原生 LIST（DDL 必须写成 INTEGER[]，LIST(INTEGER) 不合法）。
        if (TryGetElementType(t, out var element) &&
            ScalarType(Nullable.GetUnderlyingType(element) ?? element, options) is { } inner)
        {
            return inner + "[]";
        }

        // 嵌套对象、字典、复杂集合：DuckDB.NET 的 appender 不能写 STRUCT / MAP，第一期统一存 JSON。
        return "JSON";
    }

    /// <summary>
    /// 与 DuckDB.NET appender 重载一一对应的标量类型（LIST 元素也用它）；不是标量时返回 null。
    /// </summary>
    public static string? ScalarType(Type t, DuckSessionOptions options)
    {
        if (t == typeof(bool)) return "BOOLEAN";
        if (t == typeof(sbyte)) return "TINYINT";
        if (t == typeof(short)) return "SMALLINT";
        if (t == typeof(int)) return "INTEGER";
        if (t == typeof(long)) return "BIGINT";
        if (t == typeof(byte)) return "UTINYINT";
        if (t == typeof(ushort)) return "USMALLINT";
        if (t == typeof(uint)) return "UINTEGER";
        if (t == typeof(ulong)) return "UBIGINT";
        if (t == typeof(float)) return "FLOAT";
        if (t == typeof(double)) return "DOUBLE";
        if (t == typeof(decimal)) return DecimalType(options.DecimalPrecision, options.DecimalScale);
        if (t == typeof(string)) return "VARCHAR";
        if (t == typeof(Guid)) return "UUID";
        if (t == typeof(DateTime)) return "TIMESTAMP";
        if (t == typeof(DateTimeOffset)) return "TIMESTAMPTZ";
        if (t == typeof(DateOnly)) return "DATE";
        if (t == typeof(TimeOnly)) return "TIME";
        if (t == typeof(TimeSpan)) return "INTERVAL";
        return null;
    }

    /// <summary>集合的元素类型；string 与 byte[] 不算集合。</summary>
    public static bool TryGetElementType(Type type, out Type element)
    {
        if (type != typeof(string) && type != typeof(byte[]))
        {
            if (type.IsArray)
            {
                element = type.GetElementType()!;
                return true;
            }

            foreach (var iface in type.GetInterfaces().Append(type))
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    element = iface.GetGenericArguments()[0];
                    return true;
                }
            }
        }

        element = typeof(object);
        return false;
    }

    private static string DecimalType(int precision, int scale)
    {
        if (precision is < 1 or > 38 || scale < 0 || scale > precision)
            throw new DuckFrameException($"DECIMAL({precision},{scale}) 不合法：精度须在 1..38 之间，且小数位不能超过精度。");

        return $"DECIMAL({precision},{scale})";
    }
}
