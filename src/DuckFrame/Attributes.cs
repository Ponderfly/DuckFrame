namespace DuckFrame;

/// <summary>指定注册时使用的表名，优先于变量名。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class TableAttribute : Attribute
{
    public TableAttribute(string name) => Name = name;

    public string Name { get; }
}

/// <summary>指定列名。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ColumnAttribute : Attribute
{
    public ColumnAttribute(string name) => Name = name;

    public string Name { get; }
}

/// <summary>指定物化时使用的构造函数。</summary>
[AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false)]
public sealed class DuckConstructorAttribute : Attribute;

/// <summary>指定 decimal 列的 DuckDB 精度与小数位。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class DuckPrecisionAttribute : Attribute
{
    public DuckPrecisionAttribute(int precision, int scale)
    {
        Precision = precision;
        Scale = scale;
    }

    public int Precision { get; }

    public int Scale { get; }
}
