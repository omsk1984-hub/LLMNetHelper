using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Collection;
using LLMNetHelper.Core.Snapshot;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMNetHelper.Tests.Snapshot;

/// <summary>
/// Тесты построителя сводки (план 22, этап 6.7): покрывают риск-хотспот
/// <c>SnapshotBuilder.BuildAsync</c> (CC=16) — все секции Markdown-сводки и
/// ветки диффа с прошлым прогоном. Сеть/БД не используются: факты подаются
/// напрямую, <see cref="IFactStore"/> и <see cref="ISnapshotBudgetEnforcer"/>
/// подменены фейками.
/// </summary>
public class SnapshotBuilderTests
{
    private static readonly DateTime WindowFrom = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowTo = new(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc);

    private static LogFact Fact(string lvl, string job, string logger, string symbol, int count) => new()
    {
        Id = 1,
        RunId = 1,
        Job = job,
        Svc = job,
        Lvl = lvl,
        Logger = logger,
        Symbol = symbol,
        BucketStart = WindowFrom,
        Count = count
    };

    private static ErrorSample Sample(string signature, int count, string text) => new()
    {
        Id = 1,
        RunId = 1,
        Signature = signature,
        Lvl = "fail",
        Job = "w",
        Logger = "L",
        Symbol = "S",
        FirstSeen = WindowFrom,
        LastSeen = WindowTo,
        Count = count,
        SampleText = text
    };

    private static LokiFacts Loki(IEnumerable<LogFact> facts, IEnumerable<ErrorSample> samples) =>
        new(facts.ToList(), samples.ToList(), 0);

    private static PrometheusFacts Prom(IEnumerable<MetricFact>? trends = null) =>
        new(trends?.ToList() ?? [], [], new MonitoringHealthData());

    private static MetricFact Trend(string name, double value, double slope, double r2) => new()
    {
        Id = 1,
        RunId = 1,
        Name = name,
        Value = value,
        Labels = """{"instance":"a"}""",
        SlopePerHour = slope,
        R2 = r2,
        WindowFrom = WindowFrom,
        WindowTo = WindowTo
    };

    [Fact]
    public async Task BuildAsync_MetadataAndLogAggregates_Rendered()
    {
        var builder = CreateBuilder();

        var result = await builder.BuildAsync(
            WindowFrom, WindowTo,
            Loki([Fact("fail", "artas_writer", "FinamApi", "SendOrder", 17)], []),
            Prom());

        Assert.Contains("# Сводка для LLM-анализа", result.SnapshotText, StringComparison.Ordinal);
        Assert.Contains("**Окно анализа:** 2026-01-01 00:00 UTC — 2026-01-01 06:00 UTC", result.SnapshotText, StringComparison.Ordinal);
        Assert.Contains("**Записей Loki:** 1 агрегатов, 0 уникальных ошибок", result.SnapshotText, StringComparison.Ordinal);
        Assert.Contains("| fail | artas_writer | FinamApi | SendOrder | 17 |", result.SnapshotText, StringComparison.Ordinal);
        Assert.False(result.WasTrimmed);
    }

    [Fact]
    public async Task BuildAsync_MetricTrends_RenderedOnlyWhenNonZero()
    {
        var builder = CreateBuilder();

        var result = await builder.BuildAsync(
            WindowFrom, WindowTo,
            Loki([], []),
            Prom([Trend("heap", 512, 12.5, 0.9), Trend("flat", 1, 0, 0)]));

        // Тренд с slope≠0 попадает, плоская метрика — нет.
        // Числовые поля не проверяются по строке: прод рендерит :F2/:F3 в
        // текущей культуре (ru-RU → запятая), см. замечание по культуре в плане 22.
        Assert.Contains("## Тренды метрик", result.SnapshotText, StringComparison.Ordinal);
        Assert.Contains("| heap | 512 |", result.SnapshotText, StringComparison.Ordinal);
        Assert.DoesNotContain("| flat |", result.SnapshotText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_TopErrors_SortedByCountAndTruncated()
    {
        var builder = CreateBuilder(topErrorsLimit: 1, maxSampleChars: 10);

        var result = await builder.BuildAsync(
            WindowFrom, WindowTo,
            Loki([], [Sample("sig-aaaa", 3, "1234567890EXTRA"), Sample("sig-bbbb", 9, "boom")]),
            Prom());

        // Топ-1 по count: sig-bbbb (9x).
        Assert.Contains("## Топ уникальных ошибок", result.SnapshotText, StringComparison.Ordinal);
        Assert.Contains("### 1. [9x] sig-bbbb", result.SnapshotText, StringComparison.Ordinal);
        // Лимит топ-ошибок: sig-aaaa не попадает.
        Assert.DoesNotContain("sig-aaaa", result.SnapshotText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_LongSample_TruncatedWithEllipsis()
    {
        var builder = CreateBuilder(topErrorsLimit: 5, maxSampleChars: 5);

        var result = await builder.BuildAsync(
            WindowFrom, WindowTo,
            Loki([], [Sample("sig", 1, "ABCDEFGHIJ")]),
            Prom());

        Assert.Contains("ABCDE...", result.SnapshotText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_NoPreviousRun_DiffSectionOmitted()
    {
        var builder = CreateBuilder(previousRun: null);

        var result = await builder.BuildAsync(
            WindowFrom, WindowTo,
            Loki([], [Sample("sig", 1, "x")]),
            Prom());

        Assert.DoesNotContain("## Дифф с прошлым прогоном", result.SnapshotText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_PreviousRunPresent_ComputesNewAndDisappeared()
    {
        // Прошлый прогон содержал sig-old; текущий — sig-new. Значит sig-new —
        // новая ошибка, sig-old — исчезнувшая.
        var previousRun = new RunInfo
        {
            Id = 42,
            StartedAt = WindowFrom.AddDays(-1),
            WindowFrom = WindowFrom.AddDays(-1),
            WindowTo = WindowFrom,
            Status = RunStatus.Ok
        };
        var builder = CreateBuilder(
            previousRun: previousRun,
            previousSamples: [Sample("sig-old", 1, "y")]);

        var result = await builder.BuildAsync(
            WindowFrom, WindowTo,
            Loki([], [Sample("sig-new", 5, "z")]),
            Prom());

        Assert.Contains("## Дифф с прошлым прогоном", result.SnapshotText, StringComparison.Ordinal);
        Assert.Contains("**Новые ошибки:** 1", result.SnapshotText, StringComparison.Ordinal);
        Assert.Contains("`sig-new`", result.SnapshotText, StringComparison.Ordinal);
        Assert.Contains("**Исчезнувшие ошибки:** 1", result.SnapshotText, StringComparison.Ordinal);
        Assert.Contains("`sig-old`", result.SnapshotText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_SameSignaturesAsPrevious_NoChangeNotice()
    {
        var previousRun = new RunInfo
        {
            Id = 7,
            StartedAt = WindowFrom.AddDays(-1),
            WindowFrom = WindowFrom.AddDays(-1),
            WindowTo = WindowFrom,
            Status = RunStatus.Ok
        };
        var builder = CreateBuilder(
            previousRun: previousRun,
            previousSamples: [Sample("sig", 1, "y")]);

        var result = await builder.BuildAsync(
            WindowFrom, WindowTo,
            Loki([], [Sample("sig", 1, "z")]),
            Prom());

        Assert.Contains("Значимых изменений относительно прошлого прогона не обнаружено.", result.SnapshotText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_DelegatesBudgetEnforcement()
    {
        // Эндпоинт обязан вызвать enforcer с теми же топ-ошибками и лимитами.
        var enforcer = new RecordingEnforcer();
        var builder = new SnapshotBuilder(
            Options.Create(new AgentOptions { TopErrorsLimit = 3, MaxSampleChars = 50 }),
            new FakeFactStore(),
            enforcer);

        var result = await builder.BuildAsync(
            WindowFrom, WindowTo,
            Loki([], [Sample("a", 1, "x"), Sample("b", 2, "x")]),
            Prom());

        Assert.Equal(3, enforcer.TopErrorsLimit);
        Assert.Equal(50, enforcer.MaxSampleChars);
        Assert.Equal(2, enforcer.SamplesCount);
        // Возврат enforcer проходит насквозь.
        Assert.Same(enforcer.Result.SnapshotText, result.SnapshotText);
        Assert.True(result.WasTrimmed);
    }

    private static SnapshotBuilder CreateBuilder(
        int topErrorsLimit = 5,
        int maxSampleChars = 2000,
        RunInfo? previousRun = null,
        IReadOnlyList<ErrorSample>? previousSamples = null)
    {
        var store = new FakeFactStore(previousRun, previousSamples ?? []);
        return new SnapshotBuilder(
            Options.Create(new AgentOptions { TopErrorsLimit = topErrorsLimit, MaxSampleChars = maxSampleChars }),
            store,
            new TokenBudgetGuard());
    }

    /// <summary>Enforcer-заглушка: фиксирует аргументы и возвращает помеченный результат.</summary>
    private sealed class RecordingEnforcer : ISnapshotBudgetEnforcer
    {
        public int TopErrorsLimit { get; private set; }
        public int MaxSampleChars { get; private set; }
        public int SamplesCount { get; private set; }
        public SnapshotResult Result { get; } = new("trimmed", [], true);

        public SnapshotResult Enforce(string snapshotText, int topErrorsLimit, int maxSampleChars, List<ErrorSample> errorSamples)
        {
            TopErrorsLimit = topErrorsLimit;
            MaxSampleChars = maxSampleChars;
            SamplesCount = errorSamples.Count;
            return Result;
        }
    }

    /// <summary>Фейковое хранилище: отдаёт заготовленный прошлый прогон и его ошибки.</summary>
    private sealed class FakeFactStore(
        RunInfo? previousRun = null,
        IReadOnlyList<ErrorSample>? previousSamples = null) : IFactStore
    {
        public Task<RunInfo?> GetLastSuccessfulRunAsync(int hoursBack, CancellationToken ct = default) =>
            Task.FromResult(previousRun);

        public Task<List<ErrorSample>> GetPreviousErrorSamplesAsync(long runId, CancellationToken ct = default) =>
            Task.FromResult((previousSamples ?? []).ToList());

        public Task<long> CreateRunAsync(DateTime windowFrom, DateTime windowTo, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task FinishRunAsync(long runId, RunStatus status, Verdict? verdict, string? error, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SaveLogFactsAsync(long runId, IEnumerable<LogFact> facts, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SaveErrorSamplesAsync(long runId, IEnumerable<ErrorSample> samples, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SaveMetricFactsAsync(long runId, IEnumerable<MetricFact> facts, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SaveLlmRunAsync(long runId, LlmRunInfo info, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<List<LogFact>> GetPreviousLogFactsAsync(long runId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<int> PurgeOldRunsAsync(int retentionDays, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
