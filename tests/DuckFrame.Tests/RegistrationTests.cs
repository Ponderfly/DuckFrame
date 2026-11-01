using Xunit;

namespace DuckFrame.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public void Register_uses_variable_name()
    {
        using var db = DuckSession.OpenInMemory();
        var orders = new List<Order> { new(1, "EU", 10m, DateTime.UtcNow) };

        var table = db.Register(orders);

        Assert.Equal("orders", table.Name);
        Assert.Equal(1L, table.RowCount);
    }

    [Fact]
    public void Register_uses_explicit_name_over_variable_name()
    {
        using var db = DuckSession.OpenInMemory();
        var orders = new List<Order> { new(1, "EU", 10m, DateTime.UtcNow) };

        var table = db.Register(orders, name: "sales_orders");

        Assert.Equal("sales_orders", table.Name);
    }

    [Fact]
    public void Query_interpolates_table_and_parameterizes_values()
    {
        using var db = DuckSession.OpenInMemory();
        var orders = new List<Order>
        {
            new(1, "EU", 10m, DateTime.UtcNow),
            new(2, "EU", 20m, DateTime.UtcNow),
            new(3, "US", 99m, DateTime.UtcNow),
        };
        var table = db.Register(orders);

        var result = db.Query($"select Region, count(*) as N, sum(Amount) as Total from {table} where Amount > {15m} group by 1");

        var rows = result.To<RegionTotal>().OrderBy(r => r.Region).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("EU", rows[0].Region);
        Assert.Equal(1L, rows[0].N);
        Assert.Equal(20m, rows[0].Total);
        Assert.Equal("US", rows[1].Region);
        Assert.Equal(99m, rows[1].Total);
    }

    [Fact]
    public void String_parameter_is_never_treated_as_identifier()
    {
        using var db = DuckSession.OpenInMemory();
        var orders = new List<Order> { new(1, "EU", 10m, DateTime.UtcNow) };
        var table = db.Register(orders);
        var malicious = "x'; DROP TABLE orders; --";

        var count = db.Query($"select count(*) from {table} where Region <> {malicious}").To<long>().Single();

        Assert.Equal(1L, count);
        Assert.Contains(db.Tables, t => t.Name == "orders");
    }

    [Fact]
    public void Registered_rows_can_be_queried_again()
    {
        using var db = DuckSession.OpenInMemory();
        var orders = new List<Order>
        {
            new(1, "EU", 10m, DateTime.UtcNow),
            new(2, "EU", 30m, DateTime.UtcNow),
        };

        var filtered = db.Query($"select * from {orders} where Amount > {15m}");
        var avg = db.Query($"select avg(Amount) from {filtered}").To<decimal>().Single();

        Assert.Equal(30m, avg);
    }

    [Fact]
    public void RegisterRows_infers_columns()
    {
        using var db = DuckSession.OpenInMemory();
        var rows = new[]
        {
            new Dictionary<string, object?> { ["key"] = "a", ["value"] = 1 },
            new Dictionary<string, object?> { ["key"] = "b", ["value"] = 2 },
        };

        var table = db.RegisterRows(rows);

        Assert.Equal("rows", table.Name);
        Assert.Equal(2L, db.Query($"select count(*) from {table}").To<long>().Single());
    }

    [Fact]
    public void Empty_collection_round_trips()
    {
        using var db = DuckSession.OpenInMemory();
        var empty = new List<Order>();

        var table = db.Register(empty);

        Assert.Equal(0L, db.Query($"select count(*) from {table}").To<long>().Single());
    }

    [Fact]
    public void Reserved_word_and_spaces_in_names_are_quoted()
    {
        using var db = DuckSession.OpenInMemory();
        var rows = new[]
        {
            new Dictionary<string, object?> { ["select"] = "a", ["weird name"] = 1 },
        };

        var table = db.RegisterRows(rows, name: "order group");

        Assert.Equal(1L, db.Query($"select count(*) from {table}").To<long>().Single());
    }

    [Fact]
    public void Duplicate_names_get_suffix()
    {
        using var db = DuckSession.OpenInMemory();
        var a = new List<Order> { new(1, "EU", 1m, DateTime.UtcNow) };
        var b = new List<Order> { new(2, "US", 2m, DateTime.UtcNow) };

        // 两次都从同一个变量名注册同一逻辑名
        var first = db.Register(a, name: "dup");
        var second = db.Register(b, name: "dup");

        Assert.Equal("dup", first.Name);
        Assert.Equal("dup_2", second.Name);
    }
}
