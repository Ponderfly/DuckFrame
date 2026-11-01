using Xunit;

namespace DuckFrame.Tests;

public sealed class SqlFormattingTests
{
    [Theory]
    [InlineData("orders", "orders")]
    [InlineData("this._orders", "orders")]
    [InlineData("_orders", "orders")]
    [InlineData("GetOrders()", "GetOrders")]
    [InlineData("dict[\"x\"]", "dict")]
    [InlineData("a.b.C", "C")]
    public void CleanExpression_extracts_identifier(string expression, string expected)
    {
        var before = expression.Length;

        var result = NameResolver.CleanExpression(expression);

        Assert.Equal(expected, result);
        Assert.True(before > 0);
    }

    [Fact]
    public void Quote_escapes_embedded_quotes()
    {
        Assert.Equal("\"a\"\"b\"", NameResolver.Quote("a\"b"));
    }

    [Fact]
    public void MakeUnique_appends_suffix()
    {
        Assert.Equal("orders_2", NameResolver.MakeUnique("orders", ["orders"]));
        Assert.Equal("orders_3", NameResolver.MakeUnique("orders", ["orders", "orders_2"]));
    }

    [Fact]
    public void Interpolation_rejects_unbalanced_braces()
    {
        using var db = DuckSession.OpenInMemory();

        // 编译期无法写出不平衡的插值字符串，这里用工厂构造。
        var broken = System.Runtime.CompilerServices.FormattableStringFactory.Create("select '{0'");

        Assert.Throws<DuckFrameException>(() => { _ = db.Query(broken); });
    }
}
