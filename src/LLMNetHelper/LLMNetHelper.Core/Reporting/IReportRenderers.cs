using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Analysis;
using LLMNetHelper.Core.Collection;

namespace LLMNetHelper.Core.Reporting;

/// <summary>
/// Контракт Markdown-рендерера отчёта (план 22, этап 6.2). Извлечён из
/// статического <c>MarkdownReportRenderer</c>, чтобы шаг отчёта зависел от
/// абстракции: формат подменяется в тестах и верционируется без правки вызывающего кода.
/// <para>
/// Размещён в Core.Reporting (не в Abstractions): сигнатура использует типы
/// <c>Core.Analysis</c>/<c>Core.Collection</c> (<see cref="AnalysisResult"/>,
/// <see cref="MonitoringHealthData"/>), их утечка в общий слой нарушила бы границы.
/// </para>
/// </summary>
public interface IMarkdownReportRenderer
{
    /// <summary>Формирует Markdown-отчёт из результатов анализа.</summary>
    string Render(
        AnalysisResult analysis,
        RunInfo runInfo,
        string llmProvider,
        int? promptTokens,
        int? completionTokens,
        MonitoringHealthData? health,
        AgentReport? agentReport = null);
}

/// <summary>
/// Контракт JSON-рендерера отчёта (план 22, этап 6.2). Извлечён из
/// статического <c>JsonReportRenderer</c>.
/// </summary>
public interface IJsonReportRenderer
{
    /// <summary>Формирует JSON-отчёт из результатов анализа (готовая строка).</summary>
    string Render(
        AnalysisResult analysis,
        RunInfo runInfo,
        string llmProvider,
        int? promptTokens,
        int? completionTokens,
        MonitoringHealthData? health);
}
