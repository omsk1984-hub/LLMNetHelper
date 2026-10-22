using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Analysis;
using LLMNetHelper.Core.Collection;
using System.Text.Json;

namespace LLMNetHelper.Core.Reporting;

/// <summary>
/// JSON-рендерер отчёта.
/// </summary>
public static class JsonReportRenderer
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
    public static string Render(
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
            started_at = runInfo.StartedAt,
            window_from = runInfo.WindowFrom,
            window_to = runInfo.WindowTo,
            verdict = analysis.Verdict,
            summary = analysis.Summary,
            llm_provider = llmProvider,
            prompt_tokens = promptTokens,
            completion_tokens = completionTokens,
            fallback_used = analysis.FallbackUsed,
            findings = analysis.Findings.Select(f => new
            {
                f.Id,
                f.Severity,
                f.Title,
                f.Evidence,
                f.Impact,
                f.Recommendation,
                f.Confidence
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
