# 2026-11-01 性能基准

本机 BenchmarkDotNet 0.14.0 ShortRun，同进程执行，3 次预热和 3 次测量。结果用于本轮实现对比；样本较少，不能当作跨机器性能保证。进程未能设置高优先级，保留默认优先级执行。

```bash
dotnet run -c Release --project benchmarks/DuckFrame.Benchmarks -- --filter '*MaterializationBenchmarks*' --job short --inProcess
```

每次查询实际返回 100,000 行 × 8 列。建库和注册放在 GlobalSetup，三个方法都生成同一类型的对象列表。分配量仅包含托管分配，不含 DuckDB 原生内存。

| 路径 | 平均时间 | 相对原生 reader | 托管分配 |
|---|---:|---:|---:|
| 原生类型化 reader | 112.6 ms | 1.00 | 17.27 MB |
| `To<T>()` 行缓存 | 359.4 ms | 3.19 | 39.43 MB |
| `To<T>(cacheRows: false)` 直接映射 | 126.6 ms | 1.13 | 17.30 MB |

直接映射的分配比行缓存减少约 56%，同时保留默认 API 的可重复读取语义。这是两种当前读取模式的比较，不是旧版本与新版本的完整对照。

原始报告：

```

BenchmarkDotNet v0.14.0, Ubuntu 26.04.1 LTS (Resolute Raccoon)
Hygon C86-3G 5380 16-core Processor, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.114
  [Host] : .NET 10.0.14 (10.0.1426.52326), X64 RyuJIT AVX2

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3
LaunchCount=1  WarmupCount=3

```
| Method          | RowCount | Mean     | Error    | StdDev  | Ratio | RatioSD | Gen0      | Gen1      | Gen2      | Allocated | Alloc Ratio |
|---------------- |--------- |---------:|---------:|--------:|------:|--------:|----------:|----------:|----------:|----------:|------------:|
| NativeReader    | 100000   | 112.6 ms | 59.38 ms | 3.25 ms |  1.00 |    0.04 | 3000.0000 | 1666.6667 |  333.3333 |  17.27 MB |        1.00 |
| CachedRows      | 100000   | 359.4 ms | 48.41 ms | 2.65 ms |  3.19 |    0.08 | 7000.0000 | 4000.0000 | 1000.0000 |  39.43 MB |        2.28 |
| WithoutRowCache | 100000   | 126.6 ms | 60.85 ms | 3.34 ms |  1.13 |    0.04 | 3000.0000 | 1750.0000 |  500.0000 |   17.3 MB |        1.00 |

## 注册基准

相同环境、相同 ShortRun 配置，使用 `--filter '*RegistrationBenchmarks*'` 单独测量。10 万行 × 8 列注册平均 207.5 ms，托管累计分配 29.8 MB；累计分配不等于注册完成后的存活内存。

```

BenchmarkDotNet v0.14.0, Ubuntu 26.04.1 LTS (Resolute Raccoon)
Hygon C86-3G 5380 16-core Processor, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.114
  [Host] : .NET 10.0.14 (10.0.1426.52326), X64 RyuJIT AVX2

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3
LaunchCount=1  WarmupCount=3

```
| Method   | RowCount | Mean     | Error    | StdDev  | Gen0      | Allocated |
|--------- |--------- |---------:|---------:|--------:|----------:|----------:|
| Register | 100000   | 207.5 ms | 75.52 ms | 4.14 ms | 7333.3333 |   29.8 MB |
