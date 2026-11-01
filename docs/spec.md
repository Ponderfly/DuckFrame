# DuckFrame — 规格说明（Spec）

> 本文档是实现规格，不是 API 手册。实现方（人或编码 Agent）应逐条落实并补齐测试。
> 目标框架：**net10.0**（`LangVersion=latest`，C# 14）。`CallerArgumentExpressionAttribute` 在 .NET 10 中已有原生支持。

---

## 0. 版本与前提（实现前必须先核对）

- 目标框架：`net10.0`（唯一目标，不做多目标）。测试与基准同样是 `net10.0`。
- 依赖：**`DuckDB.NET.Data.Full`**（NuGet，内含原生 libduckdb）。版本锁定在 `Directory.Build.props` 的 `DuckDbNetVersion`，并在 README 写明。
  > ⚠ 只引用 `DuckDB.NET.Data` 不会带原生库，测试进程会卡在加载阶段，没有任何报错输出。
- 下列细节**必须对照所装版本确认后才能写死**，不得凭记忆实现（第一期的核对结果见 §0.2）：
  - `DuckDBAppender` 写入 NULL 的方法、`CreateRow / AppendValue / EndRow / Close` 的精确签名。
  - 命名参数写法（`$name` / `:name` / `?` 位置参数）以及 `List<T>` 能否作为参数值。
  - TEMP 表的可见性与是否需要 `temp.` 前缀。
  - appender 是否按列类型转换值（**决定了写入器的整体设计**）。
  - 读取端各类型返回的 CLR 类型、`GetDataTypeName` 的取值。
  - 是否存在可用的 Arrow 扩展包（若无，Arrow 路径必须走 P/Invoke，见 §9）。

若核对结果与本文档假设冲突，**以核对结果为准**，并在最终交付里列出偏差。

### 0.1 .NET 10 相关约定

- 使用 `System.Linq.Expressions` 编译映射器，编译结果按 (类型, 列结构) 缓存。
- 会话串行化用 `System.Threading.Lock`（.NET 9+，可重入）。
- 插值重载加 `[OverloadResolutionPriority(1)]`（.NET 9+），见 §5.1。
- 使用 `System.Text.Json` 处理 JSON 列；source-gen 可选。
- 不做 AOT 承诺（`Expression.Compile` 在 AOT 下受限）；若未来要 AOT，映射器需提供 `Interpreter` 回退。

### 0.2 核对结果（DuckDB.NET.Data.Full 1.5.6.1 实测）

以下每一条都有对应的回归测试（`tests/DuckFrame.Tests/CorrectnessTests.cs`）。

| 项 | 实测行为 | 对设计的影响 |
|---|---|---|
| appender API | `CreateRow()` 返回 `IDuckDBAppenderRow`；逐列 `AppendValue(...)` / `AppendNullValue()`，最后 `row.EndRow()`；没有 `Flush`，`Close()` 提交 | 写入器按此实现 |
| **appender 类型转换** | **不转换**：`int` 追加到 `DOUBLE` 列读回 `2.5E-323`；超出列小数位的 decimal **静默截断**（`1.23456` → `1.2345`）；`DateTime` 追加到 `DATE` 列却能正确转换——行为不一致，不能依赖 | 写入器必须**按目标列类型**转换每个值；decimal 小数位溢出直接报错 |
| LIST 追加 | `AppendValue<T>(IEnumerable<T>)` 支持所有标量元素（含可空） | 标量集合属性写成原生 LIST |
| STRUCT / MAP 追加 | 没有对应重载 | 第一期嵌套对象 / 字典存 JSON（§3.1） |
| LIST 的 DDL | 只能写 `INTEGER[]`；`LIST(INTEGER)` 报 *Expected a constant as type modifier* | 类型推导输出 `T[]` |
| 参数占位符 | SQL 写 `$p0`，`ParameterName` 写 `p0`（不带 `$`）；`:p0` 语法错误，`@p0` 被当列名 | 固定 `$` 前缀 |
| 列表参数 | `List<int>` / `string[]` 可直接作为参数：`= ANY($p0)`、`unnest($p0)` | 标量集合插值为 LIST 参数（§5.1） |
| TEMP 表 | 不加 `temp.` 前缀即可解析，appender 也能按表名找到 | 句柄和 appender 显式限定 catalog/schema，防止同名 TEMP 表遮蔽持久表 |
| `GetDataTypeName` | 返回 DuckDB.NET 枚举名：`Decimal`、`UnsignedTinyInt`、`List`、`Struct`、`TimestampTz`… 不带精度和元素类型 | 不能直接用于 DDL，需映射（`DuckDbTypes`） |
| DECIMAL 精度 | 只能从 `GetSchemaTable()` 的 `NumericPrecision` / `NumericScale` 取得 | 结果再注册时据此重建 `DECIMAL(p,s)` |
| 读取端 CLR 类型 | DATE→`DateOnly`，TIME→`TimeOnly`，TIMESTAMPTZ→`DateTime`（Kind=Unspecified，值为 UTC），HUGEINT→`BigInteger`，LIST→`List<T>`，STRUCT/MAP→`Dictionary<…>`，JSON→`string` | 读取转换按此实现 |
| BLOB 读取 | 返回指向原生结果内存的 `UnmanagedMemoryStream` | **必须在 reader 释放前拷成 `byte[]`**，否则是释放后使用 |
| `SUM(INTEGER)` | 返回 HUGEINT，读回 `BigInteger`（不实现 `IConvertible`） | 读取转换需单独处理 `BigInteger` |
| 事务 | `CREATE OR REPLACE` + appender 写入可整体回滚；嵌套 `BeginTransaction` 抛 *Already in a transaction* | 覆盖/刷新使用暂存表 + 单条原子 CTAS 发布，避免嵌套事务；只清理本次创建的表 |
| C# 重载解析 | 插值字符串同时匹配 `Query(FormattableString)` 与 `Query(string, object?)` 时**优先选 string 重载**，值被 `ToString()` 拼进 SQL | 插值重载加 `[OverloadResolutionPriority(1)]`，否则参数化与防注入全部失效 |

---

## 1. 目标与非目标

### 1.1 目标（必须做到）

1. 一行代码把 `IList<T>` / `IEnumerable<T>` / `DataTable` / `IEnumerable<IReadOnlyDictionary<string,object?>>` 变成可被 SQL 查询的表。
2. SQL 里直接引用 .NET 变量名，不手写字符串表名，不手写 `INSERT`：

   ```csharp
   using var db = DuckSession.OpenInMemory();
   var orders = db.Register(listOfOrders);   // 表名自动取变量名 "orders"

   var result = db.Query($"""
       SELECT Region, COUNT(*) AS n, SUM(Amount) AS total
       FROM {orders}
       WHERE Amount > {100m}
       GROUP BY Region
       ORDER BY total DESC
       """);
   ```

3. 查询结果零样板回到 .NET 类型：`To<T>()`、`ToDataTable()`、`ToDictionaries()`、`Print()`。
4. 支持常见 CLR 类型（`Guid`、`DateTime`、`decimal`、`byte[]`、枚举、`DateOnly`、`TimeOnly`、`TimeSpan`、嵌套 POCO → `STRUCT`、集合 → `LIST`），往返不丢精度、不丢 null。
5. 明确的**快照语义**与刷新策略，不让用户猜“改 list 之后表变没变”。

### 1.2 非目标（第一期不做，但要留接口）

- 不做 ORM、变更跟踪、迁移、事务编排。
- 不做 LINQ Provider（Phase 4 可选）。
- 不管理持久化表（`CREATE TABLE ... AS` 交给用户 SQL）。
- 不保证比 Python `duckdb` 快，但必须达到 §10 门槛。

---

## 2. 公开 API 表面

```csharp
namespace DuckFrame;

public sealed class DuckSession : IDisposable, IAsyncDisposable
{
    public static DuckSession OpenInMemory(DuckSessionOptions? options = null);
    public static DuckSession Open(string path, DuckSessionOptions? options = null);

    public TableRef<T> Register<T>(
        IEnumerable<T>? source,
        string? name = null,
        [CallerArgumentExpression(nameof(source))] string? expr = null,
        TableOptions? options = null);

    public TableRef RegisterTable(
        DataTable table,
        string? name = null,
        [CallerArgumentExpression(nameof(table))] string? expr = null,
        TableOptions? options = null);

    public TableRef RegisterRows(
        IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        string? name = null,
        [CallerArgumentExpression(nameof(rows))] string? expr = null,
        TableOptions? options = null);

    public TableRef GetTable(string name);
    public IReadOnlyList<TableInfo> Tables { get; }

    public DuckResult Query(FormattableString sql);
    public DuckResult Query(string sql, object? parameters = null);
    public Task<DuckResult> QueryAsync(FormattableString sql, CancellationToken ct = default);

    public int Execute(FormattableString sql);
    public T? Scalar<T>(FormattableString sql);

    public DuckDBConnection Connection { get; }
    public string? LastSql { get; }
    public void Refresh(string name);
    public void Drop(string name);
}

public abstract class TableRef : IFormattable
{
    public string Name { get; }
    public string QualifiedName { get; }   // 例："orders"（TEMP 表无需 temp. 前缀）
    public long RowCount { get; }
    public bool IsStale { get; }
    public string ToString(string? format, IFormatProvider? formatProvider); // 返回 QualifiedName
}

public sealed class TableRef<T> : TableRef { }

public sealed class DuckResult : IDisposable
{
    public IReadOnlyList<DuckColumn> Columns { get; }
    public long RowCount { get; }
    public bool IsMaterialized { get; }
    public IReadOnlyList<object?[]> Rows { get; }

    public Task<DuckResult> MaterializeAsync(CancellationToken ct = default);
    public List<T> To<T>();
    public List<T> To<T>(bool cacheRows);
    public IEnumerable<T> AsEnumerable<T>();
    public DataTable ToDataTable();
    public List<Dictionary<string, object?>> ToDictionaries();
    public string ToCsv();
    public string ToMarkdown();
    public void Print(int maxRows = 20, int maxWidth = 200);
    public override string ToString();
}

public sealed record DuckColumn(string Name, string DuckDbType, Type ClrType);
```

### 2.1 验收用例（必须全部能编译运行）

```csharp
// 例 1：IList<POCO>
var orders = new List<Order> { new() { Id = 1, Region = "EU", Amount = 12.5m } };
var df = db.Query($"select Region, sum(Amount) as total from {orders} group by 1");
df.Print();
var stats = df.To<RegionTotal>();

// 例 2：DataTable
db.Query($"select * from {dt} where Qty > 10").ToDataTable();

// 例 3：字典行
db.RegisterRows(rows);
db.Query($"select key, count(*) from {rows} group by 1");

// 例 4：结果二次查询
var filtered = db.Query($"select * from {orders} where Amount > {minAmount}");
db.Query($"select avg(Amount) from {filtered}").Scalar<decimal>();

// 例 5：与文件 join
db.Query($"select o.*, c.Name from {orders} o join read_csv('cust.csv') c on o.CustomerId = c.Id");
```

**关键要求**：例 4 中把 `DuckResult` 再插进 SQL，必须自动注册成临时表（命名 `__q1`）并生效。

---

## 3. 类型映射

### 3.1 CLR → DuckDB（写入方向）

| CLR 类型 | DuckDB 类型 | 说明 |
|---|---|---|
| `bool` | `BOOLEAN` | |
| `sbyte` / `short` / `int` / `long` | `TINYINT` / `SMALLINT` / `INTEGER` / `BIGINT` | |
| `byte` / `ushort` / `uint` / `ulong` | `UTINYINT` / `USMALLINT` / `UINTEGER` / `UBIGINT` | `byte[]` 是 `BLOB` |
| `float` / `double` | `FLOAT` / `DOUBLE` | |
| `decimal` | `DECIMAL(p,s)` | 默认 `DECIMAL(18,4)`；用 `[DuckPrecision(p,s)]`、`TableOptions.ColumnTypes` 或会话级 `DecimalPrecision/Scale` 覆盖；p 须在 1..38。**写入时小数位超出 s 直接报错**（appender 会静默截断） |
| `char` / `string` | `VARCHAR` | |
| `Guid` | `UUID` | |
| `DateTime` | `TIMESTAMP` | `options.UtcTimestamps` → `TIMESTAMPTZ` |
| `DateTimeOffset` | `TIMESTAMPTZ` | |
| `DateOnly` / `TimeOnly` | `DATE` / `TIME` | |
| `TimeSpan` | `INTERVAL` | `options.TimeSpanAsMicros` → `BIGINT`（微秒） |
| `byte[]` | `BLOB` | |
| `enum` | `VARCHAR`（名称） | `options.EnumsAsInt` → 枚举底层整数类型 |
| `T?` / `Nullable<T>` | 同 `T`，列可空 | |
| 标量元素的集合（`int[]`、`List<string>`、`IEnumerable<DateTime?>`…） | `T[]` | 原生 LIST。DDL 必须写 `INTEGER[]`，不能写 `LIST(INTEGER)` |
| 嵌套 POCO、字典、非标量元素的集合 | `JSON` | **第一期**：appender 不能写 STRUCT / MAP，用 `System.Text.Json` 序列化；可用 `col->>'$.City'` 查询，读回时反序列化。原生 STRUCT / MAP 见 §9 |
| `object` / 无法推导 | `JSON` | |
| 全 null 列 | `VARCHAR` | 或由 `TableOptions.ColumnTypes` 指定 |

**字典行的合并规则**：采样前 `SampleSize`（默认 1000）行，同列多类型时：

1. 全是整数 → `BIGINT`；
2. 整数 + `decimal` → `DECIMAL`，按采样值的最大整数位与小数位推导 `(p,s)`（至少为会话默认值，p ≤ 38）；
3. 含 `float`/`double` 的数值 → `DOUBLE`；
4. 含 `string` → `VARCHAR`；
5. 其他 → `JSON`。

合并后的列有确定的 CLR 类型，写入器把每个值转换成它（appender 不做转换，见 §0.2）。空集合必须通过 `TableOptions.ColumnTypes` 声明列，否则抛 `DuckFrameException`。

### 3.2 DuckDB → CLR（读取方向）

1. **列名匹配**：`[Column("order_id")]` → 精确 → 忽略大小写 → 忽略下划线差异（`order_id` ↔ `OrderId`）。record 的 `[property: Column(...)]` 也生效。
2. **构造选择**：`[DuckConstructor]` → 参数与公共属性一一对应的主构造（record）→ 无参构造 → 参数最多的 public 构造。**构造参数没覆盖的 public 可写属性（含 `init`）随后绑定**。
3. **快速路径**：列的 CLR 类型与成员类型一致（或成员是它的可空版本）时直接拆箱 / 强转，不走通用转换。
4. **通用转换**：`BigInteger`（`SUM(INTEGER)` 返回 HUGEINT）→ 数值类型（溢出抛异常）；`List<T>` → 数组 / 其他集合；JSON 文本、STRUCT/MAP 字典 → 对象（`JsonSerializer`）；整数 → `TimeSpan`（按微秒）；日期时间类型互转；字符串 → `Guid` / 日期 / 枚举；最后 `IConvertible`。
5. **缺列 / 多列**：`MissingColumn`（Throw / DefaultValue）、`ExtraColumn`（Throw / Ignore），默认 Throw，异常消息列出实际列名与期望成员名。
6. `NULL` → `null`；赋给非可空值类型时抛异常，消息里带列名。

---

## 4. 注册机制（核心）

### 4.1 名称解析

```
[Table("orders_v2")] > 显式 name > CallerArgumentExpression 清洗 > "tbl_1"
```

清洗规则：

- `orders` → `orders`
- `this._orders` / `_orders` → `orders`
- `dict["x"]`、`GetOrders()` → 取最后一段标识符，否则 `tbl_N`
- 保底：非 `[A-Za-z_][A-Za-z0-9_]*` 字符替换为 `_`；重名加后缀 `_2`。

### 4.2 建表

```sql
CREATE TEMP TABLE "orders" (
  "Id" INTEGER,
  "Region" VARCHAR,
  "Amount" DECIMAL(18,4),
  "CreatedAt" TIMESTAMP
);
```

- 默认 `TEMP`（会话私有）；`TableOptions.Persistent = true` 时建普通表。
- 表名、列名**永远加双引号**，内部 `"` 转义为 `""`。不做省略引号的优化。
- 同名列冲突（`A` vs `a`）→ 抛异常，要求 `[Column]` 区分。

### 4.3 写入

使用 `DuckDBAppender`（签名见 §0.2）：

```
appender = connection.CreateAppender(table)
每行：row = appender.CreateRow() → 每列 row.AppendValue(...) / row.AppendNullValue() → row.EndRow()
最后：appender.Close()
```

- **每列按目标列的 DuckDB 类型生成一个写入委托**，先把值转换成该类型对应的 CLR 类型再追加。appender 不做转换，类型不一致会静默写坏数据（§0.2）。
- LIST 列只接受元素类型与列定义一致的集合，否则报错（同样会被按原始字节写入）。
- 源 `IEnumerable` **只枚举一次**，行直接流进 appender，不额外缓存一份（100 万行注册的托管内存增量 ≈ 0）。字典行只缓存 SampleSize 行的采样前缀，剩余行沿同一个枚举器流式写入。
- 写入异常带列名、列类型、行号与值的 CLR 类型。
- 失败时仅删除本次成功创建的新表或暂存表，不删除已有表或旧登记；写到一半的 appender 在释放时会再抛错，要吞掉以保留真正的原因。

### 4.4 刷新策略

```csharp
public enum RefreshPolicy
{
    Immutable,   // 注册即快照，永不自动检查（默认）
    Manual,      // 仅 Refresh() 重载
    OnQuery,     // 未实现：选择该值时抛 NotSupportedException
}
```

- 默认 `Immutable`。文档用一句话讲清：**`Register` 注册的是快照，不是视图**。
- `Refresh(name)` 重新枚举源对象到独立暂存表，成功后以单条 `CREATE OR REPLACE ... AS SELECT` 原子发布，失败保留原数据；不启动嵌套事务。
- 直接插进 SQL 的集合（§5.1）每次查询都会重新载入，语义接近 Python 的 replacement scan。
- 提供 `RegisterLive(IObservableCollection<T>)`（Phase 2）走增量刷新。

---

## 5. SQL 插值解析器

### 5.1 规则

`Query(FormattableString)` 自行组装 SQL，**不走** `string.Format`：

| 插值参数类型 | 处理 | 输出示例 |
|---|---|---|
| `TableRef` | 加引号的表名（TEMP 表无需 `temp.` 前缀） | `"orders"` |
| `DuckResult` | 自动注册为 `__q1`；同一个结果多次插值只注册一次 | `"__q1"` |
| 对象集合（`IEnumerable<POCO>`、字典行）/ `DataTable` | 自动注册为 `__p1`；**同一个对象再次插值时沿用同一张表并重新载入**，既读到最新内容，又不会在循环里堆积临时表 | `"__p1"` |
| 标量集合（`int[]`、`List<string>`…） | **LIST 参数**，不注册成表 | `$p0`，可写 `= ANY({ids})`、`IN (SELECT unnest({ids}))` |
| `string` / `char` / `byte[]` | **不**当标识符，参数绑定 | `$p0` |
| 数值 / `bool` / 日期时间 / `Guid` / 枚举 / `null` | 参数绑定 | `$p0` |
| `Sql.Ident("weird name")` | 显式标识符 | `"weird name"` |
| `Sql.Raw("...")` | 显式原文（`[Obsolete]` 标记） | 原文 |

**安全底线**：`string` 永远不会被拼成标识符或表。`Sql.Ident` 是唯一入口，内部转义引号。

**重载解析陷阱**：C# 遇到插值字符串时，若同时存在 `Query(string, …)` 重载，会**优先选 string 版本**——值被 `ToString()` 直接拼进 SQL，参数化与防注入全部失效，而且编译器不会有任何提示。插值重载必须加 `[OverloadResolutionPriority(1)]`，并用“字符串参数含 `'; DROP TABLE` 不生效”的测试锁住。

### 5.2 参数绑定

- 使用 `DuckDBParameter`，SQL 占位符写 `$p0`、`$p1`…，`ParameterName` 写 `p0`（不带 `$`，见 §0.2）。
- 绑定失败异常包含：原始 SQL、参数名列表、参数 CLR 类型。
- `DuckSessionOptions.LogSql` 回调输出最终 SQL（默认关闭）。

### 5.3 标识符规范化

- 建表与引用共用同一个 `NameResolver` 输出。
- 大小写不敏感行为必须有测试覆盖，不假设。

---

## 6. 结果物化

- `DuckResult` 持有 `DuckDBDataReader`；`Materialize()` 后读入 `object?[]`。未物化访问 `Rows` 抛提示异常。
- `AsEnumerable<T>()` / `To<T>(cacheRows: false)` 使用缓存的类型化 reader 映射器直接构造对象，不缓存原始行；提前结束也会关闭 reader。
- `Print` / `ToString` 仅读取预览行及一行前瞻，保留前缀供后续读取；未到末尾时显示 `at least`，不声称已知总数。
- 预先取消物化不推进 reader；中途取消或失败会关闭 reader，拒绝后续把剩余行作为完整结果返回。
- `ToDataTable` 用原始值类型 + AllowDBNull，BLOB 为 byte[]；ExtendedProperties 保留 DuckDB 精度以便再注册。
- LIST 以可空元素读取，嵌套 BLOB 也在 reader 释放前复制；重复列名拒绝字典/对象映射。
- `To<T>()` 映射器用 `System.Linq.Expressions` 生成并缓存：
  `ConcurrentDictionary<(Type T, string SchemaKey), Func<IDataRecord, T>>`。
- `Print()` 输出对齐表格：

```
┌─────────┬────────┬───────────────┐
│ Region  │ N      │ Total         │
│ varchar │ bigint │ decimal(38,4) │
├─────────┼────────┼───────────────┤
│ US      │ 2      │ 290           │
│ EU      │ 1      │ 80            │
└─────────┴────────┴───────────────┘
2 row(s) (2 shown)
```

第二行是 DuckDB 类型（已映射成 DDL 写法，带精度）。按 `maxWidth` 均分列宽并用 `…` 截断；`null` 显示 `NULL`；LIST / STRUCT 显示为 `[1, 2]` / `{a: 1}`；CJK 字符按 2 个宽度对齐。

- 物化时 BLOB 必须当场拷成 `byte[]`（§0.2）。
- 默认 `To<T>()` 保持逐行缓存和重复读取语义；直接映射路径避免这份缓存。未来列式缓存优化独立评估。

---

## 7. 会话与连接

- `DuckSession` 持有一个 `DuckDBConnection`，所有公开方法用 `System.Threading.Lock` **串行化**（可重入：插值注册发生在查询的锁内）。
- `DuckSessionOptions`：

  ```
  SampleSize, RefreshPolicy, DecimalPrecision, DecimalScale, EnumsAsInt,
  UtcTimestamps, TimeSpanAsMicros, MissingColumn, ExtraColumn, LogSql
  ```

- `TableOptions`：`Persistent`、`Overwrite`（同名时替换而不是追加 `_2`）、`RefreshPolicy`、`ColumnTypes`（列名 → DuckDB 类型）。
- `QueryAsync`：DuckDB 内核同步，调用移到线程池；取消通过 command.Cancel 中断正在执行的查询，取消回调在释放会话锁前解除。
- 结果读取/释放与会话共享锁，流式枚举不跨 yield 持锁；同一结果同时只允许一个消费者。会话释放关闭仍活动的 reader；已物化结果可独立保留。
- 自动注册表只弱引用源对象，在后续查询、注册或访问 Tables 时回收失去源对象的表；显式注册为 Refresh 保留源，Drop/Dispose 释放引用。
- 底层 Connection 是逃生舱：调用方负责同步，外部 SQL/事务回滚对登记信息的影响不自动跟踪。
- 多连接：需要并发读时可共享数据库文件，但 **TEMP 表不跨连接**，共享模式必须建普通表。默认单连接串行。

---

## 8. 错误与诊断

- 统一异常 `DuckFrameException`，`InnerException` 保留底层异常。
- 消息包含：操作、表名、列名、列类型、行号（写入时）、实际列名列表（映射时）。
- `DuckSession.LastSql` 供调试。
- 注册失败不得留下半张新表；只有本次 CREATE 成功后才可清理对应对象，旧表和旧登记在发布成功前保持不变。

---

## 9. 进阶路线

### Phase 2 — 原生 STRUCT / MAP 与表函数

- **STRUCT / MAP 写入**：appender 没有对应重载（§0.2）。两条路：先以 JSON 写入，再 `ALTER TABLE … ALTER COLUMN c TYPE STRUCT(…) USING c::JSON::STRUCT(…)`；或用 DuckDB.NET 的 `DataChunk` 写入器直接填向量。
- **表函数（零落表）**：DuckDB.NET 1.5.6.1 已提供托管 API `DuckDBConnection.RegisterTableFunction(name, () => new TableFunction(columns, data, cardinality), (item, writers, row) => …)`，支持投影下推（不读的列不计算）。用它实现 `SELECT * FROM obs(myList)`，**不需要自己写 P/Invoke**。
  - 收益：避免全量拷贝，适合 > 100 万行或反复查询同一集合。
  - 必须有开关回退到建表路径；需确认回调的线程模型。
- `DuckDBMappedAppender<T, TMap>`（类映射式 appender）可作为 POCO 写入的备选实现，届时与当前写入器对比性能。

### Phase 3 — Arrow 零拷贝

`DuckDB.NET.Data` 已依赖 `Apache.Arrow`。用于“`DuckResult` 再喂回去”和“大 `List<T>` 写入”两条路径；先确认 DuckDB.NET 是否暴露 Arrow 查询 / 扫描接口，没有再考虑 P/Invoke（需自管 `ArrowArray` / `ArrowSchema` 的 release 回调）。

### Phase 4 — LINQ Provider（可选）

`db.From(orders).Where(o => o.Amount > 100)` → SQL。表达式树翻译 + 参数化，复用 §3 与 §5。

---

## 10. 性能门槛

以相同机器上的原生 DuckDB.NET 类型化 reader 为基线。`MaterializationBenchmarks` 实际返回 100,000 行 × 8 列，建库/注册放在 GlobalSetup，分别测量 NativeReader、CachedRows、WithoutRowCache 的时间和托管分配。`RegistrationBenchmarks` 单独测量注册。

- 大结果优先使用类型化直接映射 `To<T>(cacheRows: false)`，目标时间 ≤ 原生 reader 的 1.5 倍。
- 默认 `To<T>()` 保留原始行以支持多次转换，不与单次消费路径混淆。
- POCO 属性访问器按成员缓存；同条 SQL 内同源引用去重；字典行只缓冲采样前缀。
- 旧实现的 Stopwatch 数字已移除，当前性能以 BenchmarkDotNet 报告为准；单次 Dry 运行只验证基准可执行，不作为稳定性能结论。

---

当前短基准记录见 [2026-11-01 报告](benchmarks-2026-11-01.md)。

## 11. 测试计划

- **类型往返**：§3.1 每种类型写入 → 查询 → `To<T>()`，`Assert.Equal` 原值（decimal 精度、DateTime 微秒、Guid、byte[]、TimeSpan、枚举）。
- **Null 矩阵**：可空值类型全空 / 混合空 / 空串 vs null；NULL 赋给非可空成员时报错带列名。
- **命名**：保留字列名（`select`、`order`、`group`）、含空格/引号/中文、大小写差异、snake_case ↔ PascalCase。
- **集合与嵌套**：标量集合 ↔ 原生 LIST；嵌套 POCO / 字典 ↔ JSON（可 `->>` 查询并读回）；`List<POCO>`。
- **数据源形态**：`List<T>`、`T[]`、惰性 `IEnumerable<T>`（验证只枚举一次）、`DataTable`（含 `DBNull`、`byte[]` 列）、`IEnumerable<Dictionary<string,T>>`（经 `Register` 和插值两条路）、`DuckResult` 二次查询（精度不丢）。
- **appender 行为（§0.2）**：混合数值列不被写坏；`EnumsAsInt` / `TimeSpanAsMicros` 往返；decimal 小数位溢出报错且不留表；BLOB 在 reader 释放后仍可读。
- **映射**：无参构造 + 可写属性的 class；record + 额外 `init` 属性；`SUM(INTEGER)` 的 HUGEINT 读成 `long`。
- **刷新语义**：`Register` 后改源不生效；`Refresh()` 后生效；插值的集合每次查询读到最新内容且不堆积临时表；同一 `DuckResult` 插值多次只注册一次。
- **安全**：字符串参数含 `'; DROP TABLE --` 不导致注入（同时验证选中的是插值重载）；`Sql.Ident` 对 `a"b` 正确转义。
- **异常**：映射缺列/多列、空集合无列定义、重复表名、写入中途失败不留残表。
- **并发**：8 线程并发 `Query` 同一 session，不崩、结果正确（已由 BoundaryTests 覆盖）。
- **生命周期**：`Dispose` 后抛 `ObjectDisposedException`；`DuckResult` 未物化 Dispose 不泄漏 reader。

---

## 12. 工程结构

```
src/DuckFrame/
  DuckSession.cs                 注册 / 插值 / 查询 / 刷新
  DuckSessionOptions.cs, TableOptions.cs, TableRef.cs, TableInfo.cs, DuckColumn.cs
  DuckFrameException.cs, Attributes.cs
  Sql/SqlFormatter.cs            手写插值解析（不走 string.Format）
  Sql/SqlString.cs               Sql.Ident / Sql.Raw
  Tables/AppenderWriter.cs       按目标列类型转换后写入
  Tables/RowSchemaInference.cs   字典行的列推导与合并
  Tables/DictionaryRows.cs       识别各种“字符串键字典”元素
  Tables/RowAdapters.cs, TableRegistration.cs
  Types/ClrToDuckDb.cs           CLR → DDL 类型
  Types/DuckDbTypes.cs           reader 类型名 → DDL、类型族归一
  Types/DuckDbToClr.cs           读取方向转换
  Types/TypeInspector.cs         POCO 列推导
  Results/DuckResult.cs, Materializer.cs, TablePrinter.cs
  Internal/NameResolver.cs, NameMatcher.cs, Guard.cs, ColumnDefinition.cs
tests/DuckFrame.Tests/           CorrectnessTests.cs 锁住 §0.2 的每一条
benchmarks/DuckFrame.Benchmarks/
samples/DuckFrame.Sample/        §2.1 的五个示例
```

- 公共类型 `sealed` 或 `abstract`，不做可继承设计。
- 启用 `nullable`、`ImplicitUsings`、`LangVersion=latest`；M4 全绿后打开 `TreatWarningsAsErrors`。
- 每个公开成员写一句 XML 文档注释。
- README 含：最小示例、与 Python duckdb 的对照表、快照语义说明、已核对的行为、已知限制。

---

## 13. 与 Python 的对照

| Python (duckdb / pandas) | DuckFrame |
|---|---|
| `duckdb.sql("select * from df")` | `db.Query($"select * from {orders}")` |
| `df` 变量名自动可见 | 插值即注册；`Register` 用 `CallerArgumentExpression` 取变量名 |
| replacement scan 每次读最新的 df | 插值的集合每次查询重新载入 |
| `conn.sql(...).df()` | `.ToDataTable()` / `.To<T>()` / `.ToDictionaries()` |
| `print(df)` / `df.head()` | `.Print()` / `.Print(maxRows: 5)` |
| `duckdb.from_df(df)` | `db.Register(df)`（快照） |
| `rel.sql(...)` 惰性 | 第一期直接执行；结果可再插进 SQL |

---

## 14. 里程碑

| 里程碑 | 内容 | 验收 | 状态 |
|---|---|---|---|
| M1 | 会话 + `IList<T>` 注册 + `Query` + `Print` | §2.1 例 1 | ✅ |
| M2 | 类型映射 + `To<T>()` 映射器 + null 矩阵 | §11 类型往返 | ✅（STRUCT/MAP 第一期走 JSON） |
| M3 | `DataTable` / 字典行 / 结果二次查询 | §2.1 例 2–5 | ✅ |
| M4 | 刷新保护 + 错误诊断 + 基准 + 直接物化 | §10 相对性能 | 已补直接映射、并发和边界测试；列式缓存独立留待后续 |
| M5 | Phase 2：原生 STRUCT/MAP、表函数 | 大集合路径可开关 | 未开始 |

---

## 15. 给实现者的硬约束

1. 不要用字符串拼接构造 SQL 的**数据**部分；任何值都走参数。插值重载必须压过 string 重载（§5.1）。
2. 不要对标识符做“能省引号就省”的优化。
3. 不要在一次 `Register` 里枚举源集合两次。
4. 不要在 `To<T>()` 里逐行反射；用缓存的表达式树。
5. 不要静默重载表；刷新必须由策略或显式调用驱动。
6. 不要把值原样交给 appender；先按目标列类型转换（§4.3）。
7. 任何关于 DuckDB.NET 具体 API 的断言，先实测 / 编译验证，再写进代码与文档；新发现写进 §0.2 并配回归测试。
8. 每个公开 API 都要有对应测试；每个测试都能在 `:memory:` 上独立跑完。

## 16. 本轮边界修复

- 基线提交：`45a2bbd`。新增 `BoundaryTests` 覆盖表保护、调用方事务、LIST decimal 精度、可空元素、字典采样/大小写、构造默认值、取消、预览、GC 与并发。
- LIST 类型元数据使用已锁定版本的公开 C API；预备语句和逻辑类型均通过 provider SafeHandle 释放，不读取 provider 私有字段。升级依赖时必须重跑空 LIST、参数 LIST、decimal 精度和二次查询测试。
- 类型化直接映射与缓存行映射共用构造/成员绑定规则；日期、NULL、BLOB、LIST 和默认值都需验证两种模式。
