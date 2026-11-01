using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using DuckDB.NET.Data;

namespace DuckFrame;

/// <summary>
/// 通过 DuckDBAppender 批量写入。已按 DuckDB.NET.Data 1.5.6.1 实测核对：
/// <list type="bullet">
/// <item>CreateRow() 返回 IDuckDBAppenderRow，逐列 AppendValue / AppendNullValue，最后 EndRow()。</item>
/// <item>appender <b>不做类型转换</b>：把 int 追加到 DOUBLE 列会按原始字节写成 2.5E-323，
/// 超出列小数位的 decimal 会被静默截断。</item>
/// </list>
/// 因此每一列都按<b>目标列的 DuckDB 类型</b>生成写入委托，先把值转换成对应 CLR 类型再追加。
/// </summary>
internal static class AppenderWriter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly MethodInfo AppendEnumerable = typeof(IDuckDBAppenderRow).GetMethods()
        .Single(m => m.Name == nameof(IDuckDBAppenderRow.AppendValue) &&
                     m.IsGenericMethodDefinition &&
                     m.GetParameters()[0].ParameterType.IsGenericType &&
                     m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>));

    private static readonly ConcurrentDictionary<Type, Action<IDuckDBAppenderRow, object>> ListAppenders = new();

    /// <summary>把 rows 写入 tableName，返回写入行数。</summary>
    public static long Write(
        DuckDBConnection connection,
        string tableName,
        IReadOnlyList<ColumnDefinition> columns,
        IEnumerable<object?[]> rows,
        string? catalog = null,
        string schema = "main")
    {
        var writers = columns.Select(CreateWriter).ToArray();
        var appender = catalog is null ? connection.CreateAppender(tableName) : connection.CreateAppender(catalog, schema, tableName);
        long count = 0;

        try
        {
            foreach (var row in rows)
            {
                var appenderRow = appender.CreateRow();

                for (var i = 0; i < writers.Length; i++)
                {
                    var value = row[i];
                    if (value is null)
                    {
                        appenderRow.AppendNullValue();
                        continue;
                    }

                    try
                    {
                        writers[i](appenderRow, value);
                    }
                    catch (Exception ex) when (ex is not DuckFrameException)
                    {
                        throw new DuckFrameException(
                            $"写入列 \"{columns[i].Name}\"（{columns[i].DuckDbType}）的第 {count} 行失败，值类型为 {value.GetType().Name}。", ex);
                    }
                }

                appenderRow.EndRow();
                count++;
            }

            appender.Close();
        }
        catch
        {
            // 写了半行的 appender 在释放时还会再抛错，吞掉它以保留真正的原因。
            try { appender.Dispose(); } catch { }
            throw;
        }

        appender.Dispose();
        return count;
    }

    private static Action<IDuckDBAppenderRow, object> CreateWriter(ColumnDefinition column) =>
        DuckDbTypes.Family(column.DuckDbType) switch
        {
            "BOOLEAN" => static (r, v) => r.AppendValue(Convert.ToBoolean(v, Invariant)),
            "TINYINT" => static (r, v) => r.AppendValue(Convert.ToSByte(v, Invariant)),
            "SMALLINT" => static (r, v) => r.AppendValue(Convert.ToInt16(v, Invariant)),
            "INTEGER" => static (r, v) => r.AppendValue(Convert.ToInt32(v, Invariant)),
            "BIGINT" => static (r, v) => r.AppendValue(v is TimeSpan ts ? ts.Ticks / TimeSpan.TicksPerMicrosecond : Convert.ToInt64(v, Invariant)),
            "UTINYINT" => static (r, v) => r.AppendValue(Convert.ToByte(v, Invariant)),
            "USMALLINT" => static (r, v) => r.AppendValue(Convert.ToUInt16(v, Invariant)),
            "UINTEGER" => static (r, v) => r.AppendValue(Convert.ToUInt32(v, Invariant)),
            "UBIGINT" => static (r, v) => r.AppendValue(Convert.ToUInt64(v, Invariant)),
            "HUGEINT" or "UHUGEINT" => static (r, v) => r.AppendValue(v is BigInteger b ? b : new BigInteger(Convert.ToDecimal(v, Invariant))),
            "FLOAT" => static (r, v) => r.AppendValue(Convert.ToSingle(v, Invariant)),
            "DOUBLE" => static (r, v) => r.AppendValue(Convert.ToDouble(v, Invariant)),
            "DECIMAL" => CreateDecimalWriter(column),
            "VARCHAR" => static (r, v) => r.AppendValue(ToText(v)),
            "JSON" => static (r, v) => r.AppendValue(JsonSerializer.Serialize(v, v.GetType())),
            "UUID" => static (r, v) => r.AppendValue(v is Guid g ? g : Guid.Parse(ToText(v))),
            "DATE" => static (r, v) => r.AppendValue(ToDateOnly(v)),
            "TIME" => static (r, v) => r.AppendValue(ToTimeOnly(v)),
            "TIMESTAMP" => static (r, v) => r.AppendValue(ToDateTime(v)),
            "TIMESTAMPTZ" => static (r, v) =>
            {
                if (v is DateTimeOffset dto)
                    r.AppendValue(dto);
                else
                    r.AppendValue(ToDateTime(v));
            },
            "INTERVAL" => static (r, v) => r.AppendValue(v is TimeSpan ts ? ts : TimeSpan.Parse(ToText(v), Invariant)),
            "BLOB" => static (r, v) => r.AppendValue(ToBytes(v)),
            "LIST" => CreateListWriter(column),
            _ => throw new DuckFrameException(
                $"列 \"{column.Name}\" 的类型 {column.DuckDbType} 暂不支持写入。" +
                "可以在 SQL 里先 CAST 成基础类型，或用 TableOptions.ColumnTypes 改成可写入的类型。"),
        };

    private static Action<IDuckDBAppenderRow, object> CreateDecimalWriter(ColumnDefinition column)
    {
        var scale = DuckDbTypes.DecimalScale(column.DuckDbType);

        return (r, v) =>
        {
            var d = Convert.ToDecimal(v, Invariant);

            // appender 会把多出的小数位直接截掉，这里提前拒绝，避免静默丢精度。
            if (scale is { } s && s < 28 && decimal.Round(d, s) != d)
            {
                throw new DuckFrameException(
                    $"列 \"{column.Name}\" 定义为 {column.DuckDbType}，值 {d.ToString(Invariant)} 的小数位超出范围会被截断。" +
                    "请用 [DuckPrecision(p, s)]、TableOptions.ColumnTypes 或 DuckSessionOptions.DecimalScale 放宽小数位。");
            }

            r.AppendValue(d);
        };
    }

    private static Action<IDuckDBAppenderRow, object> CreateListWriter(ColumnDefinition column)
    {
        var declared = DuckDbTypes.Family(DuckDbTypes.ListElement(column.DuckDbType));
        var elementColumn = column with { DuckDbType = DuckDbTypes.ListElement(column.DuckDbType) };
        var scale = DuckDbTypes.DecimalScale(elementColumn.DuckDbType);

        return (r, v) =>
        {
            var type = v.GetType();
            if (!ClrToDuckDb.TryGetElementType(type, out var element) ||
                ClrToDuckDb.ScalarType(Nullable.GetUnderlyingType(element) ?? element, new DuckSessionOptions()) is not { } natural ||
                DuckDbTypes.Family(natural) != declared)
            {
                // 元素类型不一致时 appender 同样会按原始字节写入，只能拒绝。
                throw new DuckFrameException(
                    $"列 \"{column.Name}\" 定义为 {column.DuckDbType}，但值是 {type.Name}，元素类型不一致，已拒绝写入。");
            }

            // 输入只枚举一次；验证后把当前 LIST 交给已核对的泛型 appender 重载。
            if (declared == "DECIMAL")
            {
                if (v is IEnumerable<decimal> decimals)
                    v = ValidateDecimals(decimals, column.Name, scale).ToArray();
                else
                    v = ValidateNullableDecimals((IEnumerable<decimal?>)v, column.Name, scale).ToArray();
            }
            ListAppenders.GetOrAdd(element, CompileListAppender)(r, v);
        };
    }

    private static decimal ValidateDecimal(decimal value, string column, int? scale)
    {
        if (scale is { } s && s < 28 && decimal.Round(value, s) != value)
            throw new DuckFrameException($"列 \"{column}\" 的 LIST 元素 {value.ToString(Invariant)} 超出小数位 {s}；请通过 ColumnTypes 或 DecimalScale 放宽精度。");
        return value;
    }

    private static IEnumerable<decimal> ValidateDecimals(IEnumerable<decimal> values, string column, int? scale)
    {
        foreach (var value in values) yield return ValidateDecimal(value, column, scale);
    }

    private static IEnumerable<decimal?> ValidateNullableDecimals(IEnumerable<decimal?> values, string column, int? scale)
    {
        foreach (var value in values) yield return value is { } d ? ValidateDecimal(d, column, scale) : null;
    }

    private static Action<IDuckDBAppenderRow, object> CompileListAppender(Type element)
    {
        var row = Expression.Parameter(typeof(IDuckDBAppenderRow), "row");
        var value = Expression.Parameter(typeof(object), "value");
        var call = Expression.Call(
            row,
            AppendEnumerable.MakeGenericMethod(element),
            Expression.Convert(value, typeof(IEnumerable<>).MakeGenericType(element)));

        return Expression.Lambda<Action<IDuckDBAppenderRow, object>>(call, row, value).Compile();
    }

    private static string ToText(object value) => value switch
    {
        string s => s,
        Enum e => e.ToString(),
        IFormattable f => f.ToString(null, Invariant),
        _ => value.ToString() ?? string.Empty,
    };

    private static DateOnly ToDateOnly(object value) => value switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        DateTimeOffset dto => DateOnly.FromDateTime(dto.DateTime),
        _ => DateOnly.Parse(ToText(value), Invariant),
    };

    private static TimeOnly ToTimeOnly(object value) => value switch
    {
        TimeOnly t => t,
        TimeSpan ts => TimeOnly.FromTimeSpan(ts),
        DateTime dt => TimeOnly.FromDateTime(dt),
        _ => TimeOnly.Parse(ToText(value), Invariant),
    };

    private static DateTime ToDateTime(object value) => value switch
    {
        DateTime dt => dt,
        DateTimeOffset dto => dto.UtcDateTime,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        _ => DateTime.Parse(ToText(value), Invariant),
    };

    private static byte[] ToBytes(object value)
    {
        switch (value)
        {
            case byte[] bytes:
                return bytes;
            case ReadOnlyMemory<byte> memory:
                return memory.ToArray();
            case Stream stream:
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
            default:
                throw new DuckFrameException($"无法把 {value.GetType().Name} 写入 BLOB 列。");
        }
    }
}
