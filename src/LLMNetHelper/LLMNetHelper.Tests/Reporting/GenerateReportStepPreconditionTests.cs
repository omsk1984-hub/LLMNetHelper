using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Collection;
using LLMNetHelper.Running;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMNetHelper.Tests.Reporting;

/// <summary>
/// Тест предусловий шага отчёта (план 22, дефект N6):
/// <c>GenerateReportStep</c> обязан падать с понятной причиной
/// (<see cref="InvalidOperationException"/>), а не с <see cref="NullReferenceException"/>
/// из null-forgiving, когда предшествующие шаги не заполнили контекст.
/// </summary>
public class GenerateReportStepPreconditionTests
{
    /// <summary>
    /// Дефект N6: <c>context.Diagnosis!</c> при null даёт NRE.
    /// Требуется явная проверка с внятным сообщением.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_MissingDiagnosis_ThrowsInvalidOperation()
    {
        var step = CreateStep();
        var context = new RunContext
        {
            RunId = 1,
            WindowFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            WindowTo = new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc),
            Diagnosis = null,
            PrometheusFacts = new PrometheusFacts([], [], new MonitoringHealthData())
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => step.ExecuteAsync(context, CancellationToken.None));

        Assert.Contains("Diagnosis", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Дефект N6: <c>context.PrometheusFacts!</c> при null даёт NRE.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_MissingPrometheusFacts_ThrowsInvalidOperation()
    {
        var step = CreateStep();
        var context = new RunContext
        {
            RunId = 1,
            WindowFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            WindowTo = new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc),
            Diagnosis = new Diagnosis(Verdict.Ok, "s", [], [], [], 0, "native"),
            PrometheusFacts = null
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => step.ExecuteAsync(context, CancellationToken.None));

        Assert.Contains("PrometheusFacts", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Дефект N4 (план 22): токены из <see cref="Diagnosis"/> обязаны
    /// доходить до Markdown-отчёта (раньше шаг передавал в рендереры <c>null</c>).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_DiagnosisWithTokens_TokensReachMarkdownReport()
    {
        var writer = new RecordingReportWriter();
        var step = CreateStep(writer);
        var context = new RunContext
        {
            RunId = 1,
            WindowFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            WindowTo = new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc),
            Diagnosis = new Diagnosis(Verdict.Ok, "s", [], [], [], 0, "native", PromptTokens: 123, CompletionTokens: 45),
            PrometheusFacts = new PrometheusFacts([], [], new MonitoringHealthData())
        };

        await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Contains("123", writer.Markdown, StringComparison.Ordinal);
        Assert.Contains("45", writer.Markdown, StringComparison.Ordinal);
        Assert.Contains("\"prompt_tokens\": 123", writer.Json, StringComparison.Ordinal);
    }

    private static GenerateReportStep CreateStep(IReportWriter? writer = null) =>
        new(
            writer ?? new NullReportWriter(),
            new NoopFactStore(),
            Options.Create(new AgentOptions { ReportsDir = Path.GetTempPath() }),
            new StubOrchestrator(),
            NullLogger<GenerateReportStep>.Instance);

    private sealed class NullReportWriter : IReportWriter
    {
        public Task WriteMarkdownAsync(string path, string content, CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteJsonAsync(string path, string json, CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteTextAsync(string path, string content, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingReportWriter : IReportWriter
    {
        public string Markdown { get; private set; } = "";
        public string Json { get; private set; } = "";

        public Task WriteMarkdownAsync(string path, string content, CancellationToken ct = default)
        {
            Markdown = content;
            return Task.CompletedTask;
        }

        public Task WriteJsonAsync(string path, string json, CancellationToken ct = default)
        {
            Json = json;
            return Task.CompletedTask;
        }

        public Task WriteTextAsync(string path, string content, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubOrchestrator : IAgentOrchestrator
    {
        public string ProviderName => "stub";
        public Task<Diagnosis> DiagnoseAsync(AgentContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<FixPlan> PlanFixAsync(Diagnosis diagnosis, AgentContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Минимальный IFactStore: шаг отчёта вызывает только PurgeOldRunsAsync и FinishRunAsync.
    /// План 22, этап 2.7: InitializeAsync и IDisposable убраны из контракта.
    /// </summary>
    private sealed class NoopFactStore : IFactStore
    {
        public Task<long> CreateRunAsync(DateTime windowFrom, DateTime windowTo, CancellationToken ct = default) => Task.FromResult(1L);
        public Task FinishRunAsync(long runId, RunStatus status, Verdict? verdict, string? error, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveLogFactsAsync(long runId, IEnumerable<LogFact> facts, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveErrorSamplesAsync(long runId, IEnumerable<ErrorSample> samples, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveMetricFactsAsync(long runId, IEnumerable<MetricFact> facts, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveLlmRunAsync(long runId, LlmRunInfo info, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RunInfo?> GetLastSuccessfulRunAsync(int hoursBack, CancellationToken ct = default) => Task.FromResult<RunInfo?>(null);
        public Task<List<ErrorSample>> GetPreviousErrorSamplesAsync(long runId, CancellationToken ct = default) => Task.FromResult(new List<ErrorSample>());
        public Task<List<LogFact>> GetPreviousLogFactsAsync(long runId, CancellationToken ct = default) => Task.FromResult(new List<LogFact>());
        public Task<int> PurgeOldRunsAsync(int retentionDays, CancellationToken ct = default) => Task.FromResult(0);
    }
}
