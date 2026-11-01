namespace DuckFrame;

/// <summary>内部列定义：名称、DuckDB 类型（DDL 写法）、CLR 类型。</summary>
internal sealed record ColumnDefinition(string Name, string DuckDbType, Type ClrType);
