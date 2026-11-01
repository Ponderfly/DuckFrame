using System.Collections.Concurrent;
using System.Reflection;

namespace DuckFrame;

/// <summary>
/// 识别"字符串键字典行"类型的可枚举对象，并统一投影成
/// <see cref="IReadOnlyDictionary{TKey, TValue}"/>，使 <c>IEnumerable&lt;Dictionary&lt;string,T&gt;&gt;</c>
/// 也能走 RegisterRows 路径。
/// </summary>
internal static class DictionaryRows
{
    private static readonly ConcurrentDictionary<Type, Func<object?, IReadOnlyDictionary<string, object?>>?> Projectors = new();

    /// <summary>该元素类型是否可以被当作字典行。</summary>
    public static bool CanAdapt(Type type) =>
        GetProjector(type) is not null;

    /// <summary>返回把单个元素转换为字典行的委托；不是字典行时返回 null。</summary>
    public static Func<object?, IReadOnlyDictionary<string, object?>>? GetProjector(Type type) =>
        Projectors.GetOrAdd(type, static t => CreateProjector(t));

    private static Func<object?, IReadOnlyDictionary<string, object?>>? CreateProjector(Type type)
    {
        if (typeof(IReadOnlyDictionary<string, object?>).IsAssignableFrom(type))
            return item => (IReadOnlyDictionary<string, object?>)item!;

        if (typeof(System.Collections.IDictionary).IsAssignableFrom(type))
        {
            return item =>
            {
                var source = (System.Collections.IDictionary)item!;
                var result = new Dictionary<string, object?>(source.Count, StringComparer.OrdinalIgnoreCase);

                foreach (System.Collections.DictionaryEntry entry in source)
                {
                    if (entry.Key is not string key)
                    {
                        throw new DuckFrameException(
                            $"字典行的键必须为 string，实际为 {entry.Key?.GetType() ?? typeof(object)}。");
                    }

                    if (!result.TryAdd(key, entry.Value))
                        throw new DuckFrameException($"字典行存在重复列名 \"{key}\"（忽略大小写）。");
                }

                return result;
            };
        }

        // IReadOnlyDictionary<string, TValue> / IEnumerable<KeyValuePair<string, TValue>>
        foreach (var iface in type.GetInterfaces().Append(type))
        {
            if (!iface.IsGenericType || iface.GetGenericTypeDefinition() != typeof(IEnumerable<>))
                continue;

            var element = iface.GetGenericArguments()[0];
            if (!element.IsGenericType || element.GetGenericTypeDefinition() != typeof(KeyValuePair<,>))
                continue;

            var pairArgs = element.GetGenericArguments();
            if (pairArgs[0] != typeof(string))
                continue;

            var method = typeof(DictionaryRows)
                .GetMethod(nameof(ProjectGeneric), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(pairArgs[1]);

            return method.CreateDelegate<Func<object?, IReadOnlyDictionary<string, object?>>>();
        }

        return null;
    }

    private static IReadOnlyDictionary<string, object?> ProjectGeneric<TValue>(object? item)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in (IEnumerable<KeyValuePair<string, TValue>>)item!)
            if (!result.TryAdd(pair.Key, pair.Value))
                throw new DuckFrameException($"字典行存在重复列名 \"{pair.Key}\"（忽略大小写）。");

        return result;
    }
}
