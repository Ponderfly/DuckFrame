namespace DuckFrame;

/// <summary>注册后的表在源对象变化时如何刷新。</summary>
public enum RefreshPolicy
{
    /// <summary>注册即快照，之后永不检查（默认，最快）。</summary>
    Immutable,

    /// <summary>只有显式调用 <see cref="DuckSession.Refresh"/> 才重载。</summary>
    Manual,

    /// <summary>预留的自动刷新策略；尚未实现，选择时抛出 NotSupportedException。</summary>
    OnQuery,
}

/// <summary>查询结果缺少目标类型的成员时的行为。</summary>
public enum MissingColumnBehavior
{
    /// <summary>抛出 <see cref="DuckFrameException"/>（默认）。</summary>
    Throw,

    /// <summary>使用成员默认值。</summary>
    DefaultValue,
}

/// <summary>查询结果包含目标类型之外的列时的行为。</summary>
public enum ExtraColumnBehavior
{
    /// <summary>抛出 <see cref="DuckFrameException"/>（默认）。</summary>
    Throw,

    /// <summary>忽略多余列。</summary>
    Ignore,
}

/// <summary>查询结果出现同名列（忽略大小写）时的行为。</summary>
public enum DuplicateColumnBehavior
{
    /// <summary>
    /// 按 DuckDB 自身规则重命名（默认）：后出现的同名列依次加 _1、_2…，仍冲突时继续追加，
    /// 例如 <c>a, a, a_1</c> → <c>a, a_1, a_1_1</c>。与 DuckDB 用该结果建表时得到的列名一致。
    /// </summary>
    Rename,

    /// <summary>保留原列名；To&lt;T&gt;、ToDictionaries、ToDataTable、再次插值等需要唯一列名的操作会报错。</summary>
    Throw,
}

/// <summary>会话级配置。</summary>
public sealed class DuckSessionOptions
{
    /// <summary>类型推断时采样的最大行数。</summary>
    public int SampleSize { get; init; } = 1000;

    /// <summary>默认刷新策略。</summary>
    public RefreshPolicy RefreshPolicy { get; init; } = RefreshPolicy.Immutable;

    /// <summary>decimal 的默认精度。</summary>
    public int DecimalPrecision { get; init; } = 18;

    /// <summary>decimal 的默认小数位。</summary>
    public int DecimalScale { get; init; } = 4;

    /// <summary>枚举写成 INTEGER 而不是 VARCHAR。</summary>
    public bool EnumsAsInt { get; init; }

    /// <summary>DateTime 写成 TIMESTAMPTZ 而不是 TIMESTAMP。</summary>
    public bool UtcTimestamps { get; init; }

    /// <summary>TimeSpan 写成 BIGINT（微秒）而不是 INTERVAL。</summary>
    public bool TimeSpanAsMicros { get; init; }

    /// <summary>结果缺少成员时的行为。</summary>
    public MissingColumnBehavior MissingColumn { get; init; } = MissingColumnBehavior.Throw;

    /// <summary>结果包含多余列时的行为。</summary>
    public ExtraColumnBehavior ExtraColumn { get; init; } = ExtraColumnBehavior.Throw;

    /// <summary>结果出现同名列时的行为，默认按 DuckDB 规则重命名。</summary>
    public DuplicateColumnBehavior DuplicateColumns { get; init; } = DuplicateColumnBehavior.Rename;

    /// <summary>执行 SQL 前的日志回调，默认关闭。</summary>
    public Action<string>? LogSql { get; init; }
}
