using System.Reflection;

namespace DuckFrame;

/// <summary>已注册表的句柄，可直接插入 SQL 插值字符串。</summary>
public abstract class TableRef : IFormattable
{
    private protected TableRef(string name, string qualifiedName)
    {
        Name = name;
        QualifiedName = qualifiedName;
    }

    /// <summary>未加引号的表名。</summary>
    public string Name { get; }

    /// <summary>已加引号的限定名，例如 temp."orders"。</summary>
    public string QualifiedName { get; }

    /// <summary>注册时写入的行数。</summary>
    public long RowCount { get; internal set; }

    /// <summary>源对象是否可能已变化（刷新策略为 Manual / OnQuery 时由刷新逻辑维护）。</summary>
    public bool IsStale { get; internal set; }

    /// <summary>插值到 SQL 时返回限定名。</summary>
    public string ToString(string? format, IFormatProvider? formatProvider) => QualifiedName;

    /// <inheritdoc />
    public override string ToString() => QualifiedName;

    internal static TableRef Create(Type sourceType, string name, string qualifiedName)
    {
        var closed = typeof(TableRef<>).MakeGenericType(sourceType);
        return (TableRef)Activator.CreateInstance(
            closed,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [name, qualifiedName],
            culture: null)!;
    }
}

/// <summary>已注册表的强类型句柄。</summary>
/// <typeparam name="TSource">注册时使用的源元素类型。</typeparam>
public sealed class TableRef<TSource> : TableRef
{
    internal TableRef(string name, string qualifiedName) : base(name, qualifiedName) { }
}
