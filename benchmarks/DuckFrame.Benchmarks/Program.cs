using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using DuckFrame;

BenchmarkRunner.Run<RegistrationBenchmarks>();

[MemoryDiagnoser]
public class RegistrationBenchmarks
{
    private readonly List<Order> _orders = Enumerable.Range(1, 100_000)
        .Select(i => new Order(i, i % 5 == 0 ? "EU" : "US", i * 1.5m, DateTime.UnixEpoch.AddMinutes(i)))
        .ToList();

    [Benchmark]
    public long Register_100k_rows()
    {
        using var db = DuckSession.OpenInMemory();
        return db.Register(_orders).RowCount;
    }

    [Benchmark]
    public List<RegionTotal> Query_and_materialize_100k()
    {
        using var db = DuckSession.OpenInMemory();
        var table = db.Register(_orders);
        return db.Query($"select Region, count(*) as N, sum(Amount) as Total from {table} group by 1")
                 .To<RegionTotal>();
    }
}

public sealed record Order(int Id, string Region, decimal Amount, DateTime CreatedAt);

public sealed record RegionTotal(string Region, long N, decimal Total);
