using System.Text;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Analysis;
using LLMNetHelper.Core.Collection;

namespace LLMNetHelper.Core.Reporting;

/// <summary>
/// Markdown-рендерер отчёта.
/// </summary>
public static class MarkdownReportRenderer
{
    /// <summary>
    /// Формирует Markdown-отчёт из результатов анализа.
    /// </summary>
    /// <param name="analysis">Результат разбора ответа LLM.</param>
    /// <param name="runInfo">Метаданные прогона.</param>
    /// <param name="llmProvider">Имя LLM-провайдера.</param>
    /// <param name="promptTokens">Токенов во входе (может быть null).</param>
    /// <param name="completionTokens">Токенов в ответе (может быть null).</param>
    /// <param name="health">Состояние мониторинга (может быть null).</param>
    /// <param name="agentReport">Агентские сведения: гипотезы, контекст, правки.</param>
    public static string Render(
        AnalysisResult analysis,
        RunInfo runInfo,
        string llmProvider,
        int? promptTokens,
        int? completionTokens,
        MonitoringHealthData? health,
        AgentReport? agentReport = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Отчёт ночного LLM-агента мониторинга");
        sb.AppendLine();

        // Шапка
        sb.AppendLine($"**Вердикт:** {GetVerdictEmoji(analysis.Verdict)} {analysis.Verdict}");
        sb.AppendLine($"**Запуск:** {runInfo.StartedAt} UTC");
        sb.AppendLine($"**Окно:** {runInfo.WindowFrom} — {runInfo.WindowTo}");
        sb.AppendLine($"**LLM-провайдер:** {llmProvider}");

        if (promptTokens.HasValue && completionTokens.HasValue)
            sb.AppendLine($"**Токены:** {promptTokens} в → {completionTokens} в");

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // TL;DR
        if (!string.IsNullOrEmpty(analysis.Summary))
        {
            sb.AppendLine("## TL;DR");
            sb.AppendLine();
            sb.AppendLine(analysis.Summary);
            sb.AppendLine();
        }

        // Health-блок
        if (health != null)
        {
            sb.AppendLine("## Состояние мониторинга");
            sb.AppendLine();
            var promEmoji = health.PrometheusUp ? "✅" : "❌";
            var lokiEmoji = health.LokiReady ? "✅" : "❌";
            var healthyEmoji = health.AllHealthy ? "✅" : "⚠️";
            sb.AppendLine($"Prometheus up: {promEmoji}");
            sb.AppendLine($"Loki ready: {lokiEmoji}");
            sb.AppendLine($"Firing alerts: {health.FiringAlerts}");
            sb.AppendLine($"Все компоненты healthy: {healthyEmoji}");
            sb.AppendLine();
        }

        // Находки
        if (analysis.Findings.Count > 0)
        {
            sb.AppendLine("## Находки");
            sb.AppendLine();

            foreach (var finding in analysis.Findings)
            {
                sb.AppendLine($"### {finding.Id}: {finding.Title}");
                sb.AppendLine();
                // План 22, этап 1.4: enum → прежний wire-формат строк.
                sb.AppendLine($"**Серьёзность:** {FindingFormat.ToWire(finding.Severity, finding.RawSeverity)} | **Уверенность:** {FindingFormat.ToWire(finding.Confidence, finding.RawConfidence)}");
                sb.AppendLine();
                sb.AppendLine($"**Доказательство:** {finding.Evidence}");
                sb.AppendLine();
                sb.AppendLine($"**Влияние:** {finding.Impact}");
                sb.AppendLine();
                sb.AppendLine($"**Рекомендация:** {finding.Recommendation}");
                sb.AppendLine();
            }
        }

        // Noise candidates
        if (analysis.NoiseCandidates.Count > 0)
        {
            sb.AppendLine("## Возможный шум");
            sb.AppendLine();
            foreach (var noise in analysis.NoiseCandidates)
                sb.AppendLine($"- {noise}");
            sb.AppendLine();
        }

        // Вопросы к владельцу
        if (analysis.QuestionsForOwner.Count > 0)
        {
            sb.AppendLine("## Вопросы к владельцу системы");
            sb.AppendLine();
            foreach (var q in analysis.QuestionsForOwner)
                sb.AppendLine($"- {q}");
            sb.AppendLine();
        }

        // Агентские разделы
        if (agentReport is not null)
        {
            AppendAgentSections(sb, agentReport);
        }

        // Fallback-заметка
        if (analysis.FallbackUsed)
            sb.AppendLine("> ⚠️ LLM вернул ответ без JSON-схемы (fallback). Структура может быть неполной.");

        return sb.ToString();
    }

    /// <summary>
    /// Добавляет разделы агента: гипотезы, прочитанный контекст,
    /// структуру кода и результат применения правок.
    /// </summary>
    private static void AppendAgentSections(StringBuilder sb, AgentReport report)
    {
        if (!string.IsNullOrEmpty(report.Orchestrator))
        {
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## Работа агента");
            sb.AppendLine();
            sb.AppendLine($"**Оркестратор:** {report.Orchestrator}");
            sb.AppendLine($"**Вызовов инструментов:** {report.ToolCalls}");

            if (report.UnavailableSources.Count > 0)
            {
                sb.AppendLine($"**Недоступные источники:** {string.Join(", ", report.UnavailableSources)}");
            }

            sb.AppendLine();
        }

        if (report.Hypotheses.Count > 0)
        {
            sb.AppendLine("### Гипотезы о причине");
            sb.AppendLine();

            foreach (var h in report.Hypotheses)
            {
                sb.AppendLine($"- **[{FindingFormat.ToWire(h.Confidence, h.RawConfidence)}]** {h.RootCause}");
                sb.AppendLine($"  - Доказательства: {h.Evidence}");

                if (h.References.Count > 0)
                {
                    sb.AppendLine($"  - Ссылки: {string.Join(", ", h.References)}");
                }
            }

            sb.AppendLine();
        }

        if (report.ContextDocuments.Count > 0)
        {
            sb.AppendLine("### Прочитанный контекст");
            sb.AppendLine();

            foreach (var doc in report.ContextDocuments)
            {
                sb.AppendLine($"- `{doc.Id}` — {doc.Title} (score {doc.Score:F2}, {doc.Length} симв.)");
            }

            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(report.CodeStructure))
        {
            sb.AppendLine("### Структура кода");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(report.CodeStructure);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(report.FixPlanSummary) ||
            !string.IsNullOrWhiteSpace(report.ChangeMessage))
        {
            sb.AppendLine("### Правки кода");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(report.FixPlanSummary))
            {
                sb.AppendLine($"**План:** {report.FixPlanSummary}");
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(report.ChangeMessage))
            {
                sb.AppendLine($"**Результат:** {report.ChangeMessage}");
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(report.ChangeBranch))
            {
                sb.AppendLine($"**Ветка:** `{report.ChangeBranch}`");
            }

            if (!string.IsNullOrWhiteSpace(report.ChangeCommit))
            {
                sb.AppendLine($"**Коммит:** `{report.ChangeCommit}`");
            }

            if (!string.IsNullOrWhiteSpace(report.PatchPath))
            {
                sb.AppendLine($"**Патч:** `{report.PatchPath}`");
            }

            sb.AppendLine();
            sb.AppendLine("> ⚠️ Push не выполнялся. Мерж — только вручную после проверки.");
            sb.AppendLine();
        }
    }

    private static string GetVerdictEmoji(string verdict) => verdict switch
    {
        "OK" => "✅",
        "WARNING" => "⚠️",
        "CRITICAL" => "🔴",
        _ => "❓"
    };
}
