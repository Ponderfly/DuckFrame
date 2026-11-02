namespace DuckFrame;

/// <summary>自动注册表的生命周期。</summary>
internal enum AutoTableLifetime
{
    /// <summary>显式注册，直到 Drop 或会话释放。</summary>
    None,

    /// <summary>插值的集合 / DataTable：使用它的查询结果释放 reader 后删除。</summary>
    Query,

    /// <summary>插值的 DuckResult：源结果被 Dispose（或回收）后删除。</summary>
    Result,
}

/// <summary>一次注册的内部状态。</summary>
internal sealed class TableRegistration
{
    public required string Name { get; init; }

    public required string QualifiedName { get; init; }

    public required IReadOnlyList<ColumnDefinition> Columns { get; init; }

    public required TableRef Handle { get; init; }

    public required RefreshPolicy Policy { get; init; }

    /// <summary>重新生成行的方法；首写与 Refresh 共用，保证源只物化一次。</summary>
    public Func<IEnumerable<object?[]>>? Reload { get; set; }

    public AutoTableLifetime Lifetime { get; set; }

    /// <summary>决定自动表何时删除的结果；只弱引用，不延长结果的生命周期。</summary>
    public WeakReference<DuckResult>? Owner { get; set; }

    public bool Persistent { get; init; }

    public long RowCount { get; set; }
}
