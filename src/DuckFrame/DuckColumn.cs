namespace DuckFrame;

/// <summary>查询结果中的一列。</summary>
/// <param name="Name">列名。</param>
/// <param name="DuckDbType">DuckDB 类型名，例如 VARCHAR、BIGINT。</param>
/// <param name="ClrType">对应的 CLR 类型（可空值类型已取底层类型）。</param>
public sealed record DuckColumn(string Name, string DuckDbType, Type ClrType)
{
    /// <summary>
    /// 为 true 时 <see cref="DuckDbType"/> 是按 CLR 类型推测的（例如多语句 SQL 返回的 LIST 拿不到原生元素类型）。
    /// 这样的结果可以正常读取，但不能再插进 SQL 当表用，以免按推测的类型重建时丢失精度。
    /// </summary>
    public bool IsTypeApproximate { get; init; }
}
