namespace DuckFrame;

/// <summary>查询结果中的一列。</summary>
/// <param name="Name">列名。</param>
/// <param name="DuckDbType">DuckDB 类型名，例如 VARCHAR、BIGINT。</param>
/// <param name="ClrType">对应的 CLR 类型（可空值类型已取底层类型）。</param>
public sealed record DuckColumn(string Name, string DuckDbType, Type ClrType);
