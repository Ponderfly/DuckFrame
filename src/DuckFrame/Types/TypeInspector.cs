using System.Collections.Concurrent;
using System.Reflection;

namespace DuckFrame;

/// <summary>POCO 的列推导。</summary>
internal static class TypeInspector
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

    public static PropertyInfo[] GetReadableProperties(Type type) =>
        PropertyCache.GetOrAdd(type, static t => t
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.MetadataToken)
            .ToArray());

    public static IReadOnlyList<ColumnDefinition> GetColumns(
        Type elementType,
        DuckSessionOptions options,
        TableOptions? tableOptions)
    {
        if (elementType == typeof(object) || DictionaryRows.CanAdapt(elementType))
        {
            throw new DuckFrameException(
                $"无法从 {elementType} 推导列结构，请改用 RegisterRows 或提供 TableOptions.ColumnTypes。");
        }

        if (ClrToDuckDb.ScalarType(Nullable.GetUnderlyingType(elementType) ?? elementType, options) is not null)
        {
            throw new DuckFrameException(
                $"{elementType} 是标量类型，标量集合不会注册成表：在 SQL 里直接插值即可作为 LIST 参数，" +
                "例如 WHERE Id = ANY({ids}) 或 WHERE Id IN (SELECT unnest({ids}))。");
        }

        var properties = GetReadableProperties(elementType);
        if (properties.Length == 0)
        {
            throw new DuckFrameException(
                $"{elementType} 没有可读的公开实例属性，无法推导列结构。");
        }

        var columns = new List<ColumnDefinition>(properties.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in properties)
        {
            var name = property.GetCustomAttribute<ColumnAttribute>()?.Name ?? property.Name;

            if (!seen.Add(name))
            {
                throw new DuckFrameException(
                    $"{elementType} 中存在重复列名 \"{name}\"，请用 [Column] 区分。");
            }

            var type = tableOptions?.ColumnTypes is { } overrides && overrides.TryGetValue(name, out var explicitType)
                ? explicitType
                : ClrToDuckDb.GetColumnType(property.PropertyType, options, property);

            columns.Add(new ColumnDefinition(name, type, property.PropertyType));
        }

        return columns;
    }
}
