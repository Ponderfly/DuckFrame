using Xunit;

namespace DuckFrame.Tests;

/// <summary>
/// 锁住核对 DuckDB.NET 1.5.6.1 实际行为时发现的缺陷（README「已核对的行为」）。
/// </summary>
public sealed class CorrectnessTests
{
    public enum Kind { Click, View }

    public sealed record Event(int Id, Kind Kind, TimeSpan Duration);

    public sealed record Tagged(int Id, int[] Scores, List<string> Labels);

    public sealed record Address(string City, string Zip);

    public sealed record Person(int Id, string Name, Address Home);

    public sealed record Priced(int Id, [property: DuckPrecision(18, 6)] decimal Price);

    public sealed record Cheap(int Id, decimal Price);

    [Fact]
    public void Dictionary_rows_register_through_generic_register()
    {
        using var db = DuckSession.OpenInMemory();
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["k"] = "a", ["v"] = 1 },
            new() { ["k"] = "b", ["v"] = 2 },
        };

        var table = db.Register(rows);

        Assert.Equal(3L, db.Scalar<long>($"select sum(v) from {table}"));
    }

    [Fact]
    public void Interpolated_dictionary_rows_become_a_table()
    {
        using var db = DuckSession.OpenInMemory();
        var events = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["kind"] = "click" },
            new Dictionary<string, object?> { ["kind"] = "click" },
            new Dictionary<string, object?> { ["kind"] = "view" },
        };

        var clicks = db.Scalar<long>($"select count(*) from {events} where kind = {"click"}");

        Assert.Equal(2L, clicks);
    }

    [Fact]
    public void Mixed_numeric_column_is_not_corrupted()
    {
        // appender 不做类型转换：int 直接写进 DOUBLE 列会读成 2.5E-323。
        using var db = DuckSession.OpenInMemory();
        var rows = new[]
        {
            new Dictionary<string, object?> { ["v"] = 1 },
            new Dictionary<string, object?> { ["v"] = 2.5 },
        };

        var table = db.RegisterRows(rows);

        Assert.Equal(3.5, db.Scalar<double>($"select sum(v) from {table}"));
    }

    [Fact]
    public void Enums_as_int_and_timespan_as_micros_round_trip()
    {
        using var db = DuckSession.OpenInMemory(new DuckSessionOptions { EnumsAsInt = true, TimeSpanAsMicros = true });
        var events = new List<Event> { new(1, Kind.View, TimeSpan.FromMilliseconds(1500)) };

        var table = db.Register(events);

        Assert.Equal(1, db.Scalar<int>($"select Kind from {table}"));
        Assert.Equal(1_500_000L, db.Scalar<long>($"select Duration from {table}"));
        Assert.Equal(events, db.Query($"select * from {table}").To<Event>());
    }

    [Fact]
    public void Scalar_collections_are_native_lists()
    {
        // LIST 只能写成 INTEGER[]；LIST(INTEGER) 是解析错误。
        using var db = DuckSession.OpenInMemory();
        var items = new List<Tagged> { new(1, [90, 80], ["a", "b", "c"]) };

        var table = db.Register(items);

        Assert.Equal(3L, db.Scalar<long>($"select len(Labels) from {table}"));
        var back = db.Query($"select * from {table}").To<Tagged>().Single();
        Assert.Equal([90, 80], back.Scores);
        Assert.Equal(["a", "b", "c"], back.Labels);
    }

    [Fact]
    public void Nested_objects_are_stored_as_queryable_json()
    {
        using var db = DuckSession.OpenInMemory();
        var people = new List<Person> { new(1, "Ann", new Address("Paris", "75001")) };

        var table = db.Register(people);

        Assert.Equal("Paris", db.Scalar<string>($"select Home->>'$.City' from {table}"));
        Assert.Equal(people[0].Home, db.Query($"select * from {table}").To<Person>().Single().Home);
    }

    [Fact]
    public void Decimal_scale_overflow_is_rejected_instead_of_truncated()
    {
        // appender 会把 1.23456 静默截断成 1.2345。
        using var db = DuckSession.OpenInMemory();
        var items = new List<Cheap> { new(1, 1.23456m) };

        var ex = Assert.Throws<DuckFrameException>(() => db.Register(items));

        Assert.Contains("DuckPrecision", ex.Message);
        Assert.Empty(db.Tables);
    }

    [Fact]
    public void Requerying_a_result_keeps_decimal_precision()
    {
        // GetDataTypeName 只返回 "Decimal"，按它重建表会退化成 DECIMAL(18,3)。
        using var db = DuckSession.OpenInMemory();
        var items = new List<Priced> { new(1, 1.234567m), new(2, 9.5m) };
        var table = db.Register(items);

        var filtered = db.Query($"select * from {table} where Price < {5m}");
        var price = db.Scalar<decimal>($"select Price from {filtered}");

        Assert.Equal(1.234567m, price);
    }

    [Fact]
    public void Same_result_interpolated_twice_is_registered_once()
    {
        using var db = DuckSession.OpenInMemory();
        var result = db.Query($"select 1 as x");

        db.Query($"select * from {result}").Materialize();
        db.Query($"select * from {result}").Materialize();

        Assert.Single(db.Tables, t => t.Name.StartsWith("__q", StringComparison.Ordinal));
    }

    [Fact]
    public void Inline_collection_reuses_its_table_and_sees_changes()
    {
        using var db = DuckSession.OpenInMemory();
        var orders = new List<Order> { new(1, "EU", 10m, DateTime.UtcNow) };

        Assert.Equal(1L, db.Scalar<long>($"select count(*) from {orders}"));
        orders.Add(new Order(2, "US", 20m, DateTime.UtcNow));
        Assert.Equal(2L, db.Scalar<long>($"select count(*) from {orders}"));

        Assert.Single(db.Tables);
    }

    [Fact]
    public void Scalar_collection_is_bound_as_list_parameter()
    {
        using var db = DuckSession.OpenInMemory();
        var orders = new List<Order>
        {
            new(1, "EU", 10m, DateTime.UtcNow),
            new(2, "US", 20m, DateTime.UtcNow),
            new(3, "EU", 30m, DateTime.UtcNow),
        };
        var ids = new[] { 1, 3 };

        var total = db.Scalar<decimal>($"select sum(Amount) from {orders} where Id = ANY({ids})");

        Assert.Equal(40m, total);
    }

    [Fact]
    public void Refresh_reenumerates_the_source()
    {
        using var db = DuckSession.OpenInMemory();
        var orders = new List<Order> { new(1, "EU", 10m, DateTime.UtcNow) };
        var table = db.Register(orders);

        orders.Add(new Order(2, "US", 20m, DateTime.UtcNow));
        Assert.Equal(1L, db.Scalar<long>($"select count(*) from {table}"));

        db.Refresh(table.Name);
        Assert.Equal(2L, db.Scalar<long>($"select count(*) from {table}"));
        Assert.Equal(2L, table.RowCount);
    }

    [Fact]
    public void Scalar_reads_first_column_of_first_row()
    {
        using var db = DuckSession.OpenInMemory();

        Assert.Equal(42L, db.Scalar<long>($"select 42 as a, 'x' as b"));
        Assert.Equal(0L, db.Scalar<long>($"select 1 as a where false"));
    }

    [Fact]
    public void Blob_survives_after_reader_is_released()
    {
        // BLOB 读出来是指向原生内存的流，必须在 reader 释放前拷贝。
        using var db = DuckSession.OpenInMemory();
        using var result = db.Query($"select 'abc'::BLOB as b, 2 as n");
        result.Materialize();

        db.Query($"select range from range(100000)").Materialize();

        Assert.Equal("abc"u8.ToArray(), result.To<Blob>().Single().B);
    }

    public sealed record Blob(byte[] B, int N);

    public sealed class MutableStat
    {
        public string Region { get; set; } = "";

        public long N { get; set; }
    }

    public sealed record WithExtra(string Region)
    {
        public long N { get; init; }
    }

    [Fact]
    public void Class_with_parameterless_constructor_gets_its_properties_set()
    {
        // 曾经的缺陷：选中无参构造时直接 new T()，属性全部保持默认值且不报错。
        using var db = DuckSession.OpenInMemory();

        var stat = db.Query($"select 'EU' as region, 7 as n").To<MutableStat>().Single();

        Assert.Equal("EU", stat.Region);
        Assert.Equal(7L, stat.N);
    }

    [Fact]
    public void Record_extra_init_properties_are_bound_after_the_constructor()
    {
        using var db = DuckSession.OpenInMemory();

        var value = db.Query($"select 'EU' as Region, 3::BIGINT as N").To<WithExtra>().Single();

        Assert.Equal(new WithExtra("EU") { N = 3 }, value);
    }

    [Fact]
    public void Null_into_non_nullable_member_reports_the_column()
    {
        using var db = DuckSession.OpenInMemory();

        var ex = Assert.Throws<DuckFrameException>(() =>
            db.Query($"select 'EU' as Region, NULL::BIGINT as N").To<WithExtra>());

        Assert.Contains("\"N\"", ex.Message);
    }
}
