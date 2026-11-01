#!/usr/bin/env python3
"""Render the fresh-process analytics suite JSON into auditable tables and an optional plot."""
import argparse
import csv
import json
import math
import shutil
import statistics
from collections import defaultdict
from pathlib import Path

CASES = [
    ('FilterSummary', '过滤 + 综合汇总', '1', 'Paid、数量≥3、2026-07-01 起；COUNT/SUM/MIN/MAX'),
    ('MonthlyGroups', '按渠道、月份分组', '48', 'Paid；按渠道和月份计算行数、数量、金额'),
    ('JoinCustomers', '100万 × 10万连接', '1,000,000', '订单连接客户，返回全部 100 万条对象'),
    ('JoinMillionPayments', '100万 × 100万连接', '1,000,000', '订单连接支付，一对一，返回全部 100 万条对象'),
    ('JoinGroups', '两次连接 + 分组汇总', '800', '订单连接客户、商品；Paid 且数量≥2；按地区、品类汇总'),
    ('Top20', '连接、汇总后取 Top 20', '20', '同上一场景，按金额降序及地区、品类排序后取前 20'),
]
VARIANTS = [('Linq', 1), ('DuckFrame', 1), ('NativeDuckDb', 1), ('DuckFrame', 0), ('NativeDuckDb', 0)]


def percentile(values, fraction):
    items = sorted(values)
    position = (len(items) - 1) * fraction
    lo, hi = math.floor(position), math.ceil(position)
    return items[lo] + (items[hi] - items[lo]) * (position - lo)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('results', type=Path)
    parser.add_argument('report', type=Path)
    parser.add_argument('--plot', action='store_true')
    args = parser.parse_args()
    reports = json.loads((args.results / 'results.json').read_text())
    environment = json.loads((args.results / 'environment.json').read_text())
    settings = environment['Settings']
    grouped = defaultdict(list)
    for report in reports:
        if not report['Correct']:
            raise ValueError('An unchecked result cannot be reported.')
        grouped[(report['Analysis'], report['Backend'], report['RequestedThreads'])].append(report)
    stats = {}
    for case, label, _, description in CASES:
        for backend, threads in VARIANTS:
            runs = grouped[(case, backend, threads)]
            if len(runs) != settings['Repeats']:
                raise ValueError(f'Incomplete case: {case}/{backend}/{threads}')
            if len({r['ResultRows'] for r in runs}) != 1:
                raise ValueError('Output cardinality changed between processes.')
            warm = [s['Milliseconds'] for r in runs for s in r['WarmQueries']]
            cold = [r['FirstQuery']['Milliseconds'] for r in runs]
            first_total = [r['Initialize']['Milliseconds'] + r['FirstQuery']['Milliseconds'] for r in runs]
            stats[(case, backend, threads)] = dict(
                case=case, label=label, backend=backend, threads=threads,
                effective_threads=runs[0]['EffectiveThreads'], output_rows=runs[0]['ResultRows'],
                setup_ms=statistics.median(r['Initialize']['Milliseconds'] for r in runs),
                cold_ms=statistics.median(cold), cold_min=min(cold), cold_max=max(cold),
                first_total_ms=statistics.median(first_total), first_total_min=min(first_total), first_total_max=max(first_total),
                warm_ms=statistics.median(warm), warm_p95=percentile(warm, .95), warm_min=min(warm), warm_max=max(warm),
                alloc_mb=statistics.median(s['AllocatedBytes'] for r in runs for s in r['WarmQueries']) / 1024**2,
                peak_mb=statistics.median(r['PeakWorkingSetBytes'] for r in runs) / 1024**2,
                warm_samples=len(warm), warmup_min=min(r['WarmupCount'] for r in runs), warmup_max=max(r['WarmupCount'] for r in runs),
            )
    args.report.parent.mkdir(parents=True, exist_ok=True)
    evidence = args.report.parent / args.report.stem
    evidence.mkdir(exist_ok=True)
    for name in ['environment.json', 'results.json', 'samples.csv']:
        shutil.copyfile(args.results / name, evidence / name)
    with (evidence / 'summary.csv').open('w', newline='') as out:
        writer = csv.DictWriter(out, fieldnames=list(next(iter(stats.values()))), lineterminator='\n')
        writer.writeheader()
        writer.writerows(stats.values())
    auto_threads = sorted({r['EffectiveThreads'] for r in reports if r['RequestedThreads'] == 0})
    auto_label = '/'.join(map(str, auto_threads))
    repeat_count, sample_count = settings['Repeats'], settings['Samples']
    lines = [
        '# LINQ、DuckFrame 与原生 DuckDB：百万行分析基准', '',
        '本报告面向“一次载入、持续使用复杂 SQL 分析”的场景。主要关注数据已注册后的查询速度、SQL 能完成的工作和最终结果物化成本；首次载入数据作为使用背景保留。', '',
        '推荐把过滤、连接、分组和排名留在 SQL 中完成，最后再转换为 .NET 对象。CTE/窗口函数等 SQL 表达能力是这一使用方式的优势；本轮性能实测覆盖下表六种查询，不将未测试的 SQL 算子计入性能结论。', '',
        f"本轮使用 {settings['Rows']:,} 条订单，{repeat_count} 个独立进程重复；每个进程预热后测量 {sample_count} 次。所有首次、预热和正式查询均通过独立逐行 oracle 的值与行数校验。", '',
        '## 方法与范围', '',
        '- 数据库依赖为 DuckDB.NET.Data.Full 1.5.6.1；被测 DuckFrame 核心代码基于提交 ca9c9d9，本轮仅增加基准与报告。',
        '- 输入在计时前生成：订单 8 个字段、10 万客户、1,000 种商品；支付连接场景另外生成 100 万条支付。随机种子为 20261101，金额使用 decimal。',
        '- **首次查询**：每种场景、每个引擎/线程配置均启动全新 .NET 进程。先记录建库和载入所需表的成本，再记录第一次分析查询。首次合计为二者相加；不包含进程启动和公共输入生成。操作系统文件缓存未清空。',
        '- **热查询**：同一进程复用已载入的表，至少预热 5 次且查询累计至少 1 秒（上限 1,000 次），再连续测量。轮间不强制 GC，查询引起的正常 GC 计入耗时。',
        '- LINQ 使用单线程 Enumerable.Where/Join/GroupBy/Aggregate/OrderBy/Take/ToList。分组只计算 SQL 中同样的聚合项；连接的查找表每次查询重新创建。',
        '- DuckFrame 使用显式 Register，一次载入后重复查询，并以 To<T>(cacheRows: false) 物化。原生 DuckDB.NET 使用同样的 SQL/列类型，手写类型化 appender 和 reader。',
        '- 六种查询都完整消费结果。两种大连接均真正生成 100 万条同类型 DTO；汇总返回 1/48/800 行，Top 20 返回 20 行。连接不要求输出顺序；分组/排名的排序语义在 LINQ 和 SQL 间一致。',
        '- 校验在计时外进行：连接逐条核对字段并检查主键唯一、无遗漏；汇总核对每个分组的精确 decimal 值、数量和行数。数据生成使用 for 循环，oracle 在第一次计时查询后才构建，防止预热被测查询。',
        f"- 按轮旋转执行顺序；全部 {len(reports)} 个工作进程串行运行。环境为 {environment['OS']}、{environment['Framework']}、{environment['Architecture']}，逻辑 CPU {environment['ProcessorCount']}，Server GC={environment['ServerGc']}；DuckDB 默认线程数实测为 {auto_threads}。", '',
        f"耗时单位为毫秒，表内为中位数：首次取 {repeat_count} 次，热查询取 {repeat_count * sample_count} 次。首次合计先在每个进程内相加再取中位数，可能不等于两列中位数的和。", '',
        '## 场景', '',
        '| 场景 | 输出行数 | 查询内容 |', '|---|---:|---|',
    ]
    for case, label, _, description in CASES:
        count = stats[(case, 'Linq', 1)]['output_rows']
        lines.append(f'| {label} | {count:,} | {description} |')
    lines += ['', '## 首次使用：单线程对比', '',
              '| 场景 | LINQ 首次 | DuckFrame 建库/载入 | DuckFrame 首次查询 | DuckFrame 首次合计 | 原生 DuckDB 首次合计 |',
              '|---|---:|---:|---:|---:|---:|']
    for case, label, _, _ in CASES:
        l, d, n = [stats[(case, b, 1)] for b in ['Linq', 'DuckFrame', 'NativeDuckDb']]
        lines.append(f"| {label} | {l['cold_ms']:.1f} | {d['setup_ms']:.1f} | {d['cold_ms']:.1f} | {d['first_total_ms']:.1f} | {n['first_total_ms']:.1f} |")
    lines += ['', '## 预热后：相同数据重复查询', '',
              f'| 场景 | LINQ | DuckFrame 1线程 | 原生 DB 1线程 | DuckFrame 默认{auto_label}线程 | 原生 DB 默认{auto_label}线程 | LINQ / DuckFrame单线程 |',
              '|---|---:|---:|---:|---:|---:|---:|']
    for case, label, _, _ in CASES:
        l, d, n, da, na = [stats[(case, b, t)] for b, t in VARIANTS]
        lines.append(f"| {label} | {l['warm_ms']:.1f} | {d['warm_ms']:.1f} | {n['warm_ms']:.1f} | {da['warm_ms']:.1f} | {na['warm_ms']:.1f} | {l['warm_ms']/d['warm_ms']:.2f}× |")
    lines += ['', '比值大于 1 表示 DuckFrame 更快，小于 1 表示 LINQ 更快。单线程列用于同等线程配置比较；默认线程列展示此机器上 DuckDB 的并行收益及调度成本。', '',
              '## 每次热查询的托管分配', '',
              '单位为 MiB（1024² 字节）。不包含 DuckDB 原生内存、输入对象或载入表成本，不能据此推断总内存占用。', '',
              '| 场景 | LINQ | DuckFrame 1线程 | 原生 DB 1线程 |', '|---|---:|---:|---:|']
    for case, label, _, _ in CASES:
        l, d, n = [stats[(case, b, 1)] for b in ['Linq', 'DuckFrame', 'NativeDuckDb']]
        def memory_text(value):
            return '<0.001' if 0 < value < .001 else f'{value:.3f}'
        lines.append(f"| {label} | {memory_text(l['alloc_mb'])} | {memory_text(d['alloc_mb'])} | {memory_text(n['alloc_mb'])} |")
    lines += ['', '## 重复多少次可摊薄载入成本', '',
              '按本次中位数估算累计成本：首次合计 + (次数−1) × 热查询。适用于同一份注册快照、连续执行同类查询；每次修改源数据并重新注册时不能套用。', '',
              '| 场景 | DuckFrame 1线程 | DuckFrame 默认线程 |', '|---|---:|---:|']
    def breakeven(l, d):
        gap = d['first_total_ms'] - l['cold_ms']
        savings = l['warm_ms'] - d['warm_ms']
        if gap <= 0:
            return '1 次'
        if savings <= 0:
            return '本轮无交点'
        return f'{1 + math.ceil(gap/savings)} 次'
    for case, label, _, _ in CASES:
        l = stats[(case, 'Linq', 1)]
        lines.append(f"| {label} | {breakeven(l, stats[(case, 'DuckFrame', 1)])} | {breakeven(l, stats[(case, 'DuckFrame', 0)])} |")
    if args.plot:
        plot(stats, args.report.with_suffix('.png'))
        lines += ['', f'![首次与热查询对比]({args.report.with_suffix(".png").name})']
    lines += ['', '## 波动与完整明细', '',
              'P95 由 27 次热样本（默认配置）计算，样本较少；冷启动只有 3 次独立样本。以下范围用于展示本机波动，不是跨机器保证。进程峰值包含输入、原生数据库、结果、校验和运行时。', '',
              '| 场景 | 引擎 | 线程 | 首次查询 中位/最小/最大 ms | 热查询 中位/P95 ms | 进程峰值 MiB |', '|---|---|---:|---:|---:|---:|']
    for case, label, _, _ in CASES:
        for b, t in VARIANTS:
            s = stats[(case, b, t)]
            thread_label = str(s['effective_threads']) + (' (默认)' if t == 0 else '')
            lines.append(f"| {label} | {b} | {thread_label} | {s['cold_ms']:.1f}/{s['cold_min']:.1f}/{s['cold_max']:.1f} | {s['warm_ms']:.1f}/{s['warm_p95']:.1f} | {s['peak_mb']:.1f} |")
    lines += ['', '## 复现与原始数据', '', '```bash',
              'dotnet restore DuckFrame.slnx',
              'dotnet build benchmarks/DuckFrame.Benchmarks -c Release --no-restore',
              'dotnet run -c Release --no-build --no-restore --project benchmarks/DuckFrame.Benchmarks -- \\',
              '  --analytics --rows 1000000 --repeats 3 --samples 9 --warmups 5 --warmup-ms 1000 \\',
              '  --output artifacts/analytics-1m',
              'python3 benchmarks/DuckFrame.Benchmarks/Analytics/report.py \\',
              '  artifacts/analytics-1m docs/analytics-1m.md', '```', '',
              '查询及独立 oracle 位于 `benchmarks/DuckFrame.Benchmarks/Analytics/AnalyticsData.cs`；数据库载入和读取位于 `AnalyticsEngine.cs`；进程隔离、计时和采样位于 `AnalyticsSuite.cs`。计时使用 Stopwatch 的单调时钟，分配使用 GC.GetTotalAllocatedBytes(true)，完整结果可审计。绘图选项需要 matplotlib。', '',
              f'- [汇总 CSV]({evidence.name}/summary.csv)',
              f'- [逐次计时 CSV]({evidence.name}/samples.csv)',
              f'- [独立进程结果 JSON]({evidence.name}/results.json)',
              f'- [环境与参数]({evidence.name}/environment.json)', '',
              '本轮数据对应上述数据分布、投影列和标准 LINQ 操作符实现；连接输出规模、可复用索引、金额类型和数据是否已在数据库中都会影响选择。',
              '']
    args.report.write_text('\n'.join(lines))
    print(args.report)
    for case, label, _, _ in CASES:
        l, d, da = stats[(case, 'Linq', 1)], stats[(case, 'DuckFrame', 1)], stats[(case, 'DuckFrame', 0)]
        print(f"{label}: LINQ {l['warm_ms']:.1f}ms; DF1 {d['warm_ms']:.1f}ms ({l['warm_ms']/d['warm_ms']:.2f}x); DFauto {da['warm_ms']:.1f}ms")


def plot(stats, path):
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    import numpy as np
    labels = ['Filter + aggregates', 'Monthly GROUP BY', '1M x 100K JOIN', '1M x 1M JOIN', '2 JOINs + GROUP BY', 'Analytics TOP 20']
    variants = [('Linq', 1, 'LINQ', '#e69f00'), ('DuckFrame', 1, 'DuckFrame: 1 thread', '#0072b2'),
                ('DuckFrame', 0, 'DuckFrame: default threads', '#009e73')]
    fig, axes = plt.subplots(2, 1, figsize=(14, 9), constrained_layout=True)
    x = np.arange(len(CASES))
    for ax, key, title in zip(axes, ['first_total_ms', 'warm_ms'], ['First use: database setup/import + first query', 'Warm queries: registered data reused']):
        for i, (backend, threads, label, color) in enumerate(variants):
            values = [stats[(c, backend, threads)][key] for c, *_ in CASES]
            bars = ax.bar(x + (i - 1) * .26, values, .25, label=label, color=color)
            ax.bar_label(bars, labels=[f'{v:,.0f}' if v >= 100 else f'{v:.1f}' for v in values], fontsize=8, padding=3)
        ax.set_yscale('log')
        ax.set_ylim(bottom=1, top=max(s[key] for s in stats.values()) * 2.5)
        ax.set_ylabel('Median latency (ms, logarithmic scale)')
        ax.set_xticks(x, labels)
        ax.set_title(title, loc='left', fontweight='bold')
        ax.grid(axis='y', alpha=.18)
        ax.set_axisbelow(True)
        ax.spines[['top', 'right']].set_visible(False)
    axes[0].legend(loc='upper left', ncol=3)
    fig.suptitle('1,000,000-row analytics: LINQ vs DuckFrame\n3 fresh-process repetitions; 9 timed warm samples per process; input generation excluded', fontsize=14)
    fig.savefig(path, dpi=160)
    plt.close(fig)


if __name__ == '__main__':
    main()
