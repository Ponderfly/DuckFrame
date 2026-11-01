using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using DuckDB.NET.Data;

namespace DuckFrame;

/// <summary>读取 LIST 时保留 NULL，并在释放原生结果前复制所有层级的 BLOB。</summary>
internal static class ResultValueReader
{
    private static readonly ConcurrentDictionary<Type, Func<DuckDBDataReader, int, object?>> Readers = new();

    public static Func<DuckDBDataReader, int, object?> For(Type type) => Readers.GetOrAdd(type, Create);

    public static Type StoredType(Type type)
    {
        if (typeof(Stream).IsAssignableFrom(type)) return typeof(byte[]);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            return typeof(List<>).MakeGenericType(StoredType(NullableElement(type.GetGenericArguments()[0])));
        return type;
    }

    private static Type NullableElement(Type element) => element.IsValueType && Nullable.GetUnderlyingType(element) is null
        ? typeof(Nullable<>).MakeGenericType(element) : element;

    private static Type ReadType(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)
        ? typeof(List<>).MakeGenericType(ReadType(NullableElement(type.GetGenericArguments()[0]))) : type;

    private static Func<DuckDBDataReader, int, object?> Create(Type source)
    {
        var target = ReadType(source);
        if (target == source) return static (reader, index) => Own(reader.GetValue(index));
        var reader = Expression.Parameter(typeof(DuckDBDataReader));
        var index = Expression.Parameter(typeof(int));
        var get = Expression.Call(reader, nameof(DuckDBDataReader.GetFieldValue), [target], index);
        var getter = Expression.Lambda<Func<DuckDBDataReader, int, object?>>(
            Expression.Convert(get, typeof(object)), reader, index).Compile();
        return (r, i) => r.IsDBNull(i) ? null : Own(getter(r, i));
    }

    private static object? Own(object? value)
    {
        switch (value)
        {
            case DBNull: return null;
            case Stream stream:
                using (stream)
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                }
            case byte[]: return value;
            case IList list:
                var type = StoredType(list.GetType());
                var result = type == list.GetType() ? list : (IList)Activator.CreateInstance(type)!;
                for (var i = 0; i < list.Count; i++)
                    if (ReferenceEquals(result, list)) result[i] = Own(list[i]);
                    else result.Add(Own(list[i]));
                return result;
            case IDictionary dictionary:
                foreach (var key in dictionary.Keys.Cast<object>().ToArray()) dictionary[key] = Own(dictionary[key]);
                return dictionary;
            default: return value;
        }
    }
}
