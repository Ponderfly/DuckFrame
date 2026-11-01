using System.Collections;
using System.Data;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using DuckDB.NET.Data;

namespace DuckFrame;

/// <summary>
/// DuckDB 会话：注册内存对象为表、执行 SQL、物化结果。
/// 所有公开方法在内部串行化，因为 DuckDBConnection 不是线程安全的。
/// </summary>
public sealed class DuckSession : IDisposable, IAsyncDisposable
{
    private static readonly MethodInfo RegisterCoreMethod =
        typeof(DuckSession).GetMethod(nameof(RegisterCore), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly DuckDBConnection _connection;
    private readonly DuckSessionOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TableRegistration> _tables = new(StringComparer.OrdinalIgnoreCase);

    // 直接插进 SQL 的集合 / DataTable / DuckResult → 它们对应的表，按引用识别同一个对象。
    private readonly ConditionalWeakTable<object, TableRef> _inlineTables = new();
    private readonly List<WeakReference<DuckResult>> _results = [];

    private int _autoTableIndex;
    private int _autoQueryIndex;
    private bool _disposed;

    private DuckSession(DuckDBConnection connection, DuckSessionOptions options)
    {
        _connection = connection;
        _options = options;
    }

    /// <summary>打开一个内存数据库会话。</summary>
    public static DuckSession OpenInMemory(DuckSessionOptions? options = null)
        => Create(":memory:", options);

    /// <summary>打开（或创建）一个磁盘数据库文件。</summary>
    public static DuckSession Open(string path, DuckSessionOptions? options = null)
    {
        Guard.NotNull(path, nameof(path));
        return Create(path, options);
    }

    private static DuckSession Create(string dataSource, DuckSessionOptions? options)
    {
        options ??= new DuckSessionOptions();
        ValidatePolicy(options.RefreshPolicy);
        if (options.SampleSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.SampleSize));
        _ = ClrToDuckDb.GetColumnType(typeof(decimal), options);
        var builder = new System.Data.Common.DbConnectionStringBuilder { ["Data Source"] = dataSource };
        var connection = new DuckDBConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            return new DuckSession(connection, options);
        }
        catch { connection.Dispose(); throw; }
    }

    /// <summary>底层连接，供逃生舱使用。</summary>
    public DuckDBConnection Connection => _connection;

    /// <summary>最近一次执行的 SQL（含占位符）。</summary>
    public string? LastSql { get; private set; }

    /// <summary>已注册的表。</summary>
    public IReadOnlyList<TableInfo> Tables
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                CollectInlineTables();
                return _tables.Values
                    .Select(t => new TableInfo(
                        t.Name,
                        t.RowCount,
                        t.Columns.Select(c => new DuckColumn(c.Name, c.DuckDbType, c.ClrType)).ToArray()))
                    .ToArray();
            }
        }
    }

    // ---------------------------------------------------------------- 注册

    /// <summary>把可枚举对象注册为临时表，表名自动取变量名。</summary>
    public TableRef<T> Register<T>(
        IEnumerable<T>? source,
        string? name = null,
        [CallerArgumentExpression(nameof(source))] string? expr = null,
        TableOptions? options = null)
    {
        Guard.NotNull(source, nameof(source));
        lock (_gate)
        {
            ThrowIfDisposed();
            return RegisterCore(source, ResolveName(typeof(T), name, expr, options), options);
        }
    }

    /// <summary>把 DataTable 注册为临时表。</summary>
    public TableRef RegisterTable(
        DataTable table,
        string? name = null,
        [CallerArgumentExpression(nameof(table))] string? expr = null,
        TableOptions? options = null)
    {
        Guard.NotNull(table, nameof(table));
        lock (_gate)
        {
            ThrowIfDisposed();
            return RegisterTableCore(table, ResolveName(typeof(DataRow), name, expr, options), options);
        }
    }

    /// <summary>把字典行集合注册为临时表（列结构由采样推导）。</summary>
    public TableRef RegisterRows(
        IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        string? name = null,
        [CallerArgumentExpression(nameof(rows))] string? expr = null,
        TableOptions? options = null)
    {
        Guard.NotNull(rows, nameof(rows));
        lock (_gate)
        {
            ThrowIfDisposed();
            var elementType = typeof(IReadOnlyDictionary<string, object?>);
            return RegisterRowsCore(rows, ResolveName(elementType, name, expr, options), elementType, options);
        }
    }

    /// <summary>取已注册表。</summary>
    public TableRef GetTable(string name)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _tables.TryGetValue(name, out var registration)
                ? registration.Handle
                : throw new DuckFrameException($"表 \"{name}\" 未注册。");
        }
    }

    /// <summary>重新枚举注册时的源对象并重载表数据；失败时回滚，保留原数据。</summary>
    public void Refresh(string name)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            if (!_tables.TryGetValue(name, out var registration))
                throw new DuckFrameException($"表 \"{name}\" 未注册。");

            if (registration.InlineSource is { } weak && weak.TryGetTarget(out var source) && source is not DuckResult)
            {
                var refreshed = RegisterInline(source);
                registration.Handle.RowCount = refreshed.RowCount;
                registration.Handle.IsStale = false;
                return;
            }
            if (registration.Reload is null)
                throw new DuckFrameException($"表 \"{name}\" 来自查询结果，没有可重新读取的源对象。");

            var count = WriteTable(registration.QualifiedName, registration.Name, registration.Columns,
                registration.Reload(), registration.Persistent, replace: true);

            registration.RowCount = count;
            registration.Handle.RowCount = count;
            registration.Handle.IsStale = false;
        }
    }

    /// <summary>删除已注册的表。</summary>
    public void Drop(string name)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            if (!_tables.TryGetValue(name, out var registration))
                return;

            DropTable(registration.QualifiedName);
            _tables.Remove(name);
        }
    }

    private TableRef<T> RegisterCore<T>(IEnumerable<T> source, string name, TableOptions? options)
    {
        // List<Dictionary<string, object>> 这类“字典行”要按键推导列，不能当 POCO 去读 Keys / Values 属性。
        if (DictionaryRows.GetProjector(typeof(T)) is { } toRow)
        {
            var rows = source.Select((item, index) => item is null ? throw NullElement(name, index) : toRow(item));
            return (TableRef<T>)RegisterRowsCore(rows, name, typeof(T), options);
        }

        var columns = TypeInspector.GetColumns(typeof(T), _options, options);
        var project = RowAdapters.ForPoco<T>(columns);

        // 每次调用都重新枚举源：首次写入只遍历一次、行直接流进 appender；Refresh 时读到最新内容。
        IEnumerable<object?[]> ReadRows() =>
            source.Select((item, index) => item is null ? throw NullElement(name, index) : project(item));

        return (TableRef<T>)CreateTableHandle(name, typeof(T), columns, ReadRows(), ReadRows, options);
    }

    private TableRef RegisterTableCore(DataTable table, string name, TableOptions? options)
    {
        var columns = table.Columns.Cast<DataColumn>()
            .Select(c => new ColumnDefinition(
                c.ColumnName,
                ColumnTypeOverride(options, c.ColumnName) ?? c.ExtendedProperties["DuckFrame.DuckDbType"] as string
                    ?? ClrToDuckDb.GetColumnType(c.DataType, _options),
                c.DataType))
            .ToArray();

        IEnumerable<object?[]> ReadRows() => table.Rows.Cast<DataRow>()
            .Where(r => r.RowState != DataRowState.Deleted)
            .Select(RowAdapters.FromDataRow);

        return CreateTableHandle(name, typeof(DataRow), columns, ReadRows(), ReadRows, options);
    }

    private TableRef RegisterRowsCore(
        IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        string name,
        Type elementType,
        TableOptions? options)
    {
        // 只缓冲采样前缀；剩余行沿同一个枚举器直接写入，惰性数据源只枚举一次。
        using var enumerator = rows.GetEnumerator();
        var sample = new List<IReadOnlyDictionary<string, object?>>();
        while (sample.Count < _options.SampleSize && enumerator.MoveNext())
            sample.Add(enumerator.Current ?? throw NullElement(name, sample.Count));
        var columns = RowSchemaInference.Infer(sample, _options, options);
        var project = RowAdapters.ForDictionary(columns.Select(c => c.Name).ToArray());

        IEnumerable<object?[]> ReadInitial()
        {
            foreach (var row in sample) yield return project(row);
            var index = sample.Count;
            while (enumerator.MoveNext())
            {
                var row = enumerator.Current ?? throw NullElement(name, index);
                index++;
                yield return project(row);
            }
        }

        return CreateTableHandle(
            name,
            elementType,
            columns,
            ReadInitial(),
            () => rows.Select((row, index) => project(row ?? throw NullElement(name, index))),
            options);
    }

    private TableRef CreateTableHandle(
        string name,
        Type elementType,
        IReadOnlyList<ColumnDefinition> columns,
        IEnumerable<object?[]> rows,
        Func<IEnumerable<object?[]>>? reload,
        TableOptions? options)
    {
        ValidatePolicy(options?.RefreshPolicy ?? _options.RefreshPolicy);
        NameMatcher.EnsureUnique(columns.Select(c => c.Name));
        var persistent = options?.Persistent ?? false;
        var qualified = persistent ? PersistentName(name) : $"temp.main.{NameResolver.Quote(name)}";
        var count = WriteTable(qualified, name, columns, rows, persistent,
            replace: options?.Overwrite == true || _tables.ContainsKey(name));

        var handle = TableRef.Create(elementType, name, qualified);
        handle.RowCount = count;
        _tables[name] = new TableRegistration
        {
            Name = name, QualifiedName = qualified, Columns = columns, Handle = handle,
            Policy = options?.RefreshPolicy ?? _options.RefreshPolicy, Reload = reload,
            Persistent = persistent, RowCount = count,
        };
        return handle;
    }

    private string PersistentName(string name)
    {
        var (catalog, schema) = CurrentLocation();
        return $"{NameResolver.Quote(catalog)}.{NameResolver.Quote(schema)}.{NameResolver.Quote(name)}";
    }

    private (string Catalog, string Schema) CurrentLocation()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT current_database(), current_schema()";
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetString(0), reader.GetString(1));
    }

    private long WriteTable(string qualified, string name, IReadOnlyList<ColumnDefinition> columns,
        IEnumerable<object?[]> rows, bool persistent, bool replace)
    {
        // 替换时先完整写入独立的临时表，再以单条原子 DDL 发布。
        // 不启动嵌套事务，因此也能参与调用方已有的事务。
        var targetName = replace ? $"__duckframe_stage_{Guid.NewGuid():N}" : name;
        var target = replace ? $"temp.main.{NameResolver.Quote(targetName)}" : qualified;
        var created = false;
        try
        {
            CreateTableStatement(target, columns, persistent && !replace);
            created = true;
            // 明确 catalog/schema，避免同名 TEMP 表截获持久表的 appender。
            var location = replace || !persistent ? (Catalog: "temp", Schema: "main") : CurrentLocation();
            var count = AppenderWriter.Write(_connection, targetName, columns, rows, location.Catalog, location.Schema);
            if (replace)
            {
                using var publish = _connection.CreateCommand();
                publish.CommandText = $"CREATE OR REPLACE {(persistent ? "" : "TEMP ")}TABLE {qualified} AS SELECT * FROM {target}";
                Log(publish.CommandText);
                publish.ExecuteNonQuery();
                DropTable(target);
            }
            return count;
        }
        catch
        {
            // CREATE 失败时不能碰已有对象；清理失败不能掩盖原异常。
            if (created) { try { DropTable(target); } catch { } }
            throw;
        }
    }

    private void CreateTableStatement(string qualifiedName, IReadOnlyList<ColumnDefinition> columns, bool persistent)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE ")
          .Append(persistent ? "TABLE " : "TEMP TABLE ")
          .Append(qualifiedName)
          .Append(" (");

        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(NameResolver.Quote(columns[i].Name)).Append(' ').Append(columns[i].DuckDbType);
        }

        sb.Append(')');

        using var command = _connection.CreateCommand();
        command.CommandText = sb.ToString();
        Log(command.CommandText);
        command.ExecuteNonQuery();
    }

    private void DropTable(string qualifiedName)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"DROP TABLE IF EXISTS {qualifiedName}";
        command.ExecuteNonQuery();
    }

    private string ResolveName(Type elementType, string? explicitName, string? expr, TableOptions? options)
    {
        var candidate = NameResolver.Resolve(explicitName, expr, elementType)
                        ?? $"tbl_{Interlocked.Increment(ref _autoTableIndex)}";

        ValidatePolicy(options?.RefreshPolicy ?? _options.RefreshPolicy);
        if (options?.ColumnTypes is { } overrides) NameMatcher.EnsureUnique(overrides.Keys);
        CollectInlineTables();
        return options?.Overwrite == true ? candidate : NameResolver.MakeUnique(candidate, DatabaseNames());
    }

    private IEnumerable<string> DatabaseNames()
    {
        var names = new HashSet<string>(_tables.Keys, StringComparer.OrdinalIgnoreCase);
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT table_name FROM duckdb_tables() UNION SELECT view_name FROM duckdb_views()";
        using var reader = command.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private static void ValidatePolicy(RefreshPolicy policy)
    {
        if (policy == RefreshPolicy.OnQuery)
            throw new NotSupportedException("RefreshPolicy.OnQuery 尚未实现，请使用 Manual 并显式调用 Refresh()。");
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
    }

    private void CollectInlineTables()
    {
        foreach (var entry in _tables.Values.Where(t => t.InlineSource is { } source && !source.TryGetTarget(out _)).ToArray())
        {
            DropTable(entry.QualifiedName);
            _tables.Remove(entry.Name);
        }
    }

    private static string? ColumnTypeOverride(TableOptions? options, string column) =>
        options?.FindColumnType(column);

    private static DuckFrameException NullElement(string table, int index) =>
        new($"注册源 \"{table}\" 的第 {index} 个元素为 null。如需表示空行，请提供一个所有属性为 null 的对象，或改用 RegisterRows。");

    // ---------------------------------------------------------------- 查询

    /// <summary>执行插值 SQL；表引用直接写成表名，值自动参数化。</summary>
    /// <remarks>
    /// 插值字符串同时匹配本重载与 <see cref="Query(string, object?)"/>，
    /// C# 默认优先选择 string 重载（会把值 ToString() 拼进 SQL）。
    /// 这里用 OverloadResolutionPriority 强制选择插值版本，保证参数化与注入安全。
    /// </remarks>
    [OverloadResolutionPriority(1)]
    public DuckResult Query(FormattableString sql)
    {
        Guard.NotNull(sql, nameof(sql));
        lock (_gate)
        {
            ThrowIfDisposed();
            CollectInlineTables();
            var formatted = Format(sql);
            return ExecuteCore(formatted.Sql, formatted.Parameters);
        }
    }

    /// <summary>执行纯 SQL，可选命名参数对象（属性名即参数名，SQL 中写作 $name）。</summary>
    public DuckResult Query(string sql, object? parameters = null)
    {
        Guard.NotNull(sql, nameof(sql));
        lock (_gate)
        {
            ThrowIfDisposed();
            CollectInlineTables();
            return ExecuteCore(sql, BindParameters(parameters));
        }
    }

    /// <summary>异步查询（DuckDB 内核同步，此处只把调用移到线程池，不提供并行）。</summary>
    public Task<DuckResult> QueryAsync(FormattableString sql, CancellationToken ct = default)
    {
        Guard.NotNull(sql, nameof(sql));
        return Task.Run(() =>
        {
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                CollectInlineTables();
                var formatted = Format(sql);
                ct.ThrowIfCancellationRequested();
                return ExecuteCore(formatted.Sql, formatted.Parameters, ct);
            }
        }, ct);
    }

    /// <summary>执行非查询语句，返回受影响行数。</summary>
    public int Execute(FormattableString sql)
    {
        Guard.NotNull(sql, nameof(sql));
        lock (_gate)
        {
            ThrowIfDisposed();
            var formatted = Format(sql);

            using var command = _connection.CreateCommand();
            command.CommandText = formatted.Sql;
            foreach (var parameter in formatted.Parameters)
                command.Parameters.Add(parameter);

            Log(formatted.Sql);
            return command.ExecuteNonQuery();
        }
    }

    /// <summary>执行并返回第一行第一列；没有行时返回 default。</summary>
    public T? Scalar<T>(FormattableString sql)
    {
        using var result = Query(sql);
        var value = result.FirstValue();
        return value is null ? default : (T?)DuckDbToClr.ConvertValue(value, typeof(T));
    }

    private SqlFormatter.Formatted Format(FormattableString sql)
    {
        var state = new ParameterState();
        return SqlFormatter.Format(sql, (argument, parameters) => RenderArgument(argument, parameters, state));
    }

    private DuckResult ExecuteCore(string sql, IReadOnlyList<DuckDBParameter> parameters, CancellationToken ct = default)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.Add(parameter);

        DuckDBDataReader? reader = null;
        using var cancellation = ct.Register(command.Cancel);
        try
        {
            Log(sql);
            ct.ThrowIfCancellationRequested();
            reader = command.ExecuteReader();
            ct.ThrowIfCancellationRequested();

            // DECIMAL 的精度只能从 schema table 取，按需取一次。
            DataTable? schema = null;
            var schemaLoaded = false;
            DataTable? Schema()
            {
                if (!schemaLoaded)
                {
                    schemaLoaded = true;
                    try { schema = reader.GetSchemaTable(); } catch (NotSupportedException) { }
                }
                return schema;
            }

            var columns = new List<DuckColumn>(reader.FieldCount);
            string?[]? nestedTypes = null;
            string? NestedType(int ordinal) =>
                (nestedTypes ??= NativeResultSchema.Read(_connection, sql, parameters, reader.FieldCount))[ordinal];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(new DuckColumn(
                    reader.GetName(i),
                    DuckDbTypes.FromReader(reader, i, Schema, _options, NestedType),
                    ResultValueReader.StoredType(reader.GetFieldType(i))));
            }

            var result = new DuckResult(reader, command, columns, _options, _gate);
            _results.RemoveAll(r => !r.TryGetTarget(out var previous) || previous.IsReleased);
            _results.Add(new WeakReference<DuckResult>(result));
            return result;
        }
        catch (Exception ex)
        {
            cancellation.Dispose();
            reader?.Dispose();
            command.Dispose();
            if (ct.IsCancellationRequested) throw new OperationCanceledException("查询已取消。", ex, ct);
            throw;
        }
    }

    /// <summary>渲染过程中的可变状态（lambda 无法捕获 ref 参数）。</summary>
    private sealed class ParameterState
    {
        public int Index;
        public readonly Dictionary<object, TableRef> Tables = new(ReferenceEqualityComparer.Instance);
    }

    /// <summary>把插值参数渲染为 SQL 片段（docs/spec.md §5.1）。</summary>
    private string RenderArgument(object? argument, IList<DuckDBParameter> parameters, ParameterState state)
    {
        switch (argument)
        {
            case TableRef table:
                return table.QualifiedName;

            case SqlIdentifier identifier:
                return NameResolver.Quote(identifier.Name);

            case SqlRaw raw:
                return raw.Text;

            case DuckResult result:
                return InlineOnce(result, state, () => RegisterResult(result)).QualifiedName;

            case DataTable dataTable:
                return InlineOnce(dataTable, state, () => RegisterInline(dataTable)).QualifiedName;

            // 字符串永远是值，不会被当成标识符或表。
            case string or char or byte[]:
                return AddParameter(argument, parameters, state);

            // 标量集合是一个 LIST 值：WHERE Id = ANY({ids})。
            case IEnumerable enumerable when IsScalarCollection(enumerable):
                return AddParameter(argument, parameters, state);

            // 对象集合是一张表。
            case IEnumerable enumerable:
                return InlineOnce(enumerable, state, () => RegisterInline(enumerable)).QualifiedName;

            default:
                return AddParameter(argument, parameters, state);
        }
    }

    private static TableRef InlineOnce(object source, ParameterState state, Func<TableRef> register)
    {
        if (state.Tables.TryGetValue(source, out var existing)) return existing;
        var table = register();
        state.Tables.Add(source, table);
        return table;
    }

    private static string AddParameter(object? value, IList<DuckDBParameter> parameters, ParameterState state)
    {
        // SQL 里写 $p0，ParameterName 写 p0（不带 $）；:p0 是语法错误，@p0 会被当成列名（已实测）。
        var name = $"p{state.Index++}";
        parameters.Add(new DuckDBParameter
        {
            ParameterName = name,
            Value = PrepareParameterValue(value),
        });
        return "$" + name;
    }

    private bool IsScalarCollection(IEnumerable enumerable) =>
        ClrToDuckDb.TryGetElementType(enumerable.GetType(), out var element) &&
        ClrToDuckDb.ScalarType(Nullable.GetUnderlyingType(element) ?? element, _options) is not null;

    private static object PrepareParameterValue(object? value)
    {
        // 惰性 LIST 参数只枚举一次，后续元数据分析复用已缓冲的数组。
        if (value is IEnumerable and not string and not IList &&
            ClrToDuckDb.TryGetElementType(value.GetType(), out var element) &&
            ClrToDuckDb.ScalarType(Nullable.GetUnderlyingType(element) ?? element, new DuckSessionOptions()) is not null)
        {
            return typeof(Enumerable).GetMethod(nameof(Enumerable.ToArray))!.MakeGenericMethod(element)
                .Invoke(null, BindingFlags.DoNotWrapExceptions, binder: null, parameters: [value], culture: null)!;
        }
        return value ?? DBNull.Value;
    }

    /// <summary>
    /// 注册直接插进 SQL 的集合 / DataTable。同一个对象反复插值时沿用同一张表并重新载入：
    /// 和 Python 的 replacement scan 一样每次读到最新内容，又不会在循环查询里堆积临时表。
    /// </summary>
    private TableRef RegisterInline(object source)
    {
        var name = _inlineTables.TryGetValue(source, out var previous) &&
                   _tables.TryGetValue(previous.Name, out var entry) && ReferenceEquals(entry.Handle, previous)
            ? previous.Name
            : NameResolver.MakeUnique($"__p{Interlocked.Increment(ref _autoTableIndex)}", DatabaseNames());

        var handle = source switch
        {
            DataTable table => RegisterTableCore(table, name, options: null),
            IEnumerable enumerable => RegisterEnumerable(enumerable, name),
            _ => throw new DuckFrameException($"{source.GetType()} 不能作为表插入 SQL。"),
        };

        _inlineTables.AddOrUpdate(source, handle);
        _tables[name].Reload = null;
        _tables[name].InlineSource = new WeakReference<object>(source);
        return handle;
    }

    private TableRef RegisterEnumerable(IEnumerable enumerable, string name)
    {
        if (!ClrToDuckDb.TryGetElementType(enumerable.GetType(), out var elementType))
        {
            throw new DuckFrameException(
                $"无法确定 {enumerable.GetType()} 的元素类型，请先调用 Register<T>() 再插值返回的表。");
        }

        return (TableRef)RegisterCoreMethod
            .MakeGenericMethod(elementType)
            .Invoke(this, BindingFlags.DoNotWrapExceptions, binder: null, [enumerable, name, null], culture: null)!;
    }

    /// <summary>注册查询结果。结果集不可变，同一个结果多次插值时直接复用已注册的表。</summary>
    private TableRef RegisterResult(DuckResult result)
    {
        if (_inlineTables.TryGetValue(result, out var existing) &&
            _tables.TryGetValue(existing.Name, out var registration) && ReferenceEquals(registration.Handle, existing))
            return existing;

        var name = NameResolver.MakeUnique($"__q{Interlocked.Increment(ref _autoQueryIndex)}", DatabaseNames());
        var columns = result.Columns
            .Select(c => new ColumnDefinition(c.Name, c.DuckDbType, c.ClrType))
            .ToArray();

        var handle = CreateTableHandle(name, typeof(DuckResult), columns, result.Materialize().Rows, reload: null, options: null);
        _inlineTables.AddOrUpdate(result, handle);
        _tables[name].InlineSource = new WeakReference<object>(result);
        return handle;
    }

    // ---------------------------------------------------------------- 其他

    private static List<DuckDBParameter> BindParameters(object? parameters)
    {
        if (parameters is null)
            return [];

        return parameters.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => new DuckDBParameter { ParameterName = p.Name, Value = PrepareParameterValue(p.GetValue(parameters)) })
            .ToList();
    }

    private void Log(string sql)
    {
        LastSql = sql;
        _options.LogSql?.Invoke(sql);
    }

    private void ThrowIfDisposed() => Guard.NotDisposed(_disposed, nameof(DuckSession));

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            Exception? failure = null;
            foreach (var weak in _results)
            {
                if (!weak.TryGetTarget(out var result) || result.IsReleased) continue;
                try { result.Dispose(); }
                catch (Exception ex) { failure ??= ex; }
            }
            _results.Clear();
            _tables.Clear();
            _inlineTables.Clear();
            try { _connection.Dispose(); }
            catch (Exception ex) { failure ??= ex; }
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
