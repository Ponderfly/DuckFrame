using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace DuckFrame;

/// <summary>把 object?[] 行构造为 T。表达式树编译并缓存，避免逐行反射。</summary>
internal static class Materializer
{
    private static readonly ConcurrentDictionary<Type, Func<object?, object?>> ScalarConverters = new();
    private static readonly ConcurrentDictionary<(Type Type, string Schema), Delegate> Cache = new();

    private static readonly MethodInfo ConvertValueMethod =
        typeof(DuckDbToClr).GetMethod(nameof(DuckDbToClr.ConvertValue))!;

    private static readonly MethodInfo ThrowNullMethod =
        typeof(Materializer).GetMethod(nameof(ThrowNull), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static Func<object?[], T> Get<T>(IReadOnlyList<DuckColumn> columns, DuckSessionOptions options)
    {
        var key = (typeof(T), SchemaKey(columns, options));
        return (Func<object?[], T>)Cache.GetOrAdd(key, _ => Build<T>(columns, options));
    }

    private static string SchemaKey(IReadOnlyList<DuckColumn> columns, DuckSessionOptions options) =>
        string.Join('|', columns.Select(c => c.Name + ":" + c.DuckDbType)) +
        $"#{options.MissingColumn}#{options.ExtraColumn}";

    private static Func<object?[], T> Build<T>(IReadOnlyList<DuckColumn> columns, DuckSessionOptions options)
    {
        var type = typeof(T);

        if (type == typeof(object))
        {
            return row => row.Length == 1 ? (T)row[0]! : throw new DuckFrameException("T 为 object 时结果必须只有一列。");
        }

        // Dictionary<string, object?>、IDictionary<…>、IReadOnlyDictionary<…> 都按“一行一个字典”处理。
        if (type.IsAssignableFrom(typeof(Dictionary<string, object?>)))
        {
            var names = columns.Select(c => c.Name).ToArray();
            return row =>
            {
                var dict = new Dictionary<string, object?>(row.Length, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < row.Length; i++)
                    dict[names[i]] = row[i];
                return (T)(object)dict;
            };
        }

        if (IsSingleValue(type))
        {
            if (columns.Count != 1)
                throw new DuckFrameException($"目标类型 {type} 按单列值映射，但查询返回了 {columns.Count} 列。");

            var convert = GetScalarConverter(type);
            return row => (T)convert(row[0])!;
        }

        return BuildObject<T>(columns, options);
    }

    /// <summary>标量与集合（LIST 列）都按单列值映射。</summary>
    private static bool IsSingleValue(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) ||
               t == typeof(Guid) || t == typeof(DateTime) || t == typeof(DateOnly) ||
               t == typeof(TimeOnly) || t == typeof(TimeSpan) || t == typeof(DateTimeOffset) ||
               t == typeof(byte[]) || t == typeof(System.Numerics.BigInteger) ||
               ClrToDuckDb.TryGetElementType(t, out _);
    }

    private static Func<object?, object?> GetScalarConverter(Type type) =>
        ScalarConverters.GetOrAdd(type, static t => value => DuckDbToClr.ConvertValue(value, t));

    private static Func<object?[], T> BuildObject<T>(IReadOnlyList<DuckColumn> columns, DuckSessionOptions options)
    {
        var type = typeof(T);
        var used = new HashSet<int>();

        var ctor = SelectConstructor(type);
        var parameters = ctor?.GetParameters() ?? [];

        var arguments = new List<(Type Type, int Ordinal)>(parameters.Length);
        foreach (var parameter in parameters)
        {
            // record 的 [property: Column("x")] 标在属性上，按同名属性取列名。
            var name = type.GetProperty(parameter.Name!)?.GetCustomAttribute<ColumnAttribute>()?.Name ?? parameter.Name!;
            var ordinal = NameMatcher.FindOrdinal(columns, name) ?? throw Missing(name, type, columns);
            used.Add(ordinal);
            arguments.Add((parameter.ParameterType, ordinal));
        }

        // 构造参数没覆盖到、且可公开写入（含 init）的属性。
        var covered = parameters.Select(p => p.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bindings = new List<(PropertyInfo Property, int Ordinal)>();

        foreach (var property in TypeInspector.GetReadableProperties(type))
        {
            if (property.SetMethod is not { IsPublic: true } || covered.Contains(property.Name))
                continue;

            var name = property.GetCustomAttribute<ColumnAttribute>()?.Name ?? property.Name;
            var ordinal = NameMatcher.FindOrdinal(columns, name);
            if (ordinal is null)
            {
                if (options.MissingColumn == MissingColumnBehavior.Throw)
                    throw Missing(name, type, columns);
                continue;
            }

            used.Add(ordinal.Value);
            bindings.Add((property, ordinal.Value));
        }

        if (options.ExtraColumn == ExtraColumnBehavior.Throw)
        {
            var extra = columns.Where((_, i) => !used.Contains(i)).Select(c => c.Name).ToArray();
            if (extra.Length > 0)
            {
                throw new DuckFrameException(
                    $"结果包含 {type} 未映射的列：{string.Join(", ", extra)}。" +
                    "设置 ExtraColumn = Ignore 或使用 [Column] 显式映射。");
            }
        }

        var row = Expression.Parameter(typeof(object?[]), "row");

        var create = ctor is null
            ? Expression.New(type)
            : Expression.New(ctor, arguments.Select(a => Read(row, columns, a.Ordinal, a.Type)));

        Expression body = bindings.Count == 0
            ? create
            : Expression.MemberInit(create, bindings.Select(b =>
                (MemberBinding)Expression.Bind(b.Property, Read(row, columns, b.Ordinal, b.Property.PropertyType))));

        return Expression.Lambda<Func<object?[], T>>(body, row).Compile();
    }

    /// <summary>生成读取 row[ordinal] 并转换为 target 的表达式。</summary>
    private static Expression Read(ParameterExpression row, IReadOnlyList<DuckColumn> columns, int ordinal, Type target)
    {
        Expression value = Expression.ArrayIndex(row, Expression.Constant(ordinal));

        // BLOB 在读取时已拷成 byte[]（见 DuckResult.ReadValue）。
        var stored = columns[ordinal].ClrType == typeof(Stream) ? typeof(byte[]) : columns[ordinal].ClrType;

        // 快速路径：列的 CLR 类型与成员一致时直接拆箱 / 强转，不走通用转换。
        if (Nullable.GetUnderlyingType(target) == stored || (!target.IsValueType && target.IsAssignableFrom(stored)))
            return Expression.Convert(value, target);

        if (target == stored)
        {
            return Expression.Condition(
                Expression.ReferenceEqual(value, Expression.Constant(null)),
                Expression.Call(ThrowNullMethod.MakeGenericMethod(target), Expression.Constant(columns[ordinal].Name)),
                Expression.Convert(value, target));
        }

        return Expression.Convert(
            Expression.Call(ConvertValueMethod, value, Expression.Constant(target, typeof(Type))),
            target);
    }

    private static TValue ThrowNull<TValue>(string column) =>
        throw new DuckFrameException($"列 \"{column}\" 的值为 NULL，无法赋给非可空类型 {typeof(TValue)}。");

    private static DuckFrameException Missing(string name, Type type, IReadOnlyList<DuckColumn> columns) =>
        new($"结果缺少 {type} 的成员 \"{name}\" 所需列。实际列为：{string.Join(", ", columns.Select(c => c.Name))}。");

    /// <summary>
    /// 构造函数选择顺序（docs/spec.md §3.2）：[DuckConstructor] → 参数与公共属性一一对应的主构造（record）
    /// → 无参构造 + 可写属性 → 参数最多的 public 构造。没有显式构造的 struct 返回 null。
    /// </summary>
    private static ConstructorInfo? SelectConstructor(Type type)
    {
        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (ctors.Length == 0)
            return type.IsValueType ? null : throw new DuckFrameException($"{type} 没有 public 构造函数，无法物化。");

        return ctors.FirstOrDefault(c => c.GetCustomAttribute<DuckConstructorAttribute>() is not null)
            ?? ctors
                .Where(c => c.GetParameters().Length > 0 && c.GetParameters().All(p =>
                    type.GetProperty(p.Name!, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is not null))
                .MaxBy(c => c.GetParameters().Length)
            ?? ctors.FirstOrDefault(c => c.GetParameters().Length == 0)
            ?? ctors.MaxBy(c => c.GetParameters().Length);
    }
}
