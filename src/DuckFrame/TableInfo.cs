namespace DuckFrame;

/// <summary>已注册表的元信息。</summary>
/// <param name="Name">表名（未加引号）。</param>
/// <param name="RowCount">注册时的行数。</param>
/// <param name="Columns">列定义。</param>
public sealed record TableInfo(string Name, long RowCount, IReadOnlyList<DuckColumn> Columns);
