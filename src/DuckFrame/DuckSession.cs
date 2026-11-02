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

    // 插进 SQL 的 DuckResult → 它对应的表；结果不可变，同一个结果反复插值时复用。
    private readonly ConditionalWeakTable<DuckResult, TableRef> _resultTables = new();
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
        if (!Enum.IsDefined(options.DuplicateColumns))
            throw new ArgumentOutOfRangeException(nameof(options.DuplicateColumns));
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
                CollectAutoTables();
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

            if (registration.Reload is null)
                throw new DuckFrameException($"表 \"{name}\" 是插值时自动注册的临时表，没有可重新读取的源对象；需要刷新请改用 Register()。");

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
        _tables.TryGetValue(name, out var previous);
        var count = WriteTable(qualified, name, columns, rows, persistent,
            replace: options?.Overwrite == true || previous is not null);

        var handle = TableRef.Create(elementType, name, qualified);
        handle.RowCount = count;
        _tables[name] = new TableRegistration
        {
            Name = name, QualifiedName = qualified, Columns = columns, Handle = handle,
            Policy = options?.RefreshPolicy ?? _options.RefreshPolicy, Reload = reload,
            Persistent = persistent, RowCount = count,
        };

        // 同名覆盖时如果持久 / 临时位置变了，旧表不会被 CREATE OR REPLACE 覆盖到，需要单独删除。
        // 只删本会话登记过的表，用户自己建的同名表不动。
        if (previous is not null && !string.Equals(previous.QualifiedName, qualified, StringComparison.Ordinal))
        {
            try { DropTable(previous.QualifiedName); }
            catch (Exception ex)
            {
                throw new DuckFrameException(
                    $"新表 {qualified} 已发布，但删除原来的 {previous.QualifiedName} 失败，请手动删除。", ex);
            }
        }
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
        CollectAutoTables();
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

    /// <summary>
    /// 删除生命周期已结束的自动注册表。正常情况下由结果的通知即时触发（见 <see cref="OnResultLifetimeChanged"/>）；
    /// 这里在每次查询 / 注册时兜底处理结果未 Dispose 就被回收、或之前删除失败的表。
    /// </summary>
    private void CollectAutoTables(DuckResult? owner = null)
    {
        foreach (var entry in _tables.Values.Where(t => IsExpired(t, owner)).ToArray())
        {
            // 删除失败（例如调用方事务已中止）时保留登记，下次再试；不能影响当前操作。
            try { DropTable(entry.QualifiedName); }
            catch { continue; }
            _tables.Remove(entry.Name);
        }
    }

    private static bool IsExpired(TableRegistration table, DuckResult? only)
    {
        if (table.Lifetime == AutoTableLifetime.None || table.Owner is null) return false;
        if (!table.Owner.TryGetTarget(out var owner)) return true;
        if (only is not null && !ReferenceEquals(owner, only)) return false;
        return table.Lifetime == AutoTableLifetime.Query ? owner.IsReleased : owner.IsDisposed;
    }

    private void OnResultLifetimeChanged(DuckResult result)
    {
        lock (_gate)
        {
            // 会话释放时连接随之关闭，临时表自然消失。
            if (!_disposed) CollectAutoTables(result);
        }
    }

    private void DropOwnedTables(ParameterState state)
    {
        foreach (var table in state.Owned)
        {
            try { DropTable(table.QualifiedName); }
            catch { continue; }
            _tables.Remove(table.Name);
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
            CollectAutoTables();
            return QueryCore(sql, CancellationToken.None);
        }
    }

    /// <summary>执行纯 SQL，可选命名参数对象（属性名即参数名，SQL 中写作 $name）。</summary>
    public DuckResult Query(string sql, object? parameters = null)
    {
        Guard.NotNull(sql, nameof(sql));
        lock (_gate)
        {
            ThrowIfDisposed();
            CollectAutoTables();
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
                CollectAutoTables();
                return QueryCore(sql, ct);
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
            CollectAutoTables();
            var state = new ParameterState();
            try
            {
                var formatted = Format(sql, state);

                using var command = _connection.CreateCommand();
                command.CommandText = formatted.Sql;
                foreach (var parameter in formatted.Parameters)
                    command.Parameters.Add(parameter);

                Log(formatted.Sql);
                return command.ExecuteNonQuery();
            }
            finally
            {
                // 语句执行完就不再需要插值进来的集合表。
                DropOwnedTables(state);
            }
        }
    }

    /// <summary>执行并返回第一行第一列；没有行时返回 default。</summary>
    public T? Scalar<T>(FormattableString sql)
    {
        using var result = Query(sql);
        var value = result.FirstValue();
        return value is null ? default : (T?)DuckDbToClr.ConvertValue(value, typeof(T));
    }

    private SqlFormatter.Formatted Format(FormattableString sql, ParameterState state) =>
        SqlFormatter.Format(sql, (argument, parameters) => RenderArgument(argument, parameters, state));

    private DuckResult QueryCore(FormattableString sql, CancellationToken ct)
    {
        var state = new ParameterState();
        try
        {
            var formatted = Format(sql, state);
            ct.ThrowIfCancellationRequested();
            var result = ExecuteCore(formatted.Sql, formatted.Parameters, ct);

            // 插值进来的集合表归这个结果所有：结果读完、释放或被回收后删除。
            var owner = new WeakReference<DuckResult>(result);
            foreach (var table in state.Owned) table.Owner = owner;
            return result;
        }
        catch
        {
            DropOwnedTables(state);
            throw;
        }
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
            var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            if (_options.DuplicateColumns == DuplicateColumnBehavior.Rename)
                names = NameMatcher.Deduplicate(names);
            string?[]? nestedTypes = null;
            string? NestedType(int ordinal) =>
                (nestedTypes ??= NativeResultSchema.Read(_connection, sql, parameters, reader.FieldCount))[ordinal];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var type = DuckDbTypes.FromReader(reader, i, Schema, _options, NestedType, out var approximate);
                columns.Add(new DuckColumn(names[i], type, ResultValueReader.StoredType(reader.GetFieldType(i)))
                {
                    IsTypeApproximate = approximate,
                });
            }

            var result = new DuckResult(reader, command, columns, _options, _gate) { LifetimeChanged = OnResultLifetimeChanged };
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

        /// <summary>本次插值新建、生命周期跟随这条语句的表。</summary>
        public readonly List<TableRegistration> Owned = [];
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
                return InlineOnce(dataTable, state, () => RegisterInline(dataTable, state)).QualifiedName;

            // 字符串永远是值，不会被当成标识符或表。
            case string or char or byte[]:
                return AddParameter(argument, parameters, state);

            // 标量集合是一个 LIST 值：WHERE Id = ANY({ids})。
            case IEnumerable enumerable when IsScalarCollection(enumerable):
                return AddParameter(argument, parameters, state);

            // 对象集合是一张表。
            case IEnumerable enumerable:
                return InlineOnce(enumerable, state, () => RegisterInline(enumerable, state)).QualifiedName;

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
    /// 注册直接插进 SQL 的集合 / DataTable。和 Python 的 replacement scan 一样，每条语句都读到源的最新内容；
    /// 表只在这条语句（及其结果的 reader）存续期间存在，循环查询不会堆积临时表，也不持有源对象。
    /// </summary>
    private TableRef RegisterInline(object source, ParameterState state)
    {
        var name = NameResolver.MakeUnique($"__p{Interlocked.Increment(ref _autoTableIndex)}", DatabaseNames());

        var handle = source switch
        {
            DataTable table => RegisterTableCore(table, name, options: null),
            IEnumerable enumerable => RegisterEnumerable(enumerable, name),
            _ => throw new DuckFrameException($"{source.GetType()} 不能作为表插入 SQL。"),
        };

        var registration = _tables[name];
        registration.Reload = null;
        registration.Lifetime = AutoTableLifetime.Query;
        state.Owned.Add(registration);
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
        if (_resultTables.TryGetValue(result, out var existing) &&
            _tables.TryGetValue(existing.Name, out var registration) && ReferenceEquals(registration.Handle, existing))
            return existing;

        if (result.Columns.FirstOrDefault(c => c.IsTypeApproximate) is { } approximate)
        {
            throw new DuckFrameException(
                $"结果列 \"{approximate.Name}\" 的 LIST 元素类型无法确定（多语句 SQL 取不到原生元数据），" +
                "按推测类型重建表可能丢失精度。请把产生该结果的查询拆成单条 SQL。");
        }

        var name = NameResolver.MakeUnique($"__q{Interlocked.Increment(ref _autoQueryIndex)}", DatabaseNames());
        var columns = result.Columns
            .Select(c => new ColumnDefinition(c.Name, c.DuckDbType, c.ClrType))
            .ToArray();

        var handle = CreateTableHandle(name, typeof(DuckResult), columns, result.Materialize().Rows, reload: null, options: null);
        _resultTables.AddOrUpdate(result, handle);
        _tables[name].Lifetime = AutoTableLifetime.Result;
        _tables[name].Owner = new WeakReference<DuckResult>(result);
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
            _resultTables.Clear();
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
