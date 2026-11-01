using DuckDB.NET.Data;

namespace DuckFrame.Benchmarks.Analytics;

internal sealed class AnalyticsEngine(AnalyticsData data, Analysis analysis, Backend backend, int threads) : IDisposable
{
    private DuckSession? _session;
    private DuckDBConnection? _connection;
    private readonly string _sql = AnalyticsQueries.Sql(analysis);
    public int EffectiveThreads { get; private set; } = 1;

    public void Initialize()
    {
        if (backend == Backend.Linq) return;
        if (backend == Backend.DuckFrame)
        {
            _session = DuckSession.OpenInMemory();
            _connection = _session.Connection;
        }
        else
        {
            _connection = new DuckDBConnection("Data Source=:memory:");
            _connection.Open();
        }
        if (threads > 0) Execute("SET threads = " + threads);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT current_setting('threads')";
            EffectiveThreads = Convert.ToInt32(command.ExecuteScalar());
        }

        var customers = analysis is Analysis.JoinCustomers or Analysis.JoinGroups or Analysis.Top20;
        var products = analysis is Analysis.JoinGroups or Analysis.Top20;
        if (_session is not null)
        {
            _session.Register(data.Sales, "sales");
            if (customers) _session.Register(data.Customers, "customers");
            if (products) _session.Register(data.Products, "products");
            if (analysis == Analysis.JoinMillionPayments) _session.Register(data.Payments, "payments");
            return;
        }

        Execute("""
            CREATE TEMP TABLE sales(Id INTEGER, CustomerId INTEGER, ProductId INTEGER, OrderDate TIMESTAMP,
                Quantity INTEGER, Amount DECIMAL(18,4), Paid BOOLEAN, Channel VARCHAR)
            """);
        using (var appender = _connection.CreateAppender("sales"))
        {
            foreach (var sale in data.Sales)
            {
                var row = appender.CreateRow();
                row.AppendValue(sale.Id);
                row.AppendValue(sale.CustomerId);
                row.AppendValue(sale.ProductId);
                row.AppendValue(sale.OrderDate);
                row.AppendValue(sale.Quantity);
                row.AppendValue(sale.Amount);
                row.AppendValue(sale.Paid);
                row.AppendValue(sale.Channel);
                row.EndRow();
            }
            appender.Close();
        }
        if (customers)
        {
            Execute("CREATE TEMP TABLE customers(Id INTEGER, Region VARCHAR, Segment INTEGER)");
            using var appender = _connection.CreateAppender("customers");
            foreach (var customer in data.Customers)
            {
                var row = appender.CreateRow();
                row.AppendValue(customer.Id);
                row.AppendValue(customer.Region);
                row.AppendValue(customer.Segment);
                row.EndRow();
            }
            appender.Close();
        }
        if (products)
        {
            Execute("CREATE TEMP TABLE products(Id INTEGER, Category VARCHAR)");
            using var appender = _connection.CreateAppender("products");
            foreach (var product in data.Products)
            {
                var row = appender.CreateRow();
                row.AppendValue(product.Id);
                row.AppendValue(product.Category);
                row.EndRow();
            }
            appender.Close();
        }
        if (analysis == Analysis.JoinMillionPayments)
        {
            Execute("CREATE TEMP TABLE payments(OrderId INTEGER, Fee DECIMAL(18,4), Settled BOOLEAN)");
            using var appender = _connection.CreateAppender("payments");
            foreach (var payment in data.Payments)
            {
                var row = appender.CreateRow();
                row.AppendValue(payment.OrderId);
                row.AppendValue(payment.Fee);
                row.AppendValue(payment.Settled);
                row.EndRow();
            }
            appender.Close();
        }
    }

    public object Query()
    {
        if (backend == Backend.Linq) return AnalyticsQueries.Linq(data, analysis);
        return analysis switch
        {
            Analysis.FilterSummary => Read<SummaryRow>(static r => new(r.GetInt64(0), r.GetInt64(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4))),
            Analysis.MonthlyGroups => Read<MonthlyRow>(static r => new(r.GetString(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetDecimal(4))),
            Analysis.JoinCustomers => Read<CustomerJoinRow>(static r => new(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetDecimal(5))),
            Analysis.JoinMillionPayments => Read<PaymentJoinRow>(static r => new(r.GetInt32(0), r.GetInt32(1), r.GetDecimal(2), r.GetDecimal(3), r.GetBoolean(4))),
            Analysis.JoinGroups or Analysis.Top20 => Read<GroupRow>(static r => new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetDecimal(4))),
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private List<T> Read<T>(Func<DuckDBDataReader, T> map)
    {
        if (_session is not null)
        {
            using var result = _session.Query(_sql);
            return result.To<T>(cacheRows: false);
        }
        using var command = _connection!.CreateCommand();
        command.CommandText = _sql;
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(map(reader));
        return rows;
    }

    private void Execute(string sql)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_session is not null) _session.Dispose();
        else _connection?.Dispose();
    }
}
