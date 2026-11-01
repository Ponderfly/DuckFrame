using System.Data;
using DuckFrame;

// docs/spec.md §2.1 的五个示例，全部可运行。

using var db = DuckSession.OpenInMemory(new DuckSessionOptions
{
    LogSql = Console.IsOutputRedirected ? null : sql => Console.WriteLine($"[sql] {sql}"),
});

// ---------------------------------------------------------------- 例 1
var orders = new List<Order>
{
    new(1, "EU", 12.5m, new DateTime(2026, 1, 2, 10, 0, 0)),
    new(2, "EU", 80.0m, new DateTime(2026, 1, 3, 11, 30, 0)),
    new(3, "US", 250.0m, new DateTime(2026, 1, 4, 9, 15, 0)),
    new(4, "US", 40.0m, new DateTime(2026, 1, 5, 16, 45, 0)),
    new(5, "APAC", 17.25m, new DateTime(2026, 1, 6, 8, 0, 0)),
};

Console.WriteLine("== 例 1：IList<POCO> ==");
var byRegion = db.Query($"""
    SELECT Region, COUNT(*) AS N, SUM(Amount) AS Total
    FROM {orders}
    WHERE Amount > {20m}
    GROUP BY Region
    ORDER BY Total DESC
    """);
byRegion.Print();

var stats = byRegion.To<RegionTotal>();
Console.WriteLine($"强类型：{string.Join(", ", stats.Select(s => $"{s.Region}={s.Total}"))}");
Console.WriteLine();

// ---------------------------------------------------------------- 例 2
Console.WriteLine("== 例 2：DataTable ==");
var dt = new DataTable("Inventory");
dt.Columns.Add("Item", typeof(string));
dt.Columns.Add("Qty", typeof(int));
dt.Rows.Add("apple", 5);
dt.Rows.Add("pear", 52);
dt.Rows.Add("plum", 11);

db.Query($"SELECT * FROM {dt} WHERE Qty > 10 ORDER BY Qty").Print();
Console.WriteLine();

// ---------------------------------------------------------------- 例 3
Console.WriteLine("== 例 3：字典行 ==");
var events = new List<IReadOnlyDictionary<string, object?>>
{
    new Dictionary<string, object?> { ["kind"] = "click", ["count"] = 1 },
    new Dictionary<string, object?> { ["kind"] = "click", ["count"] = 1 },
    new Dictionary<string, object?> { ["kind"] = "view", ["count"] = 1 },
};

db.Query($"SELECT kind, COUNT(*) AS N FROM {events} GROUP BY 1 ORDER BY 2 DESC").Print();
Console.WriteLine();

// ---------------------------------------------------------------- 例 4
Console.WriteLine("== 例 4：查询结果再查询 ==");
var filtered = db.Query($"SELECT * FROM {orders} WHERE Amount > {30m}");
var avg = db.Query($"SELECT AVG(Amount) FROM {filtered}").To<decimal>().Single();
Console.WriteLine($"AVG(Amount > 30) = {avg:0.00}");
Console.WriteLine();

// ---------------------------------------------------------------- 例 5
Console.WriteLine("== 例 5：与其他数据源 join ==");
var customers = new List<Customer>
{
    new(1, "Acme"),
    new(2, "Globex"),
    new(3, "Initech"),
    new(4, "Umbrella"),
    new(5, "Hooli"),
};

db.Query($"""
    SELECT o.Id, c.Name AS Customer
    FROM {orders} o
    JOIN {customers} c ON o.Id = c.Id
    ORDER BY o.Id
    """).Print();

public sealed record Order(int Id, string Region, decimal Amount, DateTime CreatedAt);

public sealed record RegionTotal(string Region, long N, decimal Total);

public sealed record Customer(int Id, string Name);
