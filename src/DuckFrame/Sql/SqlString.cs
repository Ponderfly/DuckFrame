namespace DuckFrame;

/// <summary>把字符串标记为 SQL 标识符（表名 / 列名），由框架负责加引号与转义。</summary>
public sealed record SqlIdentifier(string Name);

/// <summary>把字符串原样拼进 SQL。仅用于完全可控的常量。</summary>
public sealed record SqlRaw(string Text);

/// <summary>SQL 插值的逃生舱。</summary>
public static class Sql
{
    /// <summary>把 name 当作标识符插入，自动加双引号并转义内部引号。</summary>
    public static SqlIdentifier Ident(string name) => new(name);

    /// <summary>把 sql 原样插入。不要传入来自用户输入的内容。</summary>
    [Obsolete("Sql.Raw 会把字符串原样拼进 SQL，仅在完全可控的常量上使用。")]
    public static SqlRaw Raw(string sql) => new(sql);
}
