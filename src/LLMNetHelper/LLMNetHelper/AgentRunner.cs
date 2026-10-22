using System.Text;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Agent.Orchestration;
using LLMNetHelper.CodeChange;
using LLMNetHelper.Context;
using LLMNetHelper.Core.Analysis;
using LLMNetHelper.Core.Collection;
using LLMNetHelper.Core.Reporting;
using LLMNetHelper.Core.Snapshot;
using LLMNetHelper.Roslyn;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLMNetHelper;

/// <summary>
/// Контракт прогона агента — позволяет подменять в тестах и вызывать из планировщика.
/// </summary>
public interface IAgentRunner
{
    /// <summary>
    /// Выполняет один прогон.
    /// </summary>
    /// <returns>Код возврата: 0 — OK/WARNING, 1 — сбой, 2 — CRITICAL.</returns>
    Task<int> RunAsync(
        bool dryRun,
        bool catchUp,
        DateTime? since,
        int? hours,
        CancellationToken ct);
}

/// <summary>
/// Один прогон агента: сбор фактов → контекст → диагноз → план правки → отчёт.
/// <para>
/// Отличия от прежнего <c>NightlyRunner</c>:
/// <list type="bullet">
///   <item>все зависимости приходят через DI, а не создаются через <c>new</c>;</item>
///   <item>добавлен сбор внешнего контекста и анализ кода;</item>
///   <item>диагноз ставит оркестратор, а не одиночный вызов LLM;</item>
///   <item>опционально применяются правки через <see cref="ChangeTransaction"/>.</item>
/// </list>
/// </para>
/// </summary>
public sealed class AgentRunner : IAgentRunner
{
    private readonly AgentOptions _options;
    private readonly IOptions<LokiOptions> _lokiOptions;
    private readonly IOptions<PrometheusOptions> _prometheusOptions;
    private readonly LlmOptions _llmOptions;
    private readonly ContextOptions _contextOptions;
    private readonly RoslynOptions _roslynOptions;
    private readonly CodeChangeOptions _codeChangeOptions;
    private readonly AgentOrchestrationOptions _orchestrationOptions;

    private readonly IFactStore _factStore;
    private readonly ILokiClient _lokiClient;
    private readonly IPrometheusClient _prometheusClient;
    private readonly SnapshotBuilder _snapshotBuilder;
    private readonly IReportWriter _reportWriter;

    private readonly ContextAggregator _contextAggregator;
    private readonly StructureAnalyzer _structureAnalyzer;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly ChangeTransaction _changeTransaction;

    private readonly ILogger<AgentRunner> _logger;

    public AgentRunner(
        IOptions<AgentOptions> options,
        IOptions<LokiOptions> lokiOptions,
        IOptions<PrometheusOptions> prometheusOptions,
        IOptions<LlmOptions> llmOptions,
        ContextOptions contextOptions,
        RoslynOptions roslynOptions,
        CodeChangeOptions codeChangeOptions,
        AgentOrchestrationOptions orchestrationOptions,
        IFactStore factStore,
        ILokiClient lokiClient,
        IPrometheusClient prometheusClient,
        SnapshotBuilder snapshotBuilder,
        IReportWriter reportWriter,
        ContextAggregator contextAggregator,
        StructureAnalyzer structureAnalyzer,
        IAgentOrchestrator orchestrator,
        ChangeTransaction changeTransaction,
        ILogger<AgentRunner> logger)
    {
        _options = options.Value;
        _lokiOptions = lokiOptions;
        _prometheusOptions = prometheusOptions;
        _llmOptions = llmOptions.Value;
        _contextOptions = contextOptions;
        _roslynOptions = roslynOptions;
        _codeChangeOptions = codeChangeOptions;
        _orchestrationOptions = orchestrationOptions;

        _factStore = factStore;
        _lokiClient = lokiClient;
        _prometheusClient = prometheusClient;
        _snapshotBuilder = snapshotBuilder;
        _reportWriter = reportWriter;

        _contextAggregator = contextAggregator;
        _structureAnalyzer = structureAnalyzer;
        _orchestrator = orchestrator;
        _changeTransaction = changeTransaction;

        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> RunAsync(
        bool dryRun,
        bool catchUp,
        DateTime? since,
        int? hours,
        CancellationToken ct)
    {
        // Окно анализа — всегда по UTC.
        var windowTo = DateTime.UtcNow;
        var windowFrom = since ?? windowTo.AddHours(-(hours ?? _options.WindowHours));

        _logger.LogInformation(
            "Начало прогона агента: окно {From:o} — {To:o}, dry-run={DryRun}, catch-up={CatchUp}",
            windowFrom, windowTo, dryRun, catchUp);

        var runId = await _factStore.CreateRunAsync(
            windowFrom.ToString("o"), windowTo.ToString("o"), ct).ConfigureAwait(false);

        try
        {
            // 1. Детерминированный сбор фактов.
            var lokiFacts = await CollectLokiAsync(windowFrom, windowTo, ct).ConfigureAwait(false);
            var promFacts = await CollectPrometheusAsync(windowFrom, windowTo, ct).ConfigureAwait(false);

            await _factStore.SaveLogFactsAsync(runId, lokiFacts.LogFacts, ct).ConfigureAwait(false);
            await _factStore.SaveErrorSamplesAsync(runId, lokiFacts.ErrorSamples, ct).ConfigureAwait(false);
            await _factStore.SaveMetricFactsAsync(runId, promFacts.MetricFacts, ct).ConfigureAwait(false);

            // 2. Сводка для LLM.
            var snapshot = await _snapshotBuilder
                .BuildAsync(windowFrom, windowTo, lokiFacts, promFacts, ct)
                .ConfigureAwait(false);

            var dateStr = DateOnly.FromDateTime(windowTo).ToString("yyyy-MM-dd");
            var snapshotPath = Path.Combine(_options.ReportsDir, $"snapshot_{dateStr}.md");
            await _reportWriter.WriteSnapshotAsync(snapshotPath, snapshot.SnapshotText, ct).ConfigureAwait(false);

            if (dryRun)
            {
                _logger.LogInformation("Dry-run: внешний контекст и LLM не запрашиваются");
                Console.WriteLine($"[DRY-RUN] Snapshot:\n{snapshot.SnapshotText}");
                await _factStore.FinishRunAsync(runId, "ok", "OK", null, ct).ConfigureAwait(false);
                return 0;
            }

            // 3. Внешний контекст (документация).
            var contextResult = await CollectContextAsync(lokiFacts, promFacts, ct).ConfigureAwait(false);

            // 4. Структура кода (если Roslyn доступен).
            var codeStructure = await DescribeCodeAsync(ct).ConfigureAwait(false);

            // 5. Диагноз от оркестратора.
            var agentContext = new AgentContext(
                RunId: runId,
                WindowFrom: windowFrom,
                WindowTo: windowTo,
                SnapshotText: snapshot.SnapshotText,
                ContextDocuments: contextResult.Documents,
                CodeStructureSummary: codeStructure);

            var diagnosis = await _orchestrator
                .DiagnoseAsync(agentContext, ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Диагноз: {Verdict} ({Orchestrator}), вызовов инструментов: {Calls}",
                diagnosis.Verdict, diagnosis.Orchestrator, diagnosis.ToolCallCount);

            // 6. План правки — только при проблеме.
            var fixPlan = FixPlan.Empty;
            if (_orchestrationOptions.EnableFixPlanning &&
                diagnosis.Verdict is "CRITICAL" or "WARNING")
            {
                fixPlan = await _orchestrator
                    .PlanFixAsync(diagnosis, agentContext, ct)
                    .ConfigureAwait(false);
            }

            // 7. Применение правок (по умолчанию отключено).
            var changeOutcome = ChangeOutcome.Skipped(
                ChangeFailure.Disabled,
                _codeChangeOptions.Enabled
                    ? "правок нет"
                    : "CodeChange:Enabled=false — правки не применяются");

            string? patchPath = null;

            if (fixPlan.HasChanges && _codeChangeOptions.Enabled)
            {
                changeOutcome = await _changeTransaction
                    .ApplyAsync(runId, fixPlan, ct)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Транзакция правок: {Failure}, ветка={Branch}, применено={Applied}",
                    changeOutcome.Failure, changeOutcome.BranchName, changeOutcome.AppliedChanges);

                if (!string.IsNullOrWhiteSpace(changeOutcome.Diff))
                {
                    patchPath = Path.Combine(_options.ReportsDir, $"patch_{runId}.diff");
                    await _reportWriter
                        .WriteSnapshotAsync(patchPath, changeOutcome.Diff, ct)
                        .ConfigureAwait(false);
                }
            }

            // 8. Отчёт.
            var analysis = BuildAnalysis(diagnosis);
            var runInfo = new RunInfo
            {
                Id = runId,
                StartedAt = windowFrom.ToString("o"),
                WindowFrom = windowFrom.ToString("o"),
                WindowTo = windowTo.ToString("o"),
                Status = "ok",
                Verdict = analysis.Verdict
            };

            var agentReport = new AgentReport(
                Orchestrator: diagnosis.Orchestrator,
                Hypotheses: diagnosis.Hypotheses,
                ContextDocuments: contextResult.Documents,
                UnavailableSources: contextResult.UnavailableSources,
                ToolCalls: diagnosis.ToolCallCount,
                CodeStructure: codeStructure,
                FixPlanSummary: fixPlan.HasChanges ? fixPlan.Rationale : null,
                ChangeBranch: changeOutcome.BranchName,
                ChangeCommit: changeOutcome.CommitSha,
                ChangeMessage: changeOutcome.Message,
                PatchPath: patchPath);

            var markdown = MarkdownReportRenderer.Render(
                analysis,
                runInfo,
                _orchestrator.ProviderName,
                promptTokens: null,
                completionTokens: null,
                health: promFacts.MonitoringHealth,
                agentReport: agentReport);

            var json = JsonReportRenderer.Render(
                analysis,
                runInfo,
                _orchestrator.ProviderName,
                promptTokens: null,
                completionTokens: null,
                health: promFacts.MonitoringHealth);

            var reportBase = Path.Combine(_options.ReportsDir, $"monitoring_report_{dateStr}");
            await _reportWriter.WriteMarkdownAsync($"{reportBase}.md", markdown, ct).ConfigureAwait(false);
            await _reportWriter.WriteJsonAsync($"{reportBase}.json", json, ct).ConfigureAwait(false);

            // 9. Уборка истории и финализация.
            await _factStore.PurgeOldRunsAsync(_options.RetentionDays, ct).ConfigureAwait(false);
            await _factStore.FinishRunAsync(runId, "ok", analysis.Verdict, null, ct).ConfigureAwait(false);

            return analysis.Verdict == "CRITICAL" ? 2 : 0;
        }
        catch (OperationCanceledException)
        {
            await _factStore.FinishRunAsync(runId, "failed", null, "отменено", CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Сбой прогона агента");
            await _factStore.FinishRunAsync(runId, "failed", null, ex.Message, ct).ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    /// Сбор фактов из Loki.
    /// </summary>
    private async Task<LokiFacts> CollectLokiAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        _logger.LogInformation("Сбор фактов из Loki");

        var collector = new LokiFactCollector(
            _lokiClient,
            _lokiOptions,
            Options.Create(_options));

        var facts = await collector.CollectAsync(from, to, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Loki: {LogFacts} агрегатов, {Errors} ошибок",
            facts.LogFacts.Count, facts.ErrorSamples.Count);

        return facts;
    }

    /// <summary>
    /// Сбор фактов из Prometheus.
    /// </summary>
    private async Task<PrometheusFacts> CollectPrometheusAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        _logger.LogInformation("Сбор фактов из Prometheus");

        var collector = new PrometheusFactCollector(_prometheusClient, _prometheusOptions);
        var facts = await collector.CollectAsync(from, to, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Prometheus: {Metrics} метрик, {Alerts} алертов",
            facts.MetricFacts.Count, facts.Alerts.Count);

        return facts;
    }

    /// <summary>
    /// Поиск внешнего контекста по фактам инцидента.
    /// </summary>
    private async Task<ContextSearchResult> CollectContextAsync(
        LokiFacts lokiFacts,
        PrometheusFacts promFacts,
        CancellationToken ct)
    {
        if (!_contextOptions.Enabled)
        {
            return new ContextSearchResult([], [], []);
        }

        var queryText = ContextQueryBuilder.FromFacts(lokiFacts.ErrorSamples, promFacts.MetricFacts);

        if (string.IsNullOrWhiteSpace(queryText))
        {
            _logger.LogInformation("Контекст не запрашивается: нет ошибок и аномальных метрик");
            return new ContextSearchResult([], [], []);
        }

        var query = new ContextQuery(queryText, _contextOptions.TopK, _contextOptions.MaxChars);
        var result = await _contextAggregator.SearchAsync(query, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Контекст: {Count} документов из {Sources}",
            result.Documents.Count, result.QueriedSources.Count);

        return result;
    }

    /// <summary>
    /// Краткая сводка структуры кода. Возвращает null, если анализ недоступен.
    /// </summary>
    private async Task<string?> DescribeCodeAsync(CancellationToken ct)
    {
        if (!_roslynOptions.Enabled)
        {
            return null;
        }

        try
        {
            var summary = await _structureAnalyzer.SummarizeAsync(ct).ConfigureAwait(false);

            if (summary is null)
            {
                return null;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Проектов: {summary.ProjectCount}");
            sb.AppendLine($"Типов: {summary.TypeCount}");
            sb.AppendLine($"Методов: {summary.MethodCount}");
            sb.AppendLine($"Проблемных мест (catch/throw): {summary.FailurePointCount}");
            sb.AppendLine($"Проекты: {string.Join(", ", summary.Projects)}");

            return sb.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Анализ структуры кода недоступен");
            return null;
        }
    }

    /// <summary>
    /// Преобразует <see cref="Diagnosis"/> в модель, ожидаемую рендерером отчёта.
    /// </summary>
    private static AnalysisResult BuildAnalysis(Diagnosis diagnosis)
    {
        var findings = new List<Finding>();

        foreach (var item in diagnosis.Findings)
        {
            if (item is Finding finding)
            {
                findings.Add(finding);
            }
        }

        return new AnalysisResult
        {
            Verdict = diagnosis.Verdict,
            Summary = diagnosis.Summary,
            Findings = findings,
            NoiseCandidates = [],
            QuestionsForOwner = diagnosis.QuestionsForOwner.ToList(),
            FallbackUsed = false
        };
    }
}
