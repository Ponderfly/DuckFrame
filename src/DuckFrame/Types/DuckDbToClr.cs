using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace DuckFrame;

/// <summary>读取方向的值转换（docs/spec.md §3.2）。</summary>
internal static class DuckDbToClr
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static object? ConvertValue(object? value, Type target)
    {
        if (value is null or DBNull)
        {
            if (target.IsValueType && Nullable.GetUnderlyingType(target) is null)
                throw new DuckFrameException($"列值为 NULL，无法赋给非可空类型 {target}。");
            return null;
        }

        var underlying = Nullable.GetUnderlyingType(target) ?? target;

        if (underlying == typeof(object) || underlying.IsInstanceOfType(value))
            return value;

        if (underlying.IsEnum)
        {
            return value is string name
                ? Enum.Parse(underlying, name, ignoreCase: true)
                : Enum.ToObject(underlying, value);
        }

        switch (value)
        {
            // 日期时间之间的互转。DuckDB.NET 把 TIMESTAMPTZ 读成 Kind=Unspecified 的 UTC 时间。
            case DateOnly d when underlying == typeof(DateTime):
                return d.ToDateTime(TimeOnly.MinValue);
            case DateTime dt when underlying == typeof(DateOnly):
                return DateOnly.FromDateTime(dt);
            case DateTime dt when underlying == typeof(DateTimeOffset):
                return new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt);
            case DateTimeOffset dto when underlying == typeof(DateTime):
                return dto.UtcDateTime;
            case TimeOnly t when underlying == typeof(TimeSpan):
                return t.ToTimeSpan();
            case TimeSpan ts when underlying == typeof(TimeOnly):
                return TimeOnly.FromTimeSpan(ts);

            // TimeSpanAsMicros 写入的 BIGINT。
            case long or int when underlying == typeof(TimeSpan):
                return TimeSpan.FromTicks(Convert.ToInt64(value, Invariant) * TimeSpan.TicksPerMicrosecond);

            // SUM(INTEGER) 等返回 HUGEINT，读回是 BigInteger（不实现 IConvertible）。超出范围时抛 OverflowException。
            case BigInteger big when underlying == typeof(double):
                return (double)big;
            case BigInteger big when underlying == typeof(float):
                return (float)big;
            case BigInteger big when underlying.IsPrimitive || underlying == typeof(decimal):
                return Convert.ChangeType((decimal)big, underlying, Invariant);

            case byte[] bytes when underlying == typeof(Guid):
                return new Guid(bytes);
            case Stream stream when underlying == typeof(byte[]):
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }

            // LIST 读回的是 List<T>：转成数组或其他集合类型。
            case IList list when ClrToDuckDb.TryGetElementType(underlying, out var element) && !IsDictionary(underlying):
                return ConvertList(list, underlying, element);
        }

        if (value is string text)
        {
            if (underlying == typeof(Guid)) return Guid.Parse(text);
            if (underlying == typeof(DateOnly)) return DateOnly.Parse(text, Invariant);
            if (underlying == typeof(TimeOnly)) return TimeOnly.Parse(text, Invariant);
            if (underlying == typeof(DateTime)) return DateTime.Parse(text, Invariant);
            if (underlying == typeof(DateTimeOffset)) return DateTimeOffset.Parse(text, Invariant);
            if (underlying == typeof(TimeSpan)) return TimeSpan.Parse(text, Invariant);
        }

        // JSON 列（嵌套对象、字典、复杂集合）读回的是文本；STRUCT / MAP 读回的是字典。
        if (IsJsonTarget(underlying))
        {
            var json = value as string ?? JsonSerializer.Serialize(value, value.GetType());
            return JsonSerializer.Deserialize(json, underlying);
        }

        if (value is IConvertible)
            return Convert.ChangeType(value, underlying, Invariant);

        throw new DuckFrameException($"无法把 {value.GetType()} 转换为 {underlying}。");
    }

    private static object ConvertList(IList list, Type target, Type element)
    {
        if (target.IsArray || target.IsAssignableFrom(element.MakeArrayType()))
        {
            var array = Array.CreateInstance(element, list.Count);
            for (var i = 0; i < list.Count; i++)
                array.SetValue(ConvertValue(list[i], element), i);
            return array;
        }

        // List<T>、IList<T>、ICollection<T>、IReadOnlyList<T> 等。
        var result = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element), list.Count)!;
        foreach (var item in list)
            result.Add(ConvertValue(item, element));

        return target.IsInstanceOfType(result)
            ? result
            : Activator.CreateInstance(target, result) ?? throw new DuckFrameException($"无法构造集合类型 {target}。");
    }

    private static bool IsJsonTarget(Type type) =>
        type != typeof(string) && !type.IsPrimitive && !type.IsEnum &&
        type != typeof(decimal) && ClrToDuckDb.ScalarType(type, new DuckSessionOptions()) is null &&
        type != typeof(byte[]);

    private static bool IsDictionary(Type type) =>
        typeof(IDictionary).IsAssignableFrom(type) ||
        type.GetInterfaces().Append(type).Any(i =>
            i.IsGenericType &&
            (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
             i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
}
