# DuckFrame

让 .NET 里的 DuckDB 用起来像 Python 里的 `duckdb` + pandas：**把内存对象直接当表查询，把查询结果直接当对象读回来**。

```csharp
using var db = DuckSession.OpenInMemory();

var orders = new List<Order> { /* ... */ };

using var result = db.Query($"""
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
| `db.Query($"... from {orders}")` | 每次查询都把 `orders` 载入一张新的临时表 `__pN`，读到最新内容；该结果读完、`Dispose` 或被回收后立即删表，`Execute` 执行完即删——循环查询不会堆积表，也不持有源对象 |
| `var t = db.Register(orders);` 然后 `{t}` | 注册时**拷贝一次**，之后改 `orders` 不影响 `t`；调用 `db.Refresh(t.Name)` 重新读取（先写暂存表再原子替换，失败保留原数据） |
| `{someResult}`（`DuckResult`） | 首次插值时注册成 `__qN`，之后复用；`someResult.Dispose()` 时删表 |

数据量大、要反复查询时用 `Register`，避免每次查询都重新拷贝。同一条 SQL 中对同一个源的多次引用只载入一次。

`Overwrite=false` 会避开数据库内已有的表和视图。覆盖和刷新先写入独立暂存表，再用单条原子 DDL 发布；失败保留旧数据，也能参与调用方已有的事务。表句柄和 appender 使用完整 catalog/schema 限定，避免 TEMP 表遮蔽同名持久表。

自动注册表的清理是确定性的，不依赖 GC：集合 / DataTable 的表跟随使用它的结果，查询结果的表跟随源结果的 `Dispose`。结果未释放就被回收时，下次查询、注册或访问 `Tables` 兜底清理。因此不要用插值集合建 VIEW（`create view v as select * from {list}`），视图会在表删除后失效——需要长期存在的表请用 `Register`。显式 `Register` 保留源对象以便 `Refresh`，可通过 `Drop` 或释放会话解除引用。

`Overwrite=true` 把已登记的表从持久改为临时（或反过来）时，会删除本会话登记的旧表；用户自己建的同名表不会被删除。

结果中有同名列（如 `select a.*, b.*`）时，默认按 DuckDB 的规则重命名（忽略大小写）：`Id, Id` → `Id, Id_1`，`a, a, a_1` → `a, a_1, a_1_1`，与 DuckDB 用该结果建表得到的列名一致，所以 `ToDictionaries()`、`ToDataTable()`、再次插值都能直接使用。映射到 POCO 时，多出的 `Id_1` 按 `ExtraColumn` 规则处理，不会顶替 `Id`；可以用 `[Column("Id_1")]` 显式映射。设置 `DuplicateColumns = DuplicateColumnBehavior.Throw` 则保留原名，需要唯一列名的操作会报错。多语句 SQL 返回的 LIST 列拿不到原生元素类型（`DuckColumn.IsTypeApproximate`），可以正常读取，但不能再插进 SQL 当表使用。

## 结果读取与取消

```csharp
using var result = db.Query($"select * from {orders}");
result.Print(maxRows: 20);                        // 只读取预览行及一行前瞻
var rows = result.To<Order>(cacheRows: false);    // 类型化 reader 直接构造对象，不保留原始行缓存
```

- 默认 `To<T>()` 会缓存原始行，支持重复转换；`cacheRows: false` 和 `AsEnumerable<T>()` 只消费一次，适合大结果。
- 预览行会保留，随后完整读取不会漏行；尚未读到末尾时，预览标注 `at least`，不伪报总行数。
- 会话、结果读取和释放共享同一把锁；同一个结果的并行消费者会得到明确异常。直接使用 `Connection` 的代码需自行同步，外部 SQL/事务回滚对表登记信息的影响不自动跟踪。
- `QueryAsync` 在线程池执行，并可中断正在运行的 DuckDB 查询。等待会话锁和加载插值数据期间，在执行查询前检查取消，不抢占用户的数据源枚举。
- `MaterializeAsync` 仍是同步读取接口。预先取消不会移动 reader；中途取消或读取失败会关闭 reader，之后必须重新查询。
- `ToDataTable()` 保留数值类型、NULL、BLOB 和 decimal 精度；字典行的列名忽略大小写，同一行内重复列名明确报错；查询结果的同名列默认自动重命名。采样之外出现的新字典列需用 `ColumnTypes` 声明或增加 `SampleSize`。

## 已核对的 DuckDB.NET 行为

以下都是实测结论（1.5.6.1），每条都有回归测试（`tests/DuckFrame.Tests/CorrectnessTests.cs`），详见 `docs/spec.md` §0.2。升级 DuckDB.NET 前请先重跑这些测试。

- **appender 不做类型转换**：`int` 追加到 `DOUBLE` 列会读成 `2.5E-323`，超出列小数位的 decimal 会被静默截断。DuckFrame 按目标列类型转换每个值，decimal（含 LIST 元素）小数位溢出直接报错。
- **插值字符串默认匹配 `string` 重载**：不处理的话值会被 `ToString()` 拼进 SQL。`Query(FormattableString)` 用 `[OverloadResolutionPriority(1)]` 压过它。
- **BLOB 读出来是指向原生内存的流**：reader 释放后再读就是释放后使用，DuckFrame 在读取时立即拷成 `byte[]`。
- **`SUM(INTEGER)` 返回 HUGEINT**（`BigInteger`），DuckFrame 负责转成 `long` 等类型。
- **LIST 的 DDL 只能写 `INTEGER[]`**，`LIST(INTEGER)` 是解析错误。
- `GetDataTypeName` 返回 `Decimal`、`List` 这类枚举名，标量 DECIMAL 精度从 `GetSchemaTable()` 取；LIST 使用 DuckDB 公开 C API 的预备语句类型元数据，参数类型在需要时通过单语句 `DESCRIBE` 确认。
- 参数在 SQL 里写 `$p0`、`ParameterName` 写 `p0`；TEMP 表不需要 `temp.` 前缀。

## 性能验证

```bash
dotnet run -c Release --project benchmarks/DuckFrame.Benchmarks -- --filter '*MaterializationBenchmarks*' --job short
```

基准实际返回 10 万行 × 8 列，将建库、注册放在物化基准的 setup 中，分别测量原生类型化 reader、默认行缓存和不缓存的直接映射。注册单独测量。以当前机器上的 BenchmarkDotNet 结果比较耗时及分配，不沿用旧实现的 Stopwatch 数字。此次同进程短基准中，直接映射约 126.6 ms（原生 112.6 ms，约 1.13 倍），托管分配 17.3 MB；详见 [测量环境与报告](docs/benchmarks-2026-11-01.md)。

百万行 LINQ / DuckFrame / 原生 DuckDB 分析对比使用独立进程计时器，分别记录载入、首次查询和预热后的查询。覆盖过滤汇总、月度分组、百万行连接、两次连接后汇总及 Top 20；每次结果均做独立校验。

```bash
dotnet run -c Release --project benchmarks/DuckFrame.Benchmarks -- \
  --analytics --rows 1000000 --repeats 3 --samples 9 --warmups 5 --warmup-ms 1000 \
  --output artifacts/analytics-1m
python3 benchmarks/DuckFrame.Benchmarks/Analytics/report.py \
  artifacts/analytics-1m docs/analytics-1m.md
```

完整结果见 [百万行复杂分析对比报告](docs/analytics-1m-2026-11-01.md)，包含首次查询、预热后查询、单线程/默认线程及逐次原始数据。

使用 `--rows 10000 --repeats 1 --samples 1 --warmups 1 --warmup-ms 0` 可快速预检查询正确性。报告生成器只依赖 Python 标准库；可选的 `--plot` 绘图功能需要 matplotlib。

## 已知限制

1. 嵌套对象 / 字典仍存为 JSON，原生 STRUCT / MAP 写入属于后续工作。
2. 默认 `To<T>()` 为重复读取保留装箱行缓存；需要较低分配时使用 `To<T>(cacheRows: false)`。列式缓存尚未实现。
3. `RefreshPolicy.OnQuery` 尚未实现，设置时直接抛出 `NotSupportedException`；使用显式 `Refresh()`。
4. 精确 LIST 元数据目前要求单条查询；无法取得完整类型时明确报错，不猜测 decimal 精度。
5. `AsEnumerable<T>()` 提前结束也会关闭 reader；结果无法再次读取。已完整物化的结果可以在会话释放后继续使用。

## 目录

```
docs/spec.md                     完整规格（§0.2 为实测结论）
src/DuckFrame/                   库
tests/DuckFrame.Tests/           xUnit 正确性与边界回归测试
samples/DuckFrame.Sample/        五个可运行示例
benchmarks/DuckFrame.Benchmarks/ BenchmarkDotNet
```
