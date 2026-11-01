using System.Data;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace DuckFrame;

/// <summary>把各类源对象投影成 object?[] 行，供 appender 写入。</summary>
internal static class RowAdapters
{
    private static class AccessorCache<T>
    {
        internal static readonly ConcurrentDictionary<PropertyInfo, Func<T, object?>> Accessors = new();
    }
    /// <summary>POCO → object?[]，属性访问器用表达式树编译并缓存。</summary>
    public static Func<T, object?[]> ForPoco<T>(IReadOnlyList<ColumnDefinition> columns)
    {
        var accessors = new Func<T, object?>[columns.Count];
        var properties = TypeInspector.GetReadableProperties(typeof(T));

        for (var i = 0; i < columns.Count; i++)
        {
            var property = properties.First(p =>
                (p.GetCustomAttribute<ColumnAttribute>()?.Name ?? p.Name) == columns[i].Name);

            accessors[i] = AccessorCache<T>.Accessors.GetOrAdd(property, CompileAccessor<T>);
        }

        return item =>
        {
            var row = new object?[accessors.Length];
            for (var i = 0; i < accessors.Length; i++)
                row[i] = accessors[i](item);
            return row;
        };
    }

    private static Func<T, object?> CompileAccessor<T>(PropertyInfo property)
    {
        var param = Expression.Parameter(typeof(T), "x");
        Expression body = Expression.Property(param, property);
        if (property.PropertyType.IsValueType)
            body = Expression.Convert(body, typeof(object));

        return Expression.Lambda<Func<T, object?>>(body, param).Compile();
    }

    /// <summary>Dictionary 行 → object?[]，列顺序固定。</summary>
    public static Func<IReadOnlyDictionary<string, object?>, object?[]> ForDictionary(IReadOnlyList<string> columnNames)
    {
        NameMatcher.EnsureUnique(columnNames);
        var ordinals = columnNames.Select((name, index) => (name, index))
            .ToDictionary(c => c.name, c => c.index, StringComparer.OrdinalIgnoreCase);
        return row =>
        {
            var values = new object?[columnNames.Count];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in row)
            {
                if (!seen.Add(name))
                    throw new DuckFrameException($"字典行存在重复列名 \"{name}\"（忽略大小写）。");
                if (!ordinals.TryGetValue(name, out var ordinal))
                    throw new DuckFrameException($"字典行出现采样中没有的列 \"{name}\"，请增加 SampleSize 或通过 ColumnTypes 声明列。");
                values[ordinal] = value is DBNull ? null : value;
            }
            return values;
        };
    }

    /// <summary>DataRow → object?[]。</summary>
    public static object?[] FromDataRow(DataRow row) =>
        row.ItemArray.Select(static v => v is DBNull ? null : v).ToArray();
}
