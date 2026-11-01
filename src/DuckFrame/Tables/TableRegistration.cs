namespace DuckFrame;

/// <summary>一次注册的内部状态。</summary>
internal sealed class TableRegistration
{
    public required string Name { get; init; }

    public required string QualifiedName { get; init; }

    public required IReadOnlyList<ColumnDefinition> Columns { get; init; }

    public required TableRef Handle { get; init; }

    public required RefreshPolicy Policy { get; init; }

    /// <summary>重新生成行的方法；首写与 Refresh 共用，保证源只物化一次。</summary>
    public Func<IEnumerable<object?[]>>? Reload { get; init; }

    public bool Persistent { get; init; }

    public long RowCount { get; set; }
}
