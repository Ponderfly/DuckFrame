using System.Data;
using System.Text;
using DuckDB.NET.Data;

namespace DuckFrame;

/// <summary>一次查询的结果集。</summary>
public sealed class DuckResult : IDisposable
{
    private readonly DuckSessionOptions _options;
    private DuckDBDataReader? _reader;
    private IDbCommand? _command;
    private List<object?[]>? _rows;
    private bool _disposed;
    private readonly Lock _gate;
    private List<object?[]>? _prefix;
    private bool _enumerating;
    private readonly Func<DuckDBDataReader, int, object?>[] _valueReaders;

    internal DuckResult(
        DuckDBDataReader reader,
        IDbCommand command,
        IReadOnlyList<DuckColumn> columns,
        DuckSessionOptions options,
        Lock gate)
    {
        _gate = gate;
        _reader = reader;
        _command = command;
        _options = options;
        Columns = columns;
        _valueReaders = Enumerable.Range(0, columns.Count).Select(i => ResultValueReader.For(reader.GetFieldType(i))).ToArray();
    }

    /// <summary>结果列。</summary>
    public IReadOnlyList<DuckColumn> Columns { get; }

    /// <summary>是否已全部读入内存。</summary>
    public bool IsMaterialized { get { lock (_gate) return _rows is not null; } }

    internal bool IsReleased => _reader is null;

    internal bool IsDisposed => _disposed;

    /// <summary>reader 释放或结果 Dispose 时通知会话，用于确定性地删除自动注册的临时表。在结果锁内调用。</summary>
    internal Action<DuckResult>? LifetimeChanged { get; set; }

    /// <summary>已读入的行数（未物化时返回已读行数 0）。</summary>
    public long RowCount { get { lock (_gate) return _rows?.Count ?? 0; } }

    /// <summary>物化后的行。未调用 <see cref="MaterializeAsync"/> 前访问会抛异常。</summary>
    public IReadOnlyList<object?[]> Rows
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _rows ?? throw new DuckFrameException("结果尚未物化，请先调用 MaterializeAsync() 或 To<T>()。");
            }
        }
    }

    /// <summary>把结果一次性读入内存。</summary>
    public Task<DuckResult> MaterializeAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureNotEnumerating();
            ct.ThrowIfCancellationRequested();
            if (_rows is not null) return Task.FromResult(this);
            var reader = _reader ?? throw Consumed();
            var rows = _prefix ?? [];
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!reader.Read()) break;
                    rows.Add(ReadRow(reader));
                }
                _rows = rows;
                _prefix = null;
                ReleaseReader();
                return Task.FromResult(this);
            }
            catch
            {
                _prefix = null;
                ReleaseReader();
                throw;
            }
        }
    }

    private object?[] ReadRow(DuckDBDataReader reader)
    {
        var row = new object?[Columns.Count];
        for (var i = 0; i < row.Length; i++) row[i] = _valueReaders[i](reader, i);
        return row;
    }

    /// <summary>读取并脱离原生结果的所有权，供直接物化的转换路径使用。</summary>
    internal static object? ReadValue(DuckDBDataReader reader, int ordinal) =>
        ResultValueReader.For(reader.GetFieldType(ordinal))(reader, ordinal);

    /// <summary>读取第一行第一列，供 <see cref="DuckSession.Scalar{T}"/> 使用；不物化其余行。</summary>
    internal object? FirstValue()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureNotEnumerating();
            if (_rows is not null)
                return _rows.Count > 0 && _rows[0].Length > 0 ? _rows[0][0] : null;
            try
            {
                if (_prefix is { Count: > 0 }) return _prefix[0].FirstOrDefault();
                var reader = _reader ?? throw Consumed();
                return reader.Read() && reader.FieldCount > 0 ? _valueReaders[0](reader, 0) : null;
            }
            finally { _prefix = null; ReleaseReader(); }
        }
    }

    /// <summary>同步物化并保留行缓存。</summary>
    public DuckResult Materialize() => MaterializeAsync().GetAwaiter().GetResult();

    /// <summary>转换为列表并保留原始行缓存，支持重复读取。</summary>
    public List<T> To<T>() => To<T>(cacheRows: true);

    /// <summary>转换为列表；cacheRows=false 时直接消费结果，不保留第二份原始行缓存。</summary>
    public List<T> To<T>(bool cacheRows)
    {
        if (!cacheRows) return AsEnumerable<T>().ToList();
        lock (_gate)
        {
            Materialize();
            var map = Materializer.Get<T>(Columns, _options);
            var result = new List<T>(_rows!.Count);
            try
            {
                foreach (var row in _rows) result.Add(map(row));
            }
            catch (InvalidCastException ex)
            {
                throw new DuckFrameException($"把第 {result.Count} 行映射到 {typeof(T)} 失败：列类型与成员类型不兼容。", ex);
            }
            return result;
        }
    }

    /// <summary>流式枚举；同一结果同时只允许一个消费者，提前结束会释放 reader。</summary>
    public IEnumerable<T> AsEnumerable<T>()
    {
        Func<object?[], T> map;
        Func<DuckDBDataReader, T>? read = null;
        List<object?[]>? buffered;
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureNotEnumerating();
            map = Materializer.Get<T>(Columns, _options);
            buffered = _rows;
            if (buffered is null && _reader is null) throw Consumed();
            if (buffered is null) read = Materializer.GetReader<T>(Columns, _options);
            _enumerating = true;
        }
        try
        {
            var index = 0;
            while (true)
            {
                T item;
                lock (_gate)
                {
                    ThrowIfDisposed();
                    if (buffered is not null)
                    {
                        if (index == buffered.Count) break;
                        item = map(buffered[index++]);
                    }
                    else if (_prefix is not null && index < _prefix.Count)
                        item = map(_prefix[index++]);
                    else
                    {
                        if (!_reader!.Read()) break;
                        item = read!(_reader);
                    }
                }
                yield return item;
            }
        }
        finally
        {
            lock (_gate)
            {
                _enumerating = false;
                if (buffered is null) { _prefix = null; ReleaseReader(); }
            }
        }
    }

    /// <summary>物化为 DataTable。</summary>
    public DataTable ToDataTable()
    {
        lock (_gate)
        {
            Materialize();
            NameMatcher.EnsureUnique(Columns.Select(c => c.Name));
            var table = new DataTable();

            foreach (var column in Columns)
            {
                var type = column.ClrType;
                var columnType = typeof(Stream).IsAssignableFrom(type) ? typeof(byte[]) : Nullable.GetUnderlyingType(type) ?? type;
                var dataColumn = table.Columns.Add(column.Name, columnType);
                dataColumn.AllowDBNull = true;
                // 推测出来的类型不记录，再次注册时按 CLR 类型推导，由写入器校验精度。
                if (!column.IsTypeApproximate)
                    dataColumn.ExtendedProperties["DuckFrame.DuckDbType"] = column.DuckDbType;
            }

            foreach (var row in Rows)
                table.Rows.Add(row.Select(static v => v ?? DBNull.Value).ToArray());

            return table;
        }
    }

    /// <summary>物化为字典列表（类似 df.to_dict('records')）。</summary>
    public List<Dictionary<string, object?>> ToDictionaries()
    {
        lock (_gate)
        {
            Materialize();
            var names = Columns.Select(c => c.Name).ToArray();
            NameMatcher.EnsureUnique(names);
            var result = new List<Dictionary<string, object?>>(Rows.Count);

            foreach (var row in Rows)
            {
                var dict = new Dictionary<string, object?>(names.Length, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < names.Length; i++)
                    dict[names[i]] = row[i];
                result.Add(dict);
            }

            return result;
        }
    }

    /// <summary>输出 CSV（RFC 4180 风格转义）。</summary>
    public string ToCsv()
    {
        lock (_gate)
        {
            Materialize();
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(',', Columns.Select(c => EscapeCsv(c.Name))));

            foreach (var row in Rows)
            {
                sb.AppendLine(string.Join(',', row.Select(v => EscapeCsv(
                    v is null ? "" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? ""))));
            }

            return sb.ToString();
        }
    }

    /// <summary>输出 Markdown 表格。</summary>
    public string ToMarkdown()
    {
        lock (_gate)
        {
            Materialize();
            var sb = new StringBuilder();
            sb.Append('|').AppendJoin('|', Columns.Select(c => c.Name)).AppendLine("|");
            sb.Append('|').AppendJoin('|', Columns.Select(_ => "---")).AppendLine("|");

            foreach (var row in Rows)
            {
                sb.Append('|')
                  .AppendJoin('|', row.Select(v => v is null ? "NULL" : v.ToString()?.Replace("|", "\\|") ?? ""))
                  .AppendLine("|");
            }

            return sb.ToString();
        }
    }

    /// <summary>只读取预览所需的行；预览缓存可供后续完整读取复用。</summary>
    public void Print(int maxRows = 20, int maxWidth = 200) =>
        Console.WriteLine(Preview(maxRows, maxWidth));

    /// <summary>返回有限行预览，不消费整个结果。</summary>
    public override string ToString() => Preview(20, 200);

    private string Preview(int maxRows, int maxWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureNotEnumerating();
            if (_rows is not null) return TablePrinter.Render(Columns, _rows, maxRows, maxWidth);
            var reader = _reader ?? throw Consumed();
            _prefix ??= [];
            try
            {
                while (_prefix.Count <= maxRows)
                {
                    if (!reader.Read())
                    {
                        _rows = _prefix;
                        _prefix = null;
                        ReleaseReader();
                        break;
                    }
                    _prefix.Add(ReadRow(reader));
                }
                return TablePrinter.Render(Columns, _rows ?? _prefix!, maxRows, maxWidth, _rows is not null);
            }
            catch { _prefix = null; ReleaseReader(); throw; }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _prefix = null;
            _rows = null;
            ReleaseReader();
            NotifyLifetimeChanged();
        }
    }

    private static DuckFrameException Consumed() => new("结果已被消费或读取失败，无法再次读取，请重新查询。");

    private void EnsureNotEnumerating()
    {
        if (_enumerating) throw new DuckFrameException("结果正在流式枚举，不能同时启动另一次读取。");
    }

    private void ReleaseReader()
    {
        var reader = _reader;
        var command = _command;
        _reader = null;
        _command = null;
        try
        {
            try { reader?.Dispose(); }
            finally { command?.Dispose(); }
        }
        finally
        {
            // reader 释放后，这次查询插值进来的临时表就不再被引用。
            if (reader is not null) NotifyLifetimeChanged();
        }
    }

    private void NotifyLifetimeChanged()
    {
        // 清理临时表失败不能影响结果本身的读取或释放。
        try { LifetimeChanged?.Invoke(this); } catch { }
    }

    private void ThrowIfDisposed() => Guard.NotDisposed(_disposed, nameof(DuckResult));

    private static string EscapeCsv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
}
