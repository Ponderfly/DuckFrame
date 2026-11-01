# DuckFrame

让 .NET 里的 DuckDB 用起来像 Python 里的 `duckdb` + pandas：**把内存对象直接当表查询，把查询结果直接当对象读回来**。

```csharp
using var db = DuckSession.OpenInMemory();

var orders = new List<Order> { /* ... */ };

var result = db.Query($"""
    SELECT Region, COUNT(*) AS N, SUM(Amount) AS Total
    FROM {orders}                       -- 集合直接插进 SQL 就是一张表
    WHERE Amount > {100m}               -- 值自动参数化
    GROUP BY Region
    """);

result.Print();                          // 终端对齐表格
var stats = result.To<RegionTotal>();    // 强类型结果
var dt = result.ToDataTable();           // 或 DataTable
```

## 特性

- `IEnumerable<T>`（POCO / record）、`DataTable`、字典行（`IEnumerable<Dictionary<string, T>>` 等）直接插进 SQL 即可查询；也可以 `db.Register(x)` 显式注册，表名自动取变量名。
- 插值 SQL：表自动加引号，值自动参数化；**字符串永远不会被当成标识符**。
- 标量集合作为 LIST 参数：`WHERE Id = ANY({ids})`。
- 结果物化：`To<T>()`（record 主构造、`[DuckConstructor]`、无参构造 + 可写属性都支持）、`ToDataTable()`、`ToDictionaries()`、`ToCsv()`、`ToMarkdown()`、`Print()`、`Scalar<T>()`。
- 查询结果可以再插进 SQL（`$"select * from {filtered}"`），列类型（含 DECIMAL 精度）原样保留。
- 标量集合属性存成原生 LIST，嵌套对象存成可查询的 JSON（`col->>'$.City'`）。

## 快速开始

```bash
dotnet restore DuckFrame.slnx
dotnet build   DuckFrame.slnx
dotnet test    tests/DuckFrame.Tests
dotnet run --project samples/DuckFrame.Sample
```

需要 .NET 10 SDK。依赖 **`DuckDB.NET.Data.Full` 1.5.6.1**（锁定在 `Directory.Build.props`）。

> ⚠ 必须用 `.Full` 包。只引用 `DuckDB.NET.Data` 不带原生 libduckdb，测试进程会卡住且没有任何报错。

## 与 Python 对照

| Python (duckdb / pandas) | DuckFrame |
|---|---|
| `duckdb.sql("select * from df")` | `db.Query($"select * from {orders}")` |
| replacement scan 每次读最新的 df | 插值的集合每次查询重新载入 |
| `conn.sql(...).df()` | `.ToDataTable()` / `.To<T>()` / `.ToDictionaries()` |
| `print(df)` / `df.head()` | `.Print()` / `.Print(maxRows: 5)` |
| `duckdb.from_df(df)` | `db.Register(df)`（快照） |

## 语义：插值读最新，注册是快照

| 写法 | 行为 |
|---|---|
| `db.Query($"... from {orders}")` | 每次查询都把 `orders` 重新载入同一张临时表——读到最新内容，循环查询也不会堆积表 |
| `var t = db.Register(orders);` 然后 `{t}` | 注册时**拷贝一次**，之后改 `orders` 不影响 `t`；调用 `db.Refresh(t.Name)` 重新读取（事务内完成，失败保留原数据） |
| `{someResult}`（`DuckResult`） | 首次插值时注册成 `__qN`，之后复用 |

数据量大、要反复查询时用 `Register`，避免每次查询都重新拷贝。

## 已核对的 DuckDB.NET 行为

以下都是实测结论（1.5.6.1），每条都有回归测试（`tests/DuckFrame.Tests/CorrectnessTests.cs`），详见 `docs/spec.md` §0.2。升级 DuckDB.NET 前请先重跑这些测试。

- **appender 不做类型转换**：`int` 追加到 `DOUBLE` 列会读成 `2.5E-323`，超出列小数位的 decimal 会被静默截断。DuckFrame 按目标列类型转换每个值，decimal 小数位溢出直接报错。
- **插值字符串默认匹配 `string` 重载**：不处理的话值会被 `ToString()` 拼进 SQL。`Query(FormattableString)` 用 `[OverloadResolutionPriority(1)]` 压过它。
- **BLOB 读出来是指向原生内存的流**：reader 释放后再读就是释放后使用，DuckFrame 在读取时立即拷成 `byte[]`。
- **`SUM(INTEGER)` 返回 HUGEINT**（`BigInteger`），DuckFrame 负责转成 `long` 等类型。
- **LIST 的 DDL 只能写 `INTEGER[]`**，`LIST(INTEGER)` 是解析错误。
- `GetDataTypeName` 返回 `Decimal`、`List` 这类枚举名，DECIMAL 精度要从 `GetSchemaTable()` 取。
- 参数在 SQL 里写 `$p0`、`ParameterName` 写 `p0`；TEMP 表不需要 `temp.` 前缀。

## 已知限制

1. **嵌套对象 / 字典存为 JSON**，不是原生 STRUCT / MAP（appender 没有对应写入方法）。原生支持在 Phase 2。
2. **`To<T>()` 的性能还没达标**：10 万行 × 8 列约 311 ms，是原生 reader 类型化读取（约 102 ms）的 3 倍。原因是逐行装箱的物化方式，下一步改为列式物化（`docs/spec.md` §10）。注册已达标：10 万行约 219 ms，100 万行约 1.5 s，托管内存几乎不增长。以上为本机 Stopwatch 测量，不是 BenchmarkDotNet 结果。
3. `RefreshPolicy.OnQuery` 尚未实现，目前只有显式 `Refresh()`。
4. 并发测试还没补；会话内部已用锁串行化，但查询结果的读取在锁外进行。
5. 一个结果被流式消费（`AsEnumerable<T>()`）后不能再次读取。

## 目录

```
docs/spec.md                     完整规格（§0.2 为实测结论）
src/DuckFrame/                   库
tests/DuckFrame.Tests/           xUnit 测试（46 个）
samples/DuckFrame.Sample/        五个可运行示例
benchmarks/DuckFrame.Benchmarks/ BenchmarkDotNet
```
