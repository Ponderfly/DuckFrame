using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuckFrame.Benchmarks.Analytics;

public static class AnalyticsSuite
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    public sealed record Settings(int Rows, int Repeats, int Samples, int Warmups, int WarmupMs, string Output);
    public sealed record Sample(double Milliseconds, long AllocatedBytes, int Gen0, int Gen1, int Gen2);
    public sealed record WorkerResult(Analysis Analysis, Backend Backend, int RequestedThreads, int EffectiveThreads,
        int Repetition, int InputRows, int CustomerRows, int ProductRows, int PaymentRows, int ResultRows,
        double GenerateMs, Sample Initialize, Sample FirstQuery, List<Sample> WarmQueries,
        int WarmupCount, double WarmupMs, double WarmupQueryMs, long PeakWorkingSetBytes, bool Correct);
    private sealed record Variant(Backend Backend, int Threads);

    public static async Task<int> Run(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var settings = Parse(args);
        Directory.CreateDirectory(settings.Output);
        var variants = new Variant[]
        {
            new(Backend.Linq, 1), new(Backend.DuckFrame, 1), new(Backend.NativeDuckDb, 1),
            new(Backend.DuckFrame, 0), new(Backend.NativeDuckDb, 0),
        };
        var analyses = Enum.GetValues<Analysis>();
        var reports = new List<WorkerResult>();
        var metadata = new
        {
            StartedUtc = DateTimeOffset.UtcNow,
            Framework = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            ServerGc = GCSettings.IsServerGC,
            Stopwatch.Frequency,
            Settings = settings,
            AnalyticsData.Seed,
        };
        await File.WriteAllTextAsync(Path.Combine(settings.Output, "environment.json"), JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        using var progress = new StreamWriter(Path.Combine(settings.Output, "workers.jsonl"));
        var total = settings.Repeats * variants.Length * analyses.Length;
        Console.WriteLine($"{settings.Rows:N0} facts; {analyses.Length} workloads; {variants.Length} backend/thread variants; {settings.Repeats} fresh processes per case.");
        for (var repetition = 0; repetition < settings.Repeats; repetition++)
        {
            // 按轮旋转场景和引擎顺序，避免固定顺序与机器负载漂移绑定。
            for (var scenarioIndex = 0; scenarioIndex < analyses.Length; scenarioIndex++)
            {
                var analysis = analyses[(scenarioIndex + repetition * 2) % analyses.Length];
                for (var variantIndex = 0; variantIndex < variants.Length; variantIndex++)
                {
                    var variant = variants[(variantIndex + scenarioIndex + repetition) % variants.Length];
                    var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                    foreach (var argument in new[]
                    {
                        "--analytics-worker", "--rows", settings.Rows.ToString(), "--samples", settings.Samples.ToString(),
                        "--warmups", settings.Warmups.ToString(), "--warmup-ms", settings.WarmupMs.ToString(),
                        "--analysis", analysis.ToString(), "--backend", variant.Backend.ToString(),
                        "--threads", variant.Threads.ToString(), "--repetition", repetition.ToString(),
                    }) start.ArgumentList.Add(argument);
                    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start benchmark worker.");
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    try { await process.WaitForExitAsync(timeout.Token); }
                    catch { process.Kill(entireProcessTree: true); throw; }
                    var output = await stdout;
                    var error = await stderr;
                    if (process.ExitCode != 0) throw new InvalidOperationException($"{analysis}/{variant} failed:\n{error}\n{output}");
                    var report = JsonSerializer.Deserialize<WorkerResult>(output, Json) ?? throw new InvalidOperationException("Empty worker report.");
                    reports.Add(report);
                    await progress.WriteLineAsync(JsonSerializer.Serialize(report, Json));
                    await progress.FlushAsync();
                    Console.WriteLine($"[{reports.Count}/{total}] {analysis,-20} {variant.Backend,-12} threads={report.EffectiveThreads,2} " +
                        $"load={report.Initialize.Milliseconds,8:F1} ms first={report.FirstQuery.Milliseconds,8:F1} ms " +
                        $"warm={Median(report.WarmQueries.Select(s => s.Milliseconds)),8:F1} ms rows={report.ResultRows} OK");
                }
            }
        }
        await File.WriteAllTextAsync(Path.Combine(settings.Output, "results.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions(Json) { WriteIndented = true }));
        using var csv = new StreamWriter(Path.Combine(settings.Output, "samples.csv"));
        csv.WriteLine("analysis,backend,requested_threads,effective_threads,process_repetition,phase,sample,elapsed_ms,allocated_bytes,gen0,gen1,gen2,result_rows");
        foreach (var report in reports)
        {
            WriteCsv(csv, report, "initialize", 0, report.Initialize);
            WriteCsv(csv, report, "first_query", 0, report.FirstQuery);
            for (var i = 0; i < report.WarmQueries.Count; i++) WriteCsv(csv, report, "warm_query", i, report.WarmQueries[i]);
        }
        Console.WriteLine("Reports written to " + Path.GetFullPath(settings.Output));
        return 0;
    }

    public static int Worker(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var settings = Parse(args);
        var analysis = Enum.Parse<Analysis>(Value(args, "--analysis")!);
        var backend = Enum.Parse<Backend>(Value(args, "--backend")!);
        var threads = int.Parse(Value(args, "--threads")!);
        var repetition = int.Parse(Value(args, "--repetition")!);
        var generated = Stopwatch.StartNew();
        var data = AnalyticsData.Create(settings.Rows, analysis);
        generated.Stop();
        // 输入对象准备不计入查询；在初始化引擎前清理准备阶段的临时分配。
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var engine = new AnalyticsEngine(data, analysis, backend, threads);
        var initialize = backend == Backend.Linq ? new Sample(0, 0, 0, 0, 0) : MeasureInitialization(engine);
        // 首次查询之前不执行 oracle，也不执行任何被测分析查询。
        var first = First(engine, data, analysis, out var validator, out var resultRows);
        var warmupWatch = Stopwatch.StartNew();
        var warmupCount = 0;
        var warmupQueryMs = 0.0;
        do
        {
            warmupQueryMs += Warmup(engine, validator);
            warmupCount++;
        } while (warmupCount < settings.Warmups || (warmupQueryMs < settings.WarmupMs && warmupCount < 1_000));
        warmupWatch.Stop();
        var samples = new List<Sample>(settings.Samples);
        for (var i = 0; i < settings.Samples; i++) samples.Add(TimedQuery(engine, validator));
        using var process = Process.GetCurrentProcess();
        var report = new WorkerResult(analysis, backend, threads, engine.EffectiveThreads, repetition, data.Sales.Count,
            data.Customers.Count, data.Products.Count, data.Payments.Count, resultRows, generated.Elapsed.TotalMilliseconds,
            initialize, first, samples, warmupCount, warmupWatch.Elapsed.TotalMilliseconds, warmupQueryMs, process.PeakWorkingSet64, true);
        Console.WriteLine(JsonSerializer.Serialize(report, Json));
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Sample MeasureInitialization(AnalyticsEngine engine)
    {
        var before = Snapshot();
        var start = Stopwatch.GetTimestamp();
        engine.Initialize();
        var stop = Stopwatch.GetTimestamp();
        return Finish(before, start, stop);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Sample First(AnalyticsEngine engine, AnalyticsData data, Analysis analysis, out AnalyticsValidator validator, out int rows)
    {
        var before = Snapshot();
        var start = Stopwatch.GetTimestamp();
        var result = engine.Query();
        var stop = Stopwatch.GetTimestamp();
        var sample = Finish(before, start, stop);
        validator = new AnalyticsValidator(data, analysis);
        rows = validator.Validate(result);
        return sample;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static double Warmup(AnalyticsEngine engine, AnalyticsValidator validator)
    {
        var start = Stopwatch.GetTimestamp();
        var result = engine.Query();
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        validator.Validate(result);
        return elapsed;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Sample TimedQuery(AnalyticsEngine engine, AnalyticsValidator validator)
    {
        var before = Snapshot();
        var start = Stopwatch.GetTimestamp();
        var result = engine.Query();
        var stop = Stopwatch.GetTimestamp();
        var sample = Finish(before, start, stop);
        validator.Validate(result);
        return sample;
    }

    private readonly record struct Counters(long Allocated, int Gen0, int Gen1, int Gen2);
    private static Counters Snapshot() => new(GC.GetTotalAllocatedBytes(precise: true), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    private static Sample Finish(Counters before, long start, long stop)
    {
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before.Allocated;
        return new(Stopwatch.GetElapsedTime(start, stop).TotalMilliseconds, allocated,
            GC.CollectionCount(0) - before.Gen0, GC.CollectionCount(1) - before.Gen1, GC.CollectionCount(2) - before.Gen2);
    }

    private static Settings Parse(string[] args)
    {
        int Number(string key, int fallback) => int.TryParse(Value(args, key), out var n) ? n : fallback;
        var settings = new Settings(Number("--rows", 1_000_000), Number("--repeats", 3), Number("--samples", 9),
            Number("--warmups", 5), Number("--warmup-ms", 1_000), Value(args, "--output") ?? "artifacts/analytics-1m");
        if (settings.Rows < 1 || settings.Repeats < 1 || settings.Samples < 1 || settings.Warmups < 1 || settings.WarmupMs < 0)
            throw new ArgumentException("Rows/repeats/samples/warmups must be positive; warmup-ms must be nonnegative.");
        return settings;
    }
    private static string? Value(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2];
    }
    private static void WriteCsv(TextWriter writer, WorkerResult r, string phase, int index, Sample s) =>
        writer.WriteLine($"{r.Analysis},{r.Backend},{r.RequestedThreads},{r.EffectiveThreads},{r.Repetition},{phase},{index},{s.Milliseconds:F6},{s.AllocatedBytes},{s.Gen0},{s.Gen1},{s.Gen2},{r.ResultRows}");
}
