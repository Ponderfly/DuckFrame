using Xunit;

namespace DuckFrame.Tests;

public sealed class ResultsTests
{
    [Fact]
    public void ToDataTable_handles_nulls()
    {
        using var db = DuckSession.OpenInMemory();
        var rows = new[]
        {
            new Dictionary<string, object?> { ["k"] = "a", ["v"] = 1 },
            new Dictionary<string, object?> { ["k"] = "b", ["v"] = null },
        };
        var table = db.RegisterRows(rows);

        var dt = db.Query($"select * from {table} order by 1").ToDataTable();

        Assert.Equal(2, dt.Rows.Count);
        Assert.True(dt.Rows[1]["v"] is DBNull);
    }

    [Fact]
    public void Print_renders_aligned_table()
    {
        using var db = DuckSession.OpenInMemory();

        var text = db.Query($"select 7 as n, '中文' as label").ToString();

        Assert.Contains("n", text);
        Assert.Contains("中文", text);
        Assert.Contains("row(s)", text);
    }

    [Fact]
    public void Missing_column_throws_with_actual_columns()
    {
        using var db = DuckSession.OpenInMemory();

        var ex = Assert.Throws<DuckFrameException>(() =>
            db.Query($"select 1 as Other").To<RegionTotal>());

        Assert.Contains("Other", ex.Message);
    }

    [Fact]
    public void Extra_column_can_be_ignored()
    {
        using var db = DuckSession.OpenInMemory(new DuckSessionOptions
        {
            ExtraColumn = ExtraColumnBehavior.Ignore,
        });
        var rows = new[] { new Dictionary<string, object?> { ["Region"] = "EU", ["N"] = 1L, ["Total"] = 2m } };
        var table = db.RegisterRows(rows);

        var value = db.Query($"select *, 'extra' as Ignored from {table}").To<RegionTotal>();

        Assert.Single(value);
    }

    [Fact]
    public void Result_can_be_read_twice_after_materialize()
    {
        using var db = DuckSession.OpenInMemory();
        var rows = Enumerable.Range(0, 5)
            .Select(i => new Dictionary<string, object?> { ["i"] = i })
            .ToArray();
        var table = db.RegisterRows(rows);

        using var result = db.Query($"select i from {table} order by 1");

        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, result.To<int>());
        Assert.Equal(5, result.RowCount);
        Assert.Equal(5, result.To<int>().Count);
    }

    [Fact]
    public void Dispose_then_access_throws()
    {
        using var db = DuckSession.OpenInMemory();
        var result = db.Query($"select 1");
        result.Dispose();

        Assert.Throws<ObjectDisposedException>(() => result.To<int>());
    }
}
