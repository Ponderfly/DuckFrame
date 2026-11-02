using System.Data;

namespace DuckFrame;

/// <summary>
/// DuckDB 类型名的规范化。以下行为已按 DuckDB.NET.Data 1.5.6.1 实测核对：
/// <list type="bullet">
/// <item>GetDataTypeName 返回 DuckDB.NET 的枚举名（Decimal、UnsignedTinyInt、List…），不能直接用于 DDL。</item>
/// <item>DECIMAL 的精度只能从 GetSchemaTable() 的 NumericPrecision / NumericScale 取得。</item>
/// <item>LIST 只能写成 <c>INTEGER[]</c>，<c>LIST(INTEGER)</c> 会报解析错误。</item>
/// </list>
/// </summary>
internal static class DuckDbTypes
{
    /// <summary>把结果列转换成可用于 CREATE TABLE 的类型字符串。</summary>
    /// <remarks>approximate：LIST 拿不到原生元素类型、只能按 CLR 类型推测时为 true。</remarks>
    public static string FromReader(IDataRecord reader, int ordinal, Func<DataTable?> schema, DuckSessionOptions options,
        Func<int, string?>? nestedType, out bool approximate)
    {
        var name = reader.GetDataTypeName(ordinal);
        approximate = false;

        if (name is "List" or "Array")
        {
            // List<int> → INTEGER[]；元素不是标量时退化为 JSON。
            var clrType = ClrToDuckDb.GetColumnType(reader.GetFieldType(ordinal), options);
            if (clrType == "JSON") return clrType;
            if (nestedType?.Invoke(ordinal) is { } exact) return exact;

            // 多语句 SQL 等场景取不到原生元素类型：先按 CLR 类型推测，读取不受影响；
            // 推测值可能丢失 DECIMAL 精度等信息，所以再次注册时会被拒绝（见 DuckColumn.IsTypeApproximate）。
            approximate = true;
            return clrType;
        }

        return name switch
        {
            "Decimal" => DecimalFromSchema(schema(), ordinal),
            "UnsignedTinyInt" => "UTINYINT",
            "UnsignedSmallInt" => "USMALLINT",
            "UnsignedInteger" => "UINTEGER",
            "UnsignedBigInt" => "UBIGINT",
            "UnsignedHugeInt" => "UHUGEINT",
            "TimestampTz" => "TIMESTAMPTZ",

            // 读回的已是 DateTime（100ns 精度），按微秒 TIMESTAMP 重建即可。
            "TimestampNs" or "TimestampMs" or "TimestampS" => "TIMESTAMP",

            // appender 无法写 STRUCT / MAP，第一期统一存 JSON。
            "Struct" or "Map" or "Union" => "JSON",
            "Enum" => "VARCHAR",

            // Boolean、Integer、Varchar、Uuid、Date、Time、Timestamp、Interval、Blob 等与 DDL 同名。
            _ => name.ToUpperInvariant(),
        };
    }

    /// <summary>把 DDL 类型归一成写入器分派用的类型族（去掉参数、合并常见别名）。</summary>
    public static string Family(string ddlType)
    {
        var type = ddlType.Trim().ToUpperInvariant();
        if (type.EndsWith("[]", StringComparison.Ordinal))
            return "LIST";

        var paren = type.IndexOf('(');
        if (paren >= 0)
            type = type[..paren].Trim();

        return type switch
        {
            "BOOL" or "LOGICAL" => "BOOLEAN",
            "INT" or "INT4" or "SIGNED" => "INTEGER",
            "INT8" or "LONG" => "BIGINT",
            "REAL" or "FLOAT4" => "FLOAT",
            "FLOAT8" => "DOUBLE",
            "NUMERIC" => "DECIMAL",
            "STRING" or "TEXT" or "CHAR" or "BPCHAR" => "VARCHAR",
            "BYTEA" or "BINARY" or "VARBINARY" => "BLOB",
            "DATETIME" => "TIMESTAMP",
            "TIMESTAMP WITH TIME ZONE" => "TIMESTAMPTZ",
            _ => type,
        };
    }

    /// <summary>LIST 类型去掉末尾的 []，得到元素类型。</summary>
    public static string ListElement(string ddlType) => ddlType.Trim()[..^2];

    /// <summary>取 DECIMAL(p,s) 的小数位，取不到时返回 null。</summary>
    public static int? DecimalScale(string ddlType)
    {
        var open = ddlType.IndexOf('(');
        var comma = ddlType.IndexOf(',', open + 1);
        var close = ddlType.IndexOf(')', comma + 1);

        if (Family(ddlType) == "DECIMAL" && comma < 0)
            return open < 0 ? 3 : 0; // DuckDB 的 DECIMAL 默认 (18,3)，DECIMAL(p) 默认 scale=0。

        return open >= 0 && comma > open && close > comma &&
               int.TryParse(ddlType.AsSpan(comma + 1, close - comma - 1).Trim(), out var scale)
            ? scale
            : null;
    }

    private static string DecimalFromSchema(DataTable? schema, int ordinal)
    {
        if (schema is not null && ordinal < schema.Rows.Count)
        {
            var row = schema.Rows[ordinal];
            if (row["NumericPrecision"] is not DBNull and not null && row["NumericScale"] is not DBNull and not null)
                return $"DECIMAL({Convert.ToInt32(row["NumericPrecision"])},{Convert.ToInt32(row["NumericScale"])})";
        }

        // 取不到精度时用最宽的定义，宁可多占空间也不截断。
        return "DECIMAL(38,10)";
    }
}
