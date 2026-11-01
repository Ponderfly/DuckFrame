namespace DuckFrame;

/// <summary>单次注册的选项，覆盖会话级配置。</summary>
public sealed class TableOptions
{
    /// <summary>建普通表而不是 TEMP 表。</summary>
    public bool Persistent { get; init; }

    /// <summary>同名表已注册时直接替换（CREATE OR REPLACE），而不是改名为 name_2。</summary>
    public bool Overwrite { get; init; }

    /// <summary>覆盖会话级的刷新策略。</summary>
    public RefreshPolicy? RefreshPolicy { get; init; }

    /// <summary>列名到 DuckDB 类型字符串的显式覆盖，例如 "Amount" -> "DECIMAL(38,10)"。</summary>
    public IReadOnlyDictionary<string, string>? ColumnTypes { get; init; }
}
