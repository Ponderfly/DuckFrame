namespace DuckFrame.Benchmarks.Analytics;

public enum Analysis { FilterSummary, MonthlyGroups, JoinCustomers, JoinMillionPayments, JoinGroups, Top20 }
public enum Backend { Linq, DuckFrame, NativeDuckDb }

public sealed record Sale(int Id, int CustomerId, int ProductId, DateTime OrderDate, int Quantity, decimal Amount, bool Paid, string Channel);
public sealed record Customer(int Id, string Region, int Segment);
public sealed record Product(int Id, string Category);
public sealed record Payment(int OrderId, decimal Fee, bool Settled);

public sealed record SummaryRow(long OrderCount, long Units, decimal Revenue, decimal MinAmount, decimal MaxAmount);
public sealed record MonthlyRow(string Channel, int Month, long OrderCount, long Units, decimal Revenue);
public sealed record CustomerJoinRow(int OrderId, int CustomerId, string Region, int Segment, int Quantity, decimal Amount);
public sealed record PaymentJoinRow(int OrderId, int Quantity, decimal Amount, decimal Fee, bool Settled);
public sealed record GroupRow(string Region, string Category, long OrderCount, long Units, decimal Revenue);

public sealed class AnalyticsData
{
    public const int Seed = 20261101;
    public static readonly DateTime Cutoff = new(2026, 7, 1);
    public required List<Sale> Sales { get; init; }
    public required List<Customer> Customers { get; init; }
    public required List<Product> Products { get; init; }
    public required List<Payment> Payments { get; init; }

    public static AnalyticsData Create(int count, Analysis analysis)
    {
        var random = new Random(Seed);
        var regions = new string[20];
        var categories = new string[40];
        for (var i = 0; i < regions.Length; i++) regions[i] = $"R{i:D2}";
        for (var i = 0; i < categories.Length; i++) categories[i] = $"C{i:D2}";
        string[] channels = ["web", "store", "partner", "app"];
        var customers = new List<Customer>(Math.Min(100_000, Math.Max(1, count / 10)));
        var products = new List<Product>(Math.Min(1_000, count));
        for (var i = 1; i <= customers.Capacity; i++) customers.Add(new(i, regions[i * 17 % 20], i % 5));
        for (var i = 1; i <= products.Capacity; i++) products.Add(new(i, categories[i * 7 % 40]));
        var sales = new List<Sale>(count);
        var payments = new List<Payment>(analysis == Analysis.JoinMillionPayments ? count : 0);
        var start = new DateTime(2026, 1, 1);
        for (var i = 1; i <= count; i++)
        {
            var customer = random.Next(customers.Count) + 1;
            var product = random.Next(products.Count) + 1;
            var date = start.AddDays(random.Next(365));
            var quantity = random.Next(1, 11);
            var amount = random.Next(100, 100_001) * quantity / 100m;
            var paid = random.Next(10) < 8;
            var channel = channels[random.Next(channels.Length)];
            sales.Add(new(i, customer, product, date, quantity, amount, paid, channel));
            if (analysis == Analysis.JoinMillionPayments) payments.Add(new(i, (i % 197 + 1) / 100m, i % 7 != 0));
        }
        return new() { Sales = sales, Customers = customers, Products = products, Payments = payments };
    }
}

internal struct Totals
{
    public long Count;
    public long Units;
    public decimal Revenue;

    public Totals Add(Sale sale)
    {
        Count++;
        Units += sale.Quantity;
        Revenue += sale.Amount;
        return this;
    }
}

internal struct RangedTotals
{
    public Totals Totals;
    public decimal Min;
    public decimal Max;
    public RangedTotals Add(Sale sale)
    {
        Min = Totals.Count == 0 ? sale.Amount : Math.Min(Min, sale.Amount);
        Max = Totals.Count == 0 ? sale.Amount : Math.Max(Max, sale.Amount);
        Totals = Totals.Add(sale);
        return this;
    }
}

internal static class AnalyticsQueries
{
    private readonly record struct CustomerSale(Sale Sale, Customer Customer);
    private readonly record struct CategorizedSale(Sale Sale, string Region, string Category);

    public static object Linq(AnalyticsData data, Analysis analysis) => analysis switch
    {
        Analysis.FilterSummary => Summary(data),
        Analysis.MonthlyGroups => data.Sales.Where(static s => s.Paid)
            .GroupBy(static s => (s.Channel, Month: s.OrderDate.Month))
            .Select(static group =>
            {
                var total = group.Aggregate(default(Totals), static (t, s) => t.Add(s));
                return new MonthlyRow(group.Key.Channel, group.Key.Month, total.Count, total.Units, total.Revenue);
            }).OrderBy(static g => g.Channel, StringComparer.Ordinal).ThenBy(static g => g.Month).ToList(),
        Analysis.JoinCustomers => data.Sales.Join(data.Customers, static s => s.CustomerId, static c => c.Id,
            static (s, c) => new CustomerJoinRow(s.Id, c.Id, c.Region, c.Segment, s.Quantity, s.Amount)).ToList(),
        Analysis.JoinMillionPayments => data.Sales.Join(data.Payments, static s => s.Id, static p => p.OrderId,
            static (s, p) => new PaymentJoinRow(s.Id, s.Quantity, s.Amount, p.Fee, p.Settled)).ToList(),
        Analysis.JoinGroups => Groups(data).OrderBy(static g => g.Region, StringComparer.Ordinal)
            .ThenBy(static g => g.Category, StringComparer.Ordinal).ToList(),
        Analysis.Top20 => Groups(data).OrderByDescending(static g => g.Revenue)
            .ThenBy(static g => g.Region, StringComparer.Ordinal).ThenBy(static g => g.Category, StringComparer.Ordinal).Take(20).ToList(),
        _ => throw new ArgumentOutOfRangeException(nameof(analysis)),
    };

    private static List<SummaryRow> Summary(AnalyticsData data)
    {
        // 一次 Aggregate 同时完成全部汇总，避免多次枚举偏袒 SQL。
        var total = data.Sales.Where(static s => s.Paid && s.Quantity >= 3 && s.OrderDate >= AnalyticsData.Cutoff)
            .Aggregate(default(RangedTotals), static (t, s) => t.Add(s));
        return [new(total.Totals.Count, total.Totals.Units, total.Totals.Revenue, total.Min, total.Max)];
    }

    private static IEnumerable<GroupRow> Groups(AnalyticsData data) => data.Sales.Where(static s => s.Paid && s.Quantity >= 2)
        .Join(data.Customers, static s => s.CustomerId, static c => c.Id, static (s, c) => new CustomerSale(s, c))
        .Join(data.Products, static s => s.Sale.ProductId, static p => p.Id,
            static (s, p) => new CategorizedSale(s.Sale, s.Customer.Region, p.Category))
        .GroupBy(static s => (s.Region, s.Category), static s => s.Sale)
        .Select(static group =>
        {
            var total = group.Aggregate(default(Totals), static (t, s) => t.Add(s));
            return new GroupRow(group.Key.Region, group.Key.Category, total.Count, total.Units, total.Revenue);
        });

    public static string Sql(Analysis analysis) => analysis switch
    {
        Analysis.FilterSummary => """
            SELECT count(*) AS OrderCount, coalesce(sum(Quantity), 0)::BIGINT AS Units,
                coalesce(sum(Amount), 0) AS Revenue, coalesce(min(Amount), 0) AS MinAmount, coalesce(max(Amount), 0) AS MaxAmount
            FROM sales WHERE Paid AND Quantity >= 3 AND OrderDate >= DATE '2026-07-01'
            """,
        Analysis.MonthlyGroups => """
            SELECT Channel, month(OrderDate)::INTEGER AS Month, count(*) AS OrderCount,
                sum(Quantity)::BIGINT AS Units, sum(Amount) AS Revenue
            FROM sales WHERE Paid GROUP BY Channel, month(OrderDate) ORDER BY Channel, Month
            """,
        Analysis.JoinCustomers => """
            SELECT s.Id AS OrderId, c.Id AS CustomerId, c.Region, c.Segment, s.Quantity, s.Amount
            FROM sales s JOIN customers c ON s.CustomerId = c.Id
            """,
        Analysis.JoinMillionPayments => """
            SELECT s.Id AS OrderId, s.Quantity, s.Amount, p.Fee, p.Settled
            FROM sales s JOIN payments p ON s.Id = p.OrderId
            """,
        Analysis.JoinGroups => GroupSql + " ORDER BY Region, Category",
        Analysis.Top20 => GroupSql + " ORDER BY Revenue DESC, Region, Category LIMIT 20",
        _ => throw new ArgumentOutOfRangeException(nameof(analysis)),
    };

    private const string GroupSql = """
        SELECT c.Region, p.Category, count(*) AS OrderCount, sum(s.Quantity)::BIGINT AS Units, sum(s.Amount) AS Revenue
        FROM sales s JOIN customers c ON s.CustomerId=c.Id JOIN products p ON s.ProductId=p.Id
        WHERE s.Paid AND s.Quantity >= 2 GROUP BY c.Region, p.Category
        """;
}

/// <summary>独立的逐行 oracle；不使用被测 LINQ 查询，且在首次计时查询之后构造。</summary>
internal sealed class AnalyticsValidator
{
    private readonly AnalyticsData _data;
    private readonly Analysis _analysis;
    private readonly bool[]? _visited;
    private readonly object? _expected;

    public AnalyticsValidator(AnalyticsData data, Analysis analysis)
    {
        _data = data;
        _analysis = analysis;
        if (analysis is Analysis.JoinCustomers or Analysis.JoinMillionPayments)
        {
            _visited = new bool[data.Sales.Count];
            return;
        }
        var summary = default(RangedTotals);
        var monthly = new Dictionary<(string Channel, int Month), Totals>();
        var groups = new Dictionary<(string Region, string Category), Totals>();
        foreach (var sale in data.Sales)
        {
            if (!sale.Paid) continue;
            if (analysis == Analysis.FilterSummary && sale.Quantity >= 3 && sale.OrderDate >= AnalyticsData.Cutoff)
                summary = summary.Add(sale);
            if (analysis == Analysis.MonthlyGroups)
            {
                var key = (sale.Channel, sale.OrderDate.Month);
                monthly.TryGetValue(key, out var total);
                monthly[key] = total.Add(sale);
            }
            if (analysis is Analysis.JoinGroups or Analysis.Top20 && sale.Quantity >= 2)
            {
                var key = (data.Customers[sale.CustomerId - 1].Region, data.Products[sale.ProductId - 1].Category);
                groups.TryGetValue(key, out var total);
                groups[key] = total.Add(sale);
            }
        }
        if (analysis == Analysis.FilterSummary)
            _expected = new SummaryRow(summary.Totals.Count, summary.Totals.Units, summary.Totals.Revenue, summary.Min, summary.Max);
        else if (analysis == Analysis.MonthlyGroups)
        {
            var expected = new List<MonthlyRow>();
            foreach (var (key, total) in monthly) expected.Add(new(key.Channel, key.Month, total.Count, total.Units, total.Revenue));
            expected.Sort(static (a, b) => CompareMonthly(a, b));
            _expected = expected;
        }
        else
        {
            var expected = new List<GroupRow>();
            foreach (var (key, total) in groups) expected.Add(new(key.Region, key.Category, total.Count, total.Units, total.Revenue));
            expected.Sort((a, b) => analysis == Analysis.Top20 && a.Revenue != b.Revenue
                ? b.Revenue.CompareTo(a.Revenue) : CompareGroup(a, b));
            if (analysis == Analysis.Top20 && expected.Count > 20) expected.RemoveRange(20, expected.Count - 20);
            _expected = expected;
        }
    }

    public int Validate(object result)
    {
        switch (_analysis)
        {
            case Analysis.FilterSummary:
                var summary = (List<SummaryRow>)result;
                Require(summary.Count == 1 && summary[0] == (SummaryRow)_expected!, "summary");
                return 1;
            case Analysis.MonthlyGroups:
                var monthly = (List<MonthlyRow>)result;
                Require(monthly.SequenceEqual((List<MonthlyRow>)_expected!), "monthly aggregates/order");
                return monthly.Count;
            case Analysis.JoinGroups:
            case Analysis.Top20:
                var groups = (List<GroupRow>)result;
                Require(groups.SequenceEqual((List<GroupRow>)_expected!), "join aggregates/order");
                return groups.Count;
            case Analysis.JoinCustomers:
                var customers = (List<CustomerJoinRow>)result;
                Require(customers.Count == _data.Sales.Count, "customer join cardinality");
                Array.Clear(_visited!);
                foreach (var row in customers)
                {
                    var sale = Visit(row.OrderId);
                    var customer = _data.Customers[sale.CustomerId - 1];
                    Require(row.CustomerId == customer.Id && row.Region == customer.Region && row.Segment == customer.Segment &&
                        row.Quantity == sale.Quantity && row.Amount == sale.Amount, "customer join values");
                }
                return customers.Count;
            case Analysis.JoinMillionPayments:
                var payments = (List<PaymentJoinRow>)result;
                Require(payments.Count == _data.Sales.Count, "million join cardinality");
                Array.Clear(_visited!);
                foreach (var row in payments)
                {
                    var sale = Visit(row.OrderId);
                    var payment = _data.Payments[row.OrderId - 1];
                    Require(row.Quantity == sale.Quantity && row.Amount == sale.Amount && row.Fee == payment.Fee &&
                        row.Settled == payment.Settled, "million join values");
                }
                return payments.Count;
            default: throw new ArgumentOutOfRangeException();
        }
    }

    private Sale Visit(int id)
    {
        Require(id > 0 && id <= _data.Sales.Count && !_visited![id - 1], "duplicate/missing order");
        _visited![id - 1] = true;
        return _data.Sales[id - 1];
    }
    private static int CompareMonthly(MonthlyRow a, MonthlyRow b) => a.Channel != b.Channel
        ? StringComparer.Ordinal.Compare(a.Channel, b.Channel) : a.Month.CompareTo(b.Month);
    private static int CompareGroup(GroupRow a, GroupRow b) => a.Region != b.Region
        ? StringComparer.Ordinal.Compare(a.Region, b.Region) : StringComparer.Ordinal.Compare(a.Category, b.Category);
    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Benchmark correctness check failed: " + label);
    }
}
