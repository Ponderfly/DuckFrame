using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using DuckFrame;

BenchmarkSwitcher.FromAssembly(typeof(RegistrationBenchmarks).Assembly).Run(args);

[MemoryDiagnoser]
public class RegistrationBenchmarks
{
    private List<BenchmarkRow> _rows = null!;
    [Params(100_000)] public int RowCount { get; set; }

    [GlobalSetup]
    public void Setup() => _rows = BenchmarkRow.Create(RowCount);

    [Benchmark]
    public long Register()
    {
        using var db = DuckSession.OpenInMemory();
        return db.Register(_rows).RowCount;
    }
}

[MemoryDiagnoser]
public class MaterializationBenchmarks
{
    private DuckSession _db = null!;
    private string _sql = null!;
    [Params(100_000)] public int RowCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _db = DuckSession.OpenInMemory();
        var table = _db.Register(BenchmarkRow.Create(RowCount));
        _sql = $"select * from {table.QualifiedName}";
    }

    [GlobalCleanup]
    public void Cleanup() => _db.Dispose();

    [Benchmark(Baseline = true)]
    public List<BenchmarkRow> NativeReader()
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = _sql;
        using var reader = command.ExecuteReader();
        var result = new List<BenchmarkRow>();
        while (reader.Read())
            result.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetDecimal(2), reader.GetDateTime(3),
                reader.GetBoolean(4), reader.GetDouble(5), reader.GetGuid(6), reader.GetString(7)));
        return result;
    }

    [Benchmark]
    public List<BenchmarkRow> CachedRows()
    {
        using var result = _db.Query(_sql);
        return result.To<BenchmarkRow>();
    }

    [Benchmark]
    public List<BenchmarkRow> WithoutRowCache()
    {
        using var result = _db.Query(_sql);
        return result.To<BenchmarkRow>(cacheRows: false);
    }
}

public sealed record BenchmarkRow(int Id, string Region, decimal Amount, DateTime CreatedAt,
    bool Paid, double Score, Guid Uuid, string Note)
{
    public static List<BenchmarkRow> Create(int count) => Enumerable.Range(1, count)
        .Select(i => new BenchmarkRow(i, i % 5 == 0 ? "EU" : "US", i * 1.5m,
            DateTime.UnixEpoch.AddMinutes(i), i % 2 == 0, i / 3.0,
            Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"), "sample"))
        .ToList();
}
