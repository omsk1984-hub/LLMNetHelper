using System.Text.Json;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Agent.Orchestration;
using LLMNetHelper.Agent.Tools;
using LLMNetHelper.Core.Collection;
using LLMNetHelper.Running;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LLMNetHelper.Tests.Reporting;

/// <summary>
/// Контрактные тесты отчётности (план 22, этап 6.4): сырой JSON-ответ модели →
/// <see cref="NativeAgentOrchestrator"/> → <see cref="Diagnosis"/> →
/// <see cref="GenerateReportStep"/> (реальные рендереры) → строки Markdown/JSON.
/// <para>
/// Отличаются от <c>ReportEndToEndTests</c> (шаг → строка) и
/// <c>GenerateReportStepPreconditionTests</c> (предусловия/токены на готовом
/// Diagnosis) тем, что начинаются с текста ответа LLM: проверяют всю цепочку
/// контрактов целиком, включая разбор гипотез и сохранение нераспознанных
/// severity/confidence как исходных строк (вариант (б), этап 1.4).
/// </para>
/// <para>
/// Сеть не используется: фейковый <see cref="IChatClient"/> отдаёт заготовленный текст.
/// </para>
/// </summary>
public class ReportContractTests
{
    /// <summary>
    /// Гипотезы из ответа LLM обязаны дойти до раздела «Гипотезы о причине»
    /// Markdown-отчёта (в JSON-отчёте гипотез нет by design — они часть
    /// агентского блока только в Markdown).
    /// </summary>
    [Fact]
    public async Task LlmResponse_Hypotheses_ReachMarkdownReport()
    {
        var (markdown, _) = await RenderReportsAsync("""
            {
              "verdict": "WARNING",
              "summary": "рост ошибок в Writer",
              "findings": [],
              "hypotheses": [
                { "root_cause": "таймаут Finam", "confidence": "high", "evidence": "лог", "references": ["log:1", "log:2"] }
              ],
              "questions_for_owner": []
            }
            """);

        Assert.Contains("### Гипотезы о причине", markdown, StringComparison.Ordinal);
        Assert.Contains("- **[high]** таймаут Finam", markdown, StringComparison.Ordinal);
        Assert.Contains("- Доказательства: лог", markdown, StringComparison.Ordinal);
        Assert.Contains("- Ссылки: log:1, log:2", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// Вариант (б) этапа 1.4: нераспознанные severity/confidence модели
    /// сохраняются в отчётах исходной строкой, а не молча теряются в Info/low.
    /// </summary>
    [Fact]
    public async Task LlmResponse_UnknownSeverityAndConfidence_PreservedAsRawInReports()
    {
        var (markdown, json) = await RenderReportsAsync("""
            {
              "verdict": "WARNING",
              "summary": "s",
              "findings": [{
                "id": "F1",
                "severity": "catastrophic",
                "title": "High memory in Writer",
                "evidence": "RSS growing",
                "impact": "OOM risk",
                "recommendation": "Check for leaks",
                "confidence": "very-high"
              }],
              "hypotheses": [
                { "root_cause": "утечка", "confidence": "almost-sure", "evidence": "e", "references": [] }
              ],
              "questions_for_owner": []
            }
            """);

        // Markdown: исходные строки в блоке находки и в блоке гипотезы.
        Assert.Contains("**Серьёзность:** catastrophic | **Уверенность:** very-high", markdown, StringComparison.Ordinal);
        Assert.Contains("- **[almost-sure]** утечка", markdown, StringComparison.Ordinal);

        // JSON: те же значения в wire-поле.
        using var doc = JsonDocument.Parse(json);
        var finding = doc.RootElement.GetProperty("findings")[0];
        Assert.Equal("catastrophic", finding.GetProperty("severity").GetString());
        Assert.Equal("very-high", finding.GetProperty("confidence").GetString());
    }

    /// <summary>
    /// Usage модели (дефект N4) обязан пройти всю цепочку до обоих отчётов:
    /// LLM-ответ с токенами → Diagnosis → Markdown «Токены» и JSON «*_tokens».
    /// </summary>
    [Fact]
    public async Task LlmResponse_UsageTokens_ReachBothReports()
    {
        var (markdown, json) = await RenderReportsAsync(
            """{"verdict":"OK","summary":"всё хорошо","findings":[],"hypotheses":[],"questions_for_owner":[]}""",
            promptTokens: 1234,
            completionTokens: 567);

        Assert.Contains("**Токены:** 1234 в → 567 в", markdown, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1234, doc.RootElement.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(567, doc.RootElement.GetProperty("completion_tokens").GetInt32());
    }

    /// <summary>
    /// Прогоняет полный конвейер отчёта: оркестратор разбирает ответ модели,
    /// шаг строит оба отчёта реальными рендерерами и записывает их в фейковый писатель.
    /// </summary>
    private static async Task<(string Markdown, string Json)> RenderReportsAsync(
        string llmResponse,
        long? promptTokens = null,
        long? completionTokens = null)
    {
        IChatClient chat = promptTokens is null && completionTokens is null
            ? new ScriptedChatClient(llmResponse)
            : new UsageChatClient(llmResponse, promptTokens!.Value, completionTokens!.Value);

        var orchestrator = new NativeAgentOrchestrator(
            chat,
            new ToolRegistry([]),
            new AgentOrchestrationOptions(),
            NullLogger<NativeAgentOrchestrator>.Instance);

        var windowFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var windowTo = new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc);

        var diagnosis = await orchestrator.DiagnoseAsync(new AgentContext(
            RunId: 1,
            WindowFrom: windowFrom,
            WindowTo: windowTo,
            SnapshotText: "# Сводка\nошибок: 0",
            ContextDocuments: [],
            CodeStructureSummary: null));

        var writer = new RecordingReportWriter();
        var step = new GenerateReportStep(
            writer,
            new NoopFactStore(),
            Options.Create(new AgentOptions { ReportsDir = Path.GetTempPath() }),
            orchestrator,
            NullLogger<GenerateReportStep>.Instance);

        await step.ExecuteAsync(new RunContext
        {
            RunId = 1,
            WindowFrom = windowFrom,
            WindowTo = windowTo,
            Diagnosis = diagnosis,
            PrometheusFacts = new PrometheusFacts([], [], new MonitoringHealthData())
        }, CancellationToken.None);

        return (writer.Markdown, writer.Json);
    }

    /// <summary>Фейковый чат-клиент: отдаёт заготовленный текст (контракт MEAI).</summary>
    private sealed class ScriptedChatClient(string text) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = "scripted-model"
            });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Фейковый чат-клиент с заполненным Usage (дефект N4).</summary>
    private sealed class UsageChatClient(string text, long inputTokens, long outputTokens) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = "usage-model",
                Usage = new UsageDetails
                {
                    InputTokenCount = inputTokens,
                    OutputTokenCount = outputTokens
                }
            });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
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
