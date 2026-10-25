using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Analysis;
using LLMNetHelper.Core.Collection;
using System.Text.Json;

namespace LLMNetHelper.Core.Reporting;

/// <summary>
/// JSON-рендерер отчёта (план 22, этап 6.2: экземплярный класс,
/// реализующий <see cref="IJsonReportRenderer"/> — подменяемость в тестах).
/// </summary>
public sealed class JsonReportRenderer : IJsonReportRenderer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Формирует JSON-отчёт из результатов анализа.
    /// </summary>
    public string Render(
        AnalysisResult analysis,
        RunInfo runInfo,
        string llmProvider,
        int? promptTokens,
        int? completionTokens,
        MonitoringHealthData? health)
    {
        var report = new
        {
            run_id = runInfo.Id,
            // План 22, этап 1.5: DateTime → прежняя ISO-строка ("o") в отчёте.
            started_at = runInfo.StartedAt.ToString("o"),
            window_from = runInfo.WindowFrom.ToString("o"),
            window_to = runInfo.WindowTo.ToString("o"),
            // План 22, этап 1.1: enum → прежняя wire-строка отчёта.
            verdict = VerdictFormat.ToWire(analysis.Verdict),
            summary = analysis.Summary,
            llm_provider = llmProvider,
            prompt_tokens = promptTokens,
            completion_tokens = completionTokens,
            fallback_used = analysis.FallbackUsed,
            // План 22, этап 1.4: enum → прежний wire-формат строк.
            findings = analysis.Findings.Select(f => new
            {
                f.Id,
                severity = FindingFormat.ToWire(f.Severity, f.RawSeverity),
                f.Title,
                f.Evidence,
                f.Impact,
                f.Recommendation,
                confidence = FindingFormat.ToWire(f.Confidence, f.RawConfidence)
            }).ToArray(),
            noise_candidates = analysis.NoiseCandidates.ToArray(),
            questions_for_owner = analysis.QuestionsForOwner.ToArray(),
            monitoring_health = health != null ? new
            {
                prometheus_up = health.PrometheusUp,
                loki_ready = health.LokiReady,
                firing_alerts = health.FiringAlerts,
                all_healthy = health.AllHealthy
            } : (object?)null
        };

        return JsonSerializer.Serialize(report, JsonOpts);
    }
}
