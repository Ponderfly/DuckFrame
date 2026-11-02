using System.Collections;
using System.Data;
using System.Runtime.CompilerServices;
using Xunit;

namespace DuckFrame.Tests;

public sealed class BoundaryTests
{
    public sealed record Item(int Id);
    public sealed record Price(decimal Amount);
    public sealed record Amounts(IEnumerable<decimal?> Values);
    public sealed record Optional(int Id, string? Name, int Count = 7);
    public sealed record Mapped(int Id, string? Name, decimal Amount, DateOnly Day, byte[] Blob, int?[] Values);
    public sealed class Aliased(int id)
    {
        [Column("external_id")] public int Id { get; } = id;
    }

    [Fact]
    public void Existing_database_tables_are_not_overwritten_by_default()
    {
        using var db = DuckSession.OpenInMemory();
        db.Execute($"create table preserved as select 99 as Id");
        var t = db.Register(new[] { new Item(1) }, "preserved", options: new() { Persistent = true });
        Assert.Equal("preserved_2", t.Name);
        Assert.Equal(99, db.Scalar<int>($"select Id from preserved"));
        Assert.Equal(1, db.Scalar<int>($"select Id from {t}"));
    }

    [Fact]
    public void Invalid_schema_does_not_delete_existing_table()
    {
        using var db = DuckSession.OpenInMemory();
        db.Execute($"create table preserved as select 99 as Id");
        Assert.ThrowsAny<Exception>(() => db.Register(new[] { new Item(1) }, "preserved", options: new()
        {
            Persistent = true, Overwrite = true,
            ColumnTypes = new Dictionary<string, string> { ["Id"] = "INVALID_TYPE" },
        }));
        Assert.Equal(99, db.Scalar<int>($"select Id from preserved"));
        AssertNoStagingTables(db);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_overwrite_and_refresh_preserve_data(bool persistent)
    {
        using var db = DuckSession.OpenInMemory();
        var source = new List<Price> { new(3m) };
        var original = db.Register(source, "prices", options: new() { Persistent = persistent });
        Assert.Throws<DuckFrameException>(() => db.Register(new[] { new Price(1.23456m) }, "prices",
            options: new() { Persistent = persistent, Overwrite = true }));
        Assert.Same(original, db.GetTable("prices"));
        Assert.Equal(3m, db.Scalar<decimal>($"select Amount from {original}"));
        source.Add(new(1.23456m));
        Assert.Throws<DuckFrameException>(() => db.Refresh(original.Name));
        Assert.Equal(1, original.RowCount);
        Assert.Equal(3m, db.Scalar<decimal>($"select Amount from {original}"));
        AssertNoStagingTables(db);
    }

    [Fact]
    public void Overwrite_and_registration_participate_in_callers_transaction()
    {
        using var db = DuckSession.OpenInMemory();
        var original = db.Register(new[] { new Item(1) }, "items");
        using (var transaction = db.Connection.BeginTransaction())
        {
            var fresh = db.Register(new[] { new Item(4) }, "fresh");
            db.Register(new[] { new Item(2) }, "items", options: new() { Overwrite = true });
            Assert.Equal(2, db.Scalar<int>($"select Id from {original}"));
            Assert.Equal(4, db.Scalar<int>($"select Id from {fresh}"));
            transaction.Rollback();
        }
        Assert.Equal(1, db.Scalar<int>($"select Id from {original}"));
        Assert.Equal(0, db.Scalar<int>($"select count(*) from duckdb_tables() where table_name='fresh'"));
    }

    [Fact]
    public void Persistent_appender_is_not_redirected_to_shadowing_temp_table()
    {
        using var db = DuckSession.OpenInMemory();
        db.Execute($"create temp table shadow as select 99 as Id");
        var table = db.Register(new[] { new Item(7) }, "shadow", options: new() { Persistent = true, Overwrite = true });
        Assert.Equal(7, db.Scalar<int>($"select Id from {table}"));
        Assert.Equal(99, db.Scalar<int>($"select Id from temp.main.shadow"));
    }

    [Fact]
    public void Decimal_list_rejects_truncation_and_preserves_nullable_elements()
    {
        using var db = DuckSession.OpenInMemory();
        Assert.Throws<DuckFrameException>(() => db.Register(new[] { new Amounts(new decimal?[] { null, 1.234567m }) }));
        var values = new SinglePass<decimal?>(new decimal?[] { null, 1.234567m });
        var table = db.Register(new[] { new Amounts(values) }, options: new()
        {
            ColumnTypes = new Dictionary<string, string> { ["Values"] = "DECIMAL(18,6)[]" },
        });
        Assert.Equal(new decimal?[] { null, 1.234567m }, db.Query($"select Values from {table}").To<decimal?[]>().Single());
    }

    [Theory]
    [InlineData("DECIMAL")]
    [InlineData("DECIMAL(18)")]
    public void Implicit_decimal_scales_also_reject_truncation(string type)
    {
        using var db = DuckSession.OpenInMemory();
        Assert.Throws<DuckFrameException>(() => db.Register(new[] { new Price(1.23456m) }, options: new()
        {
            ColumnTypes = new Dictionary<string, string> { ["amount"] = type },
        }));
        Assert.Empty(db.Tables);
    }

    [Theory]
    [InlineData("select [1.234567::decimal(18,6)] as v")]
    [InlineData("select [1.234567::decimal(18,6)] as v; -- trailing comment")]
    [InlineData("select []::decimal(18,6)[] as v where false")]
    public void Decimal_list_schema_survives_result_requery(string sql)
    {
        using var db = DuckSession.OpenInMemory();
        using var result = db.Query(sql);
        Assert.Equal("DECIMAL(18,6)[]", result.Columns[0].DuckDbType);
        using var again = db.Query($"select * from {result}");
        Assert.Equal("DECIMAL(18,6)[]", again.Columns[0].DuckDbType);
        var rows = again.To<decimal[]>();
        if (rows.Count > 0) Assert.Equal(1.234567m, rows[0][0]);
    }

    [Fact]
    public void Bound_decimal_list_can_be_queried_again()
    {
        using var db = DuckSession.OpenInMemory();
        using var result = db.Query($"select {new[] { 1.234567m }} as v");
        Assert.Equal(1.234567m, db.Scalar<decimal>($"select v[1] from {result}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lazy_list_parameter_is_not_reenumerated_for_schema(bool namedParameters)
    {
        using var db = DuckSession.OpenInMemory();
        var source = new SinglePass<decimal>(new[] { 1.234567m });
        using var result = namedParameters ? db.Query("select $values as v", new { values = source }) : db.Query($"select {source} as v");
        Assert.Equal(1.234567m, db.Scalar<decimal>($"select v[1] from {result}"));
    }

    [Fact]
    public void DataTable_keeps_numeric_types_nulls_blob_and_decimal_precision()
    {
        using var db = DuckSession.OpenInMemory();
        var table = db.Query("select 1::int n, null::int empty, 1.234567::decimal(18,6) price, 'abc'::blob b").ToDataTable();
        Assert.Equal(typeof(int), table.Columns["n"]!.DataType);
        Assert.Equal(typeof(int), table.Columns["empty"]!.DataType);
        Assert.Equal(typeof(byte[]), table.Columns["b"]!.DataType);
        Assert.Equal(DBNull.Value, table.Rows[0]["empty"]);
        var registered = db.RegisterTable(table);
        Assert.Equal(1.234567m, db.Scalar<decimal>($"select price from {registered}"));
        Assert.Equal("INTEGER", db.Scalar<string>($"select typeof(n) from {registered}"));
        Assert.Equal("abc"u8.ToArray(), db.Query($"select b from {registered}").To<byte[]>().Single());
    }

    [Fact]
    public async Task Already_cancelled_materialization_does_not_advance_reader()
    {
        using var db = DuckSession.OpenInMemory();
        using var result = db.Query("select range from range(3)");
        await Assert.ThrowsAsync<OperationCanceledException>(() => result.MaterializeAsync(new CancellationToken(true)));
        Assert.Equal(new long[] { 0, 1, 2 }, result.To<long>());
    }

    [Fact]
    public void Dictionary_keys_ignore_case_without_losing_values()
    {
        using var db = DuckSession.OpenInMemory();
        var table = db.RegisterRows(new[]
        {
            new Dictionary<string, object?> { ["Name"] = "first" },
            new Dictionary<string, object?> { ["name"] = "second" },
        });
        Assert.Equal(new[] { "first", "second" }, db.Query($"select Name from {table} order by Name").To<string>());
        Assert.Throws<DuckFrameException>(() => db.RegisterRows(new[]
        {
            new Dictionary<string, object?> { ["Name"] = "a", ["name"] = "b" },
        }));
    }

    [Fact]
    public void Duplicate_result_column_names_are_rejected_when_configured()
    {
        using var db = DuckSession.OpenInMemory(new() { DuplicateColumns = DuplicateColumnBehavior.Throw });
        using var result = db.Query("select 1 as Id, 2 as id");
        Assert.Equal(new[] { "Id", "id" }, result.Columns.Select(c => c.Name));
        Assert.Throws<DuckFrameException>(() => result.ToDictionaries());
        Assert.Throws<DuckFrameException>(() => result.To<Dictionary<string, object?>>());
        Assert.Throws<DuckFrameException>(() => result.ToDataTable());
    }

    [Theory]
    [InlineData("select 1 a, 2 a, 3 a", "a,a_1,a_2")]
    [InlineData("select 1 a, 2 A", "a,A_1")]
    [InlineData("select 1 a, 2 a, 3 a_1", "a,a_1,a_1_1")]
    [InlineData("select 1 a, 2 b", "a,b")]
    public void Duplicate_result_columns_are_renamed_like_duckdb(string sql, string expected)
    {
        using var db = DuckSession.OpenInMemory();
        using var result = db.Query(sql);
        Assert.Equal(expected.Split(','), result.Columns.Select(c => c.Name));

        // 与 DuckDB 用同一 SQL 建表得到的列名一致。
        db.Execute(FormattableStringFactory.Create("create table ctas as " + sql));
        Assert.Equal(expected, db.Scalar<string>($"select string_agg(column_name, ',' order by column_index) from duckdb_columns() where table_name = 'ctas'"));
    }

    [Fact]
    public void Renamed_duplicate_columns_work_everywhere()
    {
        using var db = DuckSession.OpenInMemory();
        var a = db.Register(new[] { new Item(1) }, "a");
        var b = db.Register(new[] { new Item(2) }, "b");
        using var result = db.Query($"select x.*, y.* from {a} x cross join {b} y");

        var row = result.ToDictionaries().Single();
        Assert.Equal(1, row["Id"]);
        Assert.Equal(2, row["Id_1"]);
        var table = result.ToDataTable();
        Assert.Equal(2, table.Rows[0]["Id_1"]);
        Assert.Equal(2, db.Scalar<int>($"select Id_1 from {result}"));
        Assert.Equal(new Pair(1, 2), result.To<Pair>().Single());

        // 映射到只有 Id 的类型：重命名后的 Id_1 是多余列，按 ExtraColumn 规则处理，而不是静默顶替 Id。
        var extra = Assert.Throws<DuckFrameException>(() => db.Query($"select x.*, y.* from {a} x cross join {b} y").To<Item>());
        Assert.Contains("Id_1", extra.Message);
        using var lenient = DuckSession.OpenInMemory(new() { ExtraColumn = ExtraColumnBehavior.Ignore });
        Assert.Equal(new Item(1), lenient.Query("select 1 as Id, 2 as Id").To<Item>(cacheRows: false).Single());
    }

    public sealed record Pair(int Id, [property: Column("Id_1")] int OtherId);

    [Fact]
    public void Direct_dictionary_mapping_keeps_all_columns()
    {
        using var db = DuckSession.OpenInMemory();
        var row = db.Query("select 1 as Id, 'a' as Name").To<Dictionary<string, object?>>(cacheRows: false).Single();
        Assert.Equal(1, row["Id"]);
        Assert.Equal("a", row["Name"]);
    }

    [Fact]
    public void Dictionary_registration_streams_after_sample_and_enumerates_once()
    {
        var created = false;
        using var db = DuckSession.OpenInMemory(new() { SampleSize = 1, LogSql = sql => created |= sql.StartsWith("CREATE ") });
        IEnumerable<IReadOnlyDictionary<string, object?>> Source()
        {
            yield return new Dictionary<string, object?> { ["Id"] = 1 };
            Assert.True(created, "The source was fully buffered before creating the table.");
            yield return new Dictionary<string, object?> { ["id"] = 2 };
        }
        var table = db.RegisterRows(new SinglePass<IReadOnlyDictionary<string, object?>>(Source()));
        Assert.Equal(3, db.Scalar<int>($"select sum(Id) from {table}"));
    }

    [Fact]
    public void Null_dictionary_after_sample_reports_its_actual_index()
    {
        using var db = DuckSession.OpenInMemory(new() { SampleSize = 1 });
        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["Id"] = 1 },
            new Dictionary<string, object?> { ["Id"] = 2 },
            null!,
        };
        var error = Assert.Throws<DuckFrameException>(() => db.RegisterRows(rows));
        Assert.Contains("第 2 个元素", error.Message);
        Assert.Empty(db.Tables);
    }

    [Fact]
    public void Columns_outside_sample_must_be_declared()
    {
        using var db = DuckSession.OpenInMemory(new() { SampleSize = 1 });
        var rows = new[] { new Dictionary<string, object?> { ["a"] = 1 }, new Dictionary<string, object?> { ["b"] = 2 } };
        Assert.Throws<DuckFrameException>(() => db.RegisterRows(rows));
        var table = db.RegisterRows(rows, options: new() { ColumnTypes = new Dictionary<string, string> { ["b"] = "INTEGER" } });
        Assert.Equal(2, db.Scalar<int>($"select sum(b) from {table}"));
    }

    [Fact]
    public void Constructor_defaults_and_property_aliases_are_honored()
    {
        using var db = DuckSession.OpenInMemory(new() { MissingColumn = MissingColumnBehavior.DefaultValue });
        Assert.Equal(new Optional(1, null, 7), db.Query("select 1 as Id").To<Optional>().Single());
        Assert.Equal(9, db.Query("select 9 as external_id").To<Aliased>().Single().Id);
        Assert.Equal(new Optional(1, null, 7), db.Query("select 1 as Id").To<Optional>(cacheRows: false).Single());
    }

    [Fact]
    public void Same_inline_source_is_enumerated_once_per_query()
    {
        using var db = DuckSession.OpenInMemory();
        var source = new SinglePass<Item>(new[] { new Item(1) });
        Assert.Equal(1, db.Scalar<int>($"select count(*) from {source} a join {source} b on a.Id=b.Id"));
    }

    [Fact]
    public void Preview_and_uncached_conversion_preserve_all_rows()
    {
        using var db = DuckSession.OpenInMemory();
        using var result = db.Query("select range from range(100)");
        Assert.Contains("20 shown", result.ToString());
        Assert.False(result.IsMaterialized);
        Assert.Equal(Enumerable.Range(0, 100), result.To<int>(cacheRows: false));
        Assert.False(result.IsMaterialized);
        Assert.Throws<DuckFrameException>(() => result.Materialize());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cached_and_direct_mapping_preserve_typed_values(bool cacheRows)
    {
        using var db = DuckSession.OpenInMemory();
        using var result = db.Query("select 1 as Id, null::varchar as Name, 1.234567::decimal(18,6) as Amount, date '2026-01-02' as Day, 'abc'::blob as Blob, [1,null]::int[] as Values");
        var value = result.To<Mapped>(cacheRows).Single();
        Assert.Equal(1, value.Id);
        Assert.Null(value.Name);
        Assert.Equal(1.234567m, value.Amount);
        Assert.Equal(new DateOnly(2026, 1, 2), value.Day);
        Assert.Equal("abc"u8.ToArray(), value.Blob);
        Assert.Equal(new int?[] { 1, null }, value.Values);
    }

    [Fact]
    public void Materialized_results_remain_usable_after_session_disposal()
    {
        var db = DuckSession.OpenInMemory();
        using var result = db.Query("select 42").Materialize();
        db.Dispose();
        Assert.Equal(42, result.To<int>().Single());
    }

    [Fact]
    public void Streaming_result_refuses_competing_consumers_and_disposes_safely()
    {
        using var db = DuckSession.OpenInMemory();
        using var result = db.Query("select range from range(3)");
        using var iterator = result.AsEnumerable<int>().GetEnumerator();
        Assert.True(iterator.MoveNext());
        Assert.Throws<DuckFrameException>(() => result.To<int>());
        db.Dispose();
        Assert.Throws<ObjectDisposedException>(() => iterator.MoveNext());
    }

    [Fact]
    public void Inline_source_can_be_collected_and_its_table_is_cleaned_up()
    {
        using var db = DuckSession.OpenInMemory();
        var weak = RegisterSource(db, inline: true);
        Collect();
        Assert.False(weak.IsAlive);
        Assert.Empty(db.Tables);
        Assert.Equal(0, db.Scalar<int>($"select count(*) from duckdb_tables() where temporary"));
    }

    [Fact]
    public void Disposed_session_releases_explicit_sources()
    {
        var db = DuckSession.OpenInMemory();
        var weak = RegisterSource(db, inline: false);
        Collect();
        Assert.True(weak.IsAlive);
        db.Dispose();
        Collect();
        Assert.False(weak.IsAlive);
        GC.KeepAlive(db);
    }

    [Fact]
    public async Task Concurrent_queries_on_one_session_return_independent_results()
    {
        using var db = DuckSession.OpenInMemory();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 10; i++)
            {
                using var result = db.Query($"select range + {worker} from range(100)");
                Assert.Equal(Enumerable.Range(worker, 100), result.To<int>(cacheRows: false));
            }
        })));
    }

    [Fact]
    public async Task Running_async_query_can_be_cancelled_and_session_reused()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var db = DuckSession.OpenInMemory(new() { LogSql = sql => { if (sql.Contains("cross join")) started.TrySetResult(); } });
        using var cancellation = new CancellationTokenSource();
        var task = db.QueryAsync($"select sum(a.range*b.range) from range(100000) a cross join range(100000) b", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.CancelAfter(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(42, db.Scalar<int>($"select 42"));
    }

    [Fact]
    public void Unsupported_refresh_policy_is_rejected_before_registration()
    {
        Assert.Throws<NotSupportedException>(() => DuckSession.OpenInMemory(new() { RefreshPolicy = RefreshPolicy.OnQuery }));
        using var db = DuckSession.OpenInMemory();
        Assert.Throws<NotSupportedException>(() => db.Register(new[] { new Item(1) }, options: new() { RefreshPolicy = RefreshPolicy.OnQuery }));
        Assert.Empty(db.Tables);
    }

    [Fact]
    public void Multi_statement_list_result_is_readable_but_not_requeried_with_guessed_type()
    {
        using var db = DuckSession.OpenInMemory();
        using var result = db.Query("create table t as select 1 x; select [1.234567::decimal(18,6)] as v");
        Assert.True(result.Columns[0].IsTypeApproximate);
        Assert.Equal(1.234567m, result.To<decimal[]>().Single()[0]);
        var error = Assert.Throws<DuckFrameException>(() => db.Query($"select * from {result}"));
        Assert.Contains("拆成单条 SQL", error.Message);
        Assert.False(result.ToDataTable().Columns[0].ExtendedProperties.ContainsKey("DuckFrame.DuckDbType"));
    }

    [Fact]
    public void Interpolated_lazy_sources_do_not_accumulate_tables_without_gc()
    {
        using var db = DuckSession.OpenInMemory();
        for (var i = 0; i < 50; i++)
            Assert.Equal(2000, db.Scalar<long>($"select count(*) from {Enumerable.Range(0, 2000).Select(x => new Item(x))}"));
        Assert.Empty(db.Tables);
        Assert.Equal(0, TempTableCount(db));
    }

    [Fact]
    public void Inline_table_lives_until_its_result_is_read_or_disposed()
    {
        using var db = DuckSession.OpenInMemory();
        var source = new List<Item> { new(1), new(2) };

        var pending = db.Query($"select Id from {source} order by Id");
        Assert.Equal(1, TempTableCount(db));
        Assert.Equal(new[] { 1, 2 }, pending.To<int>());
        Assert.Equal(0, TempTableCount(db));

        var unread = db.Query($"select Id from {source}");
        Assert.Equal(1, TempTableCount(db));
        unread.Dispose();
        Assert.Equal(0, TempTableCount(db));

        db.Execute($"create table copied as select * from {source}");
        Assert.Equal(0, TempTableCount(db));
        Assert.Throws<DuckDB.NET.Data.DuckDBException>(() => db.Query($"select missing_column from {source}"));
        Assert.Equal(0, TempTableCount(db));
        Assert.Empty(db.Tables);
    }

    [Fact]
    public void Requeried_result_table_is_dropped_when_source_result_is_disposed()
    {
        using var db = DuckSession.OpenInMemory();
        var result = db.Query("select 42 as v");
        Assert.Equal(42, db.Scalar<int>($"select v from {result}"));
        Assert.Equal(42, db.Scalar<int>($"select v from {result}"));
        Assert.Single(db.Tables);
        result.Dispose();
        Assert.Empty(db.Tables);
        Assert.Equal(0, TempTableCount(db));
    }

    [Fact]
    public void Overwrite_that_moves_location_drops_the_old_registered_table_only()
    {
        using var db = DuckSession.OpenInMemory();
        db.Register(new[] { new Item(1) }, "x", options: new() { Persistent = true });
        var moved = db.Register(new[] { new Item(2) }, "x", options: new() { Overwrite = true });
        Assert.Equal(1, db.Scalar<int>($"select count(*) from duckdb_tables() where table_name = 'x'"));
        Assert.Equal(2, db.Scalar<int>($"select Id from {moved}"));
        db.Drop("x");
        Assert.Equal(0, db.Scalar<int>($"select count(*) from duckdb_tables() where table_name = 'x'"));

        // 用户自己建的持久表不归会话所有，临时表覆盖时不能删除它。
        db.Execute($"create table owned as select 99 as Id");
        db.Register(new[] { new Item(3) }, "owned", options: new() { Overwrite = true });
        Assert.Equal(99, db.Scalar<int>($"select Id from memory.main.owned"));
    }

    [Fact]
    public void Preview_shows_full_decimal_scale()
    {
        using var db = DuckSession.OpenInMemory();
        Assert.Contains("1.234567", db.Query("select 1.234567::decimal(18,6) as d").ToString());
    }

    private static int TempTableCount(DuckSession db) =>
        db.Scalar<int>($"select count(*) from duckdb_tables() where temporary");

    private static void AssertNoStagingTables(DuckSession db) =>
        Assert.Equal(0, db.Scalar<int>($"select count(*) from duckdb_tables() where table_name like '__duckframe_stage_%'"));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterSource(DuckSession db, bool inline)
    {
        var source = new List<Item> { new(1) };
        if (inline) db.Scalar<int>($"select count(*) from {source}");
        else db.Register(source);
        return new WeakReference(source);
    }

    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    private sealed class SinglePass<T>(IEnumerable<T> source) : IEnumerable<T>
    {
        private bool _used;
        public IEnumerator<T> GetEnumerator()
        {
            if (_used) throw new InvalidOperationException("Source enumerated twice.");
            _used = true;
            return source.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
