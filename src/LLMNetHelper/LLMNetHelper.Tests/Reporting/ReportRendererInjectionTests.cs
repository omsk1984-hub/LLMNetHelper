using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Analysis;
using LLMNetHelper.Core.Collection;
using LLMNetHelper.Core.Reporting;
using LLMNetHelper.Running;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMNetHelper.Tests.Reporting;

/// <summary>
/// Тесты подменяемости рендереров отчётов (план 22, этап 6.2):
/// <see cref="GenerateReportStep"/> обязан получать рендереры через абстракции
/// <see cref="IMarkdownReportRenderer"/>/<see cref="IJsonReportRenderer"/>,
/// а не вызывать статические классы — иначе формат отчёта нельзя подменить
/// в тестах и версионировать.
/// </summary>
public class ReportRendererInjectionTests
{
    /// <summary>
    /// Внедрённые рендереры используются шагом отчёта: маркерные строки
    /// обязаны попасть в записанные файлы.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UsesInjectedRenderers()
    {
        var writer = new RecordingReportWriter();
        var step = CreateStep(writer, new StubMarkdownRenderer(), new StubJsonRenderer());

        var context = new RunContext
        {
            RunId = 7,
            WindowFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            WindowTo = new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc),
            Diagnosis = new Diagnosis(Verdict.Ok, "s", [], [], [], 0, "native"),
            PrometheusFacts = new PrometheusFacts([], [], new MonitoringHealthData())
        };

        await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal("stub-markdown", writer.Markdown);
        Assert.Equal("stub-json", writer.Json);
    }

    /// <summary>
    /// Без внедрённых рендереров шаг работает на стандартных реализациях
    /// (обратная совместимость: существующие вызовы живы).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithoutInjectedRenderers_UsesDefault()
    {
        var writer = new RecordingReportWriter();
        var step = CreateStep(writer);

        var context = new RunContext
        {
            RunId = 7,
            WindowFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            WindowTo = new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc),
            Diagnosis = new Diagnosis(Verdict.Ok, "сводка", [], [], [], 0, "native"),
            PrometheusFacts = new PrometheusFacts([], [], new MonitoringHealthData())
        };

        await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Contains("Вердикт", writer.Markdown, StringComparison.Ordinal);
        Assert.Contains("\"run_id\": 7", writer.Json, StringComparison.Ordinal);
    }

    private static GenerateReportStep CreateStep(
        IReportWriter writer,
        IMarkdownReportRenderer? markdown = null,
        IJsonReportRenderer? json = null) =>
        new(
            writer,
            new NoopFactStore(),
            Options.Create(new AgentOptions { ReportsDir = Path.GetTempPath() }),
            new StubOrchestrator(),
            NullLogger<GenerateReportStep>.Instance,
            markdown,
            json);

    private sealed class StubMarkdownRenderer : IMarkdownReportRenderer
    {
        public string Render(
            AnalysisResult analysis, RunInfo runInfo, string llmProvider,
            int? promptTokens, int? completionTokens,
            MonitoringHealthData? health, AgentReport? agentReport = null) => "stub-markdown";
    }

    private sealed class StubJsonRenderer : IJsonReportRenderer
    {
        public string Render(
            AnalysisResult analysis, RunInfo runInfo, string llmProvider,
            int? promptTokens, int? completionTokens,
            MonitoringHealthData? health) => "stub-json";
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
