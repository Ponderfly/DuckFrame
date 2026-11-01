namespace DuckFrame.Tests;

public sealed record Order(int Id, string Region, decimal Amount, DateTime CreatedAt);

public sealed record RegionTotal(string Region, long N, decimal Total);

public sealed record TypeZoo(
    bool Bool,
    int Int,
    long Long,
    double Double,
    decimal Decimal,
    string? Text,
    Guid Uuid,
    DateTime Timestamp,
    DateOnly Date,
    TimeSpan Interval,
    byte[] Blob);

public sealed class SessionFixture : IDisposable
{
    public DuckSession Session { get; } = DuckSession.OpenInMemory();

    public void Dispose() => Session.Dispose();
}
