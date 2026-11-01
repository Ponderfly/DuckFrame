using System.Runtime.InteropServices;
using DuckDB.NET.Data;
using DuckDB.NET.Native;

namespace DuckFrame;

/// <summary>补充 provider 未暴露的嵌套结果类型元数据；只准备语句，不重新执行查询。</summary>
internal static class NativeResultSchema
{
    // DuckDB 1.5.6 的公开 C API；返回的 logical type 由 provider SafeHandle 负责释放。
    [DllImport("duckdb", EntryPoint = "duckdb_prepared_statement_column_logical_type", CallingConvention = CallingConvention.Cdecl)]
    private static extern DuckDBLogicalType ColumnType(DuckDBPreparedStatement statement, ulong index);

    [DllImport("duckdb", EntryPoint = "duckdb_prepared_statement_column_count", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong ColumnCount(DuckDBPreparedStatement statement);

    public static string?[] Read(DuckDBConnection connection, string sql, IReadOnlyList<DuckDBParameter> parameters, int count)
    {
        var types = new string?[count];
        var statementCount = NativeMethods.ExtractStatements.DuckDBExtractStatements(connection.NativeConnection, sql, out var extracted);
        using (extracted)
        {
            // 不把多条语句拼到 DESCRIBE 后面，避免重复执行后续语句。
            if (statementCount != 1) return types;
            var status = NativeMethods.ExtractStatements.DuckDBPrepareExtractedStatement(connection.NativeConnection, extracted, 0, out var prepared);
            using (prepared)
            {
                if (status == DuckDBState.Success && ColumnCount(prepared) == (ulong)count)
                    for (var i = 0; i < count; i++)
                    {
                        using var logical = ColumnType(prepared, (ulong)i);
                        if (!logical.IsInvalid) types[i] = Format(logical);
                    }
            }
        }

        // SELECT $parameter 的类型要在绑定后才能确定；DESCRIBE 只分析单条语句。
        if (types.Any(t => t is null) && parameters.Count > 0)
        {
            using var describe = connection.CreateCommand();
            describe.CommandText = "DESCRIBE " + sql;
            foreach (var parameter in parameters)
                describe.Parameters.Add(new DuckDBParameter(parameter.ParameterName, parameter.Value));
            using var reader = describe.ExecuteReader();
            var index = 0;
            while (reader.Read() && index < count) types[index++] = reader.GetString(1);
        }
        return types;
    }

    private static string? Format(DuckDBLogicalType type)
    {
        var kind = NativeMethods.LogicalType.DuckDBGetTypeId(type);
        if (kind == DuckDBType.Decimal)
            return $"DECIMAL({NativeMethods.LogicalType.DuckDBDecimalWidth(type)},{NativeMethods.LogicalType.DuckDBDecimalScale(type)})";
        if (kind is DuckDBType.List or DuckDBType.Array)
        {
            using var child = kind == DuckDBType.List
                ? NativeMethods.LogicalType.DuckDBListTypeChildType(type)
                : NativeMethods.LogicalType.DuckDBArrayTypeChildType(type);
            var element = Format(child);
            return element is null ? null : element + "[]";
        }
        return kind.ToString() switch
        {
            "Invalid" or "SqlNull" or "Any" => null,
            "UnsignedTinyInt" => "UTINYINT", "UnsignedSmallInt" => "USMALLINT",
            "UnsignedInteger" => "UINTEGER", "UnsignedBigInt" => "UBIGINT", "UnsignedHugeInt" => "UHUGEINT",
            "TimestampTz" => "TIMESTAMPTZ", "TimestampNs" or "TimestampMs" or "TimestampS" => "TIMESTAMP",
            "Enum" => "VARCHAR", "Struct" or "Map" or "Union" => "JSON",
            var name => name.ToUpperInvariant(),
        };
    }
}
