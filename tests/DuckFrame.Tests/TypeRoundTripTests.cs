using Xunit;

namespace DuckFrame.Tests;

public sealed class TypeRoundTripTests
{
    [Fact]
    public void All_supported_types_round_trip()
    {
        using var db = DuckSession.OpenInMemory();
        var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
        var items = new List<TypeZoo>
        {
            new(
                Bool: true,
                Int: -42,
                Long: 9_000_000_000L,
                Double: 3.14159,
                Decimal: 1234.5678m,
                Text: "中文 text",
                Uuid: Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"),
                Timestamp: created,
                Date: new DateOnly(2026, 1, 2),
                Interval: TimeSpan.FromMinutes(90),
                Blob: [1, 2, 3]),
        };

        var table = db.Register(items);
        var back = db.Query($"select * from {table}").To<TypeZoo>().Single();
        var original = items[0];

        Assert.Equal(original.Bool, back.Bool);
        Assert.Equal(original.Int, back.Int);
        Assert.Equal(original.Long, back.Long);
        Assert.Equal(original.Double, back.Double);
        Assert.Equal(original.Decimal, back.Decimal);
        Assert.Equal(original.Text, back.Text);
        Assert.Equal(original.Uuid, back.Uuid);
        Assert.Equal(original.Timestamp, back.Timestamp);
        Assert.Equal(original.Date, back.Date);
        Assert.Equal(original.Interval, back.Interval);
        Assert.Equal(original.Blob, back.Blob);
    }

    [Fact]
    public void Null_values_are_preserved()
    {
        using var db = DuckSession.OpenInMemory();
        var items = new List<TypeZoo>
        {
            new(false, 0, 0, 0, 0, null, Guid.Empty, DateTime.MinValue, default, TimeSpan.Zero, []),
            new(true, 1, 2, 3, 4, null, Guid.Empty, DateTime.MinValue, default, TimeSpan.Zero, []),
        };

        var table = db.Register(items);
        var count = db.Query($"select count(*) from {table} where Text is null").To<long>().Single();

        Assert.Equal(2L, count);
    }

    [Fact]
    public void Null_element_in_source_throws_with_index()
    {
        using var db = DuckSession.OpenInMemory();
        var items = new List<TypeZoo?>
        {
            new(false, 0, 0, 0, 0, null, Guid.Empty, DateTime.MinValue, default, TimeSpan.Zero, []),
            null,
        };

        var ex = Assert.Throws<DuckFrameException>(() => db.Register(items));

        Assert.Contains("第 1 个元素", ex.Message);
    }

    [Fact]
    public void Decimal_precision_is_not_lost()
    {
        using var db = DuckSession.OpenInMemory();
        var rows = new[] { new Dictionary<string, object?> { ["amount"] = 999999999999.9999m } };

        var table = db.RegisterRows(rows);
        var value = db.Query($"select amount from {table}").To<decimal>().Single();

        Assert.Equal(999999999999.9999m, value);
    }

    [Fact]
    public void Snake_case_columns_map_to_pascal_case_members()
    {
        using var db = DuckSession.OpenInMemory();

        var value = db.Query($"select 'EU' as region, 3 as n, 12.5 as total").To<RegionTotal>().Single();

        Assert.Equal("EU", value.Region);
        Assert.Equal(3L, value.N);
        Assert.Equal(12.5m, value.Total);
    }
}
