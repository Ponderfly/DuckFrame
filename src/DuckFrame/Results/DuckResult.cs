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

    internal DuckResult(
        DuckDBDataReader reader,
        IDbCommand command,
        IReadOnlyList<DuckColumn> columns,
        DuckSessionOptions options)
    {
        _reader = reader;
        _command = command;
        _options = options;
        Columns = columns;
    }

    /// <summary>结果列。</summary>
    public IReadOnlyList<DuckColumn> Columns { get; }

    /// <summary>是否已全部读入内存。</summary>
    public bool IsMaterialized => _rows is not null;

    /// <summary>已读入的行数（未物化时返回已读行数 0）。</summary>
    public long RowCount => _rows?.Count ?? 0;

    /// <summary>物化后的行。未调用 <see cref="MaterializeAsync"/> 前访问会抛异常。</summary>
    public IReadOnlyList<object?[]> Rows =>
        _rows ?? throw new DuckFrameException("结果尚未物化，请先调用 MaterializeAsync() 或 To<T>()。");

    /// <summary>把结果一次性读入内存。</summary>
    public Task<DuckResult> MaterializeAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_rows is not null)
            return Task.FromResult(this);

        var reader = _reader ?? throw new DuckFrameException("结果已被消费，无法再次物化。");
        var rows = new List<object?[]>();

        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            var row = new object?[Columns.Count];
            for (var i = 0; i < row.Length; i++)
                row[i] = ReadValue(reader, i);
            rows.Add(row);
        }

        _rows = rows;
        ReleaseReader();
        return Task.FromResult(this);
    }

    /// <summary>
    /// 读一个值。BLOB 在 DuckDB.NET 里是指向原生结果内存的 UnmanagedMemoryStream，
    /// reader 释放后再读就是释放后使用，所以必须当场拷成 byte[]。
    /// </summary>
    private static object? ReadValue(DuckDBDataReader reader, int ordinal)
    {
        switch (reader.GetValue(ordinal))
        {
            case DBNull:
                return null;
            case Stream stream:
            {
                using (stream)
                {
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                }
            }
            case var value:
                return value;
        }
    }

    /// <summary>读取第一行第一列，供 <see cref="DuckSession.Scalar{T}"/> 使用；不物化其余行。</summary>
    internal object? FirstValue()
    {
        ThrowIfDisposed();

        if (_rows is not null)
            return _rows.Count > 0 && _rows[0].Length > 0 ? _rows[0][0] : null;

        var reader = _reader ?? throw new DuckFrameException("结果已被消费，无法再次读取。");
        try
        {
            return reader.Read() && reader.FieldCount > 0 ? ReadValue(reader, 0) : null;
        }
        finally
        {
            ReleaseReader();
        }
    }

    /// <summary>同步物化。</summary>
    public DuckResult Materialize() => MaterializeAsync().GetAwaiter().GetResult();

    /// <summary>物化为 <typeparamref name="T"/> 列表。</summary>
    public List<T> To<T>()
    {
        Materialize();
        var map = Materializer.Get<T>(Columns, _options);
        var result = new List<T>(Rows.Count);

        try
        {
            foreach (var row in Rows)
                result.Add(map(row));
        }
        catch (InvalidCastException ex)
        {
            throw new DuckFrameException($"把第 {result.Count} 行映射到 {typeof(T)} 失败：列类型与成员类型不兼容。", ex);
        }

        return result;
    }

    /// <summary>流式枚举 <typeparamref name="T"/>，不缓存全部行。</summary>
    public IEnumerable<T> AsEnumerable<T>()
    {
        ThrowIfDisposed();
        var map = Materializer.Get<T>(Columns, _options);

        if (_rows is not null)
        {
            foreach (var row in _rows)
                yield return map(row);
            yield break;
        }

        var reader = _reader ?? throw new DuckFrameException("结果已被消费，无法再次枚举。");
        try
        {
            while (reader.Read())
            {
                var row = new object?[Columns.Count];
                for (var i = 0; i < row.Length; i++)
                    row[i] = ReadValue(reader, i);
                yield return map(row);
            }
        }
        finally
        {
            ReleaseReader();
        }
    }

    /// <summary>物化为 DataTable。</summary>
    public DataTable ToDataTable()
    {
        Materialize();
        var table = new DataTable();

        foreach (var column in Columns)
        {
            // DataColumn 不允许 DBNull 写入非可空值类型列，因此值类型列退化为 object 列。
            var type = column.ClrType;
            var columnType = type.IsValueType && Nullable.GetUnderlyingType(type) is null
                ? typeof(object)
                : type;

            table.Columns.Add(column.Name, columnType);
        }

        foreach (var row in Rows)
            table.Rows.Add(row.Select(static v => v ?? DBNull.Value).ToArray());

        return table;
    }

    /// <summary>物化为字典列表（类似 df.to_dict('records')）。</summary>
    public List<Dictionary<string, object?>> ToDictionaries()
    {
        Materialize();
        var names = Columns.Select(c => c.Name).ToArray();
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

    /// <summary>输出 CSV（RFC 4180 风格转义）。</summary>
    public string ToCsv()
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

    /// <summary>输出 Markdown 表格。</summary>
    public string ToMarkdown()
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

    /// <summary>在终端打印对齐表格。</summary>
    public void Print(int maxRows = 20, int maxWidth = 200) =>
        Console.WriteLine(TablePrinter.Render(Columns, Materialize().Rows, maxRows, maxWidth));

    /// <inheritdoc />
    public override string ToString() => TablePrinter.Render(Columns, Materialize().Rows, 20, 200);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ReleaseReader();
        _command?.Dispose();
        _command = null;
    }

    private void ReleaseReader()
    {
        _reader?.Dispose();
        _reader = null;
        _command?.Dispose();
        _command = null;
    }

    private void ThrowIfDisposed() => Guard.NotDisposed(_disposed, nameof(DuckResult));

    private static string EscapeCsv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
}
