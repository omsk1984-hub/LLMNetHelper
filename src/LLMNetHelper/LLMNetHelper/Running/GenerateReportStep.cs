using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Analysis;
using LLMNetHelper.Core.Collection;
using LLMNetHelper.Core.Reporting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLMNetHelper.Running;

/// <summary>
/// Шаг: генерация отчётов (Markdown + JSON) и финализация прогона.
/// </summary>
public sealed class GenerateReportStep : IRunStep
{
    public string Name => "GenerateReport";

    private readonly IReportWriter _reportWriter;
    private readonly IFactStore _factStore;
    private readonly IOptions<AgentOptions> _agentOptions;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly ILogger<GenerateReportStep> _logger;

    public GenerateReportStep(
        IReportWriter reportWriter,
        IFactStore factStore,
        IOptions<AgentOptions> agentOptions,
        IAgentOrchestrator orchestrator,
        ILogger<GenerateReportStep> logger)
    {
        _reportWriter = reportWriter;
        _factStore = factStore;
        _agentOptions = agentOptions;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    public async Task ExecuteAsync(RunContext context, CancellationToken ct)
    {
        // Дефект N6 (план 22): явные проверки предусловий вместо null-forgiving.
        // Гарантия порядка шагов сейчас — только порядок DI-регистраций; при сбое
        // предыдущего шага здесь был бы NullReferenceException без объяснений.
        var diagnosis = context.Diagnosis
            ?? throw new InvalidOperationException(
                "GenerateReport: Diagnosis отсутствует в контексте прогона — шаг Diagnose не выполнен или упал.");
        var prometheusFacts = context.PrometheusFacts
            ?? throw new InvalidOperationException(
                "GenerateReport: PrometheusFacts отсутствуют в контексте прогона — шаг CollectPrometheus не выполнен или упал.");

        var analysis = BuildAnalysis(diagnosis);
        var dateStr = DateOnly.FromDateTime(context.WindowTo).ToString("yyyy-MM-dd");

        var runInfo = new RunInfo
        {
            Id = context.RunId,
            StartedAt = context.WindowFrom.ToString("o"),
            WindowFrom = context.WindowFrom.ToString("o"),
            WindowTo = context.WindowTo.ToString("o"),
            Status = RunStatus.Ok,
            Verdict = analysis.Verdict
        };

        var agentReport = new AgentReport(
            Orchestrator: diagnosis.Orchestrator,
            Hypotheses: diagnosis.Hypotheses,
            ContextDocuments: context.ContextResult?.Documents ?? [],
            UnavailableSources: context.ContextResult?.UnavailableSources ?? [],
            ToolCalls: diagnosis.ToolCallCount,
            CodeStructure: context.CodeStructureSummary,
            FixPlanSummary: context.FixPlan?.HasChanges == true ? context.FixPlan.Rationale : null,
            ChangeBranch: context.ChangeOutcome?.BranchName,
            ChangeCommit: context.ChangeOutcome?.CommitSha,
            ChangeMessage: context.ChangeOutcome?.Message,
            PatchPath: context.PatchPath);

        // Дефект N4 (план 22): токены из Diagnosis доходят до рендереров
        // (раньше здесь были жёсткие null).
        var markdown = MarkdownReportRenderer.Render(
            analysis,
            runInfo,
            _orchestrator.ProviderName,
            promptTokens: diagnosis.PromptTokens,
            completionTokens: diagnosis.CompletionTokens,
            health: prometheusFacts.MonitoringHealth,
            agentReport: agentReport);

        var json = JsonReportRenderer.Render(
            analysis,
            runInfo,
            _orchestrator.ProviderName,
            promptTokens: diagnosis.PromptTokens,
            completionTokens: diagnosis.CompletionTokens,
            health: prometheusFacts.MonitoringHealth);

        var reportBase = Path.Combine(_agentOptions.Value.ReportsDir, $"monitoring_report_{dateStr}");
        await _reportWriter.WriteMarkdownAsync($"{reportBase}.md", markdown, ct).ConfigureAwait(false);
        await _reportWriter.WriteJsonAsync($"{reportBase}.json", json, ct).ConfigureAwait(false);

        // Уборка истории и финализация.
        await _factStore.PurgeOldRunsAsync(_agentOptions.Value.RetentionDays, ct).ConfigureAwait(false);
        await _factStore.FinishRunAsync(context.RunId, RunStatus.Ok, analysis.Verdict, null, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Отчёт сохранён: {ReportBase}.md/json, вердикт={Verdict}",
            reportBase, analysis.Verdict);
    }

    // internal для сквозных тестов отчётности (InternalsVisibleTo LLMNetHelper.Tests):
    // критерий готовности 0.1 — находки обязаны доходить до строк Markdown/JSON отчёта.
    internal static AnalysisResult BuildAnalysis(Diagnosis diagnosis)
    {
        return new AnalysisResult
        {
            Verdict = diagnosis.Verdict,
            Summary = diagnosis.Summary,
            // Diagnosis.Findings типизирован (IReadOnlyList<Finding>) — фильтр
            // по типу удалён: именно он молча терял все находки (дефект D1, план 18).
            Findings = [.. diagnosis.Findings],
            NoiseCandidates = [],
            QuestionsForOwner = diagnosis.QuestionsForOwner.ToList(),
            FallbackUsed = false
        };
    }
}
