using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Analysis;
using LLMNetHelper.Core.Collection;
using LLMNetHelper.Core.Reporting;
using Xunit;

namespace LLMNetHelper.Tests.Reporting;

/// <summary>
/// Тесты Markdown-рендерера отчёта (план 22, этап 6.7): покрывают риск-хотспот
/// <c>MarkdownReportRenderer.Render</c> (CC=16) — все условные секции: health,
/// шум, вопросы, агентские блоки (гипотезы, контекст, структура кода, правки)
/// и fallback-заметку. Сквозной минимум находок уже закрыт
/// <c>ReportEndToEndTests</c>/<c>ReportContractTests</c>, здесь — ветвление.
/// </summary>
public class MarkdownReportRendererTests
{
    private static readonly DateTime StartedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowTo = new(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc);

    private static readonly RunInfo Run = new()
    {
        Id = 42,
        StartedAt = StartedAt,
        WindowFrom = StartedAt,
        WindowTo = WindowTo,
        Status = RunStatus.Ok,
        Verdict = Verdict.Warning
    };

    private static AnalysisResult Analysis(
        Verdict verdict = Verdict.Warning,
        IReadOnlyList<Finding>? findings = null,
        IReadOnlyList<string>? noise = null,
        IReadOnlyList<string>? questions = null,
        bool fallbackUsed = false) => new()
    {
        Verdict = verdict,
        Summary = "рост ошибок в Writer",
        Findings = findings?.ToList() ?? [],
        NoiseCandidates = noise?.ToList() ?? [],
        QuestionsForOwner = questions?.ToList() ?? [],
        FallbackUsed = fallbackUsed
    };

    private static string Render(
        AnalysisResult analysis,
        MonitoringHealthData? health = null,
        AgentReport? agentReport = null,
        int? promptTokens = null,
        int? completionTokens = null) =>
        new MarkdownReportRenderer().Render(
            analysis, Run, "native", promptTokens, completionTokens, health, agentReport);

    // ---------- Шапка и вердикты ----------

    [Theory]
    [InlineData(Verdict.Ok, "✅", "OK")]
    [InlineData(Verdict.Warning, "⚠️", "WARNING")]
    [InlineData(Verdict.Critical, "🔴", "CRITICAL")]
    [InlineData(Verdict.Unknown, "❓", "UNKNOWN")]
    public void Render_VerdictHeader_EmojiAndWireString(Verdict verdict, string emoji, string wire)
    {
        var markdown = Render(Analysis(verdict));

        Assert.Contains($"**Вердикт:** {emoji} {wire}", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_HeaderCarriesRunMetadata()
    {
        var markdown = Render(Analysis(), promptTokens: 100, completionTokens: 20);

        Assert.Contains("**Запуск:** 2026-01-01T00:00:00.0000000Z UTC", markdown, StringComparison.Ordinal);
        Assert.Contains("**Окно:** 2026-01-01T00:00:00.0000000Z — 2026-01-01T06:00:00.0000000Z", markdown, StringComparison.Ordinal);
        Assert.Contains("**LLM-провайдер:** native", markdown, StringComparison.Ordinal);
        // Оба токена обязательны, иначе строка не выводится (этап 0.5/N4).
        Assert.Contains("**Токены:** 100 в → 20 в", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_PartialTokens_TokenLineOmitted()
    {
        var markdown = Render(Analysis(), promptTokens: 100, completionTokens: null);

        Assert.DoesNotContain("**Токены:**", markdown, StringComparison.Ordinal);
    }

    // ---------- Опциональные секции ----------

    [Fact]
    public void Render_NoOptionalData_SectionsOmitted()
    {
        var markdown = Render(Analysis());

        Assert.Contains("## TL;DR", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("## Состояние мониторинга", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("## Находки", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("## Возможный шум", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("## Вопросы к владельцу системы", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("## Работа агента", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("fallback", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_HealthBlock_AllHealthy()
    {
        var health = new MonitoringHealthData
        {
            PrometheusUp = true,
            LokiReady = true,
            FiringAlerts = 0,
            LastDotnetMetricAge = TimeSpan.FromHours(1)
        };

        var markdown = Render(Analysis(), health: health);

        Assert.Contains("## Состояние мониторинга", markdown, StringComparison.Ordinal);
        Assert.Contains("Prometheus up: ✅", markdown, StringComparison.Ordinal);
        Assert.Contains("Loki ready: ✅", markdown, StringComparison.Ordinal);
        Assert.Contains("Firing alerts: 0", markdown, StringComparison.Ordinal);
        Assert.Contains("Все компоненты healthy: ✅", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_HealthBlock_Unhealthy()
    {
        var health = new MonitoringHealthData
        {
            PrometheusUp = false,
            LokiReady = true,
            FiringAlerts = 3,
            LastDotnetMetricAge = TimeSpan.FromHours(9)
        };

        var markdown = Render(Analysis(), health: health);

        Assert.Contains("Prometheus up: ❌", markdown, StringComparison.Ordinal);
        Assert.Contains("Firing alerts: 3", markdown, StringComparison.Ordinal);
        // AllHealthy = false при возрасте метрики > 6h (этап 1.6).
        Assert.Contains("Все компоненты healthy: ⚠️", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_NoiseAndQuestions_RenderedAsLists()
    {
        var markdown = Render(Analysis(
            noise: ["повторяющийся таймаут Finam", "шум health-пробы"],
            questions: ["проверить ключ Finam?"]));

        Assert.Contains("## Возможный шум", markdown, StringComparison.Ordinal);
        Assert.Contains("- повторяющийся таймаут Finam", markdown, StringComparison.Ordinal);
        Assert.Contains("- шум health-пробы", markdown, StringComparison.Ordinal);
        Assert.Contains("## Вопросы к владельцу системы", markdown, StringComparison.Ordinal);
        Assert.Contains("- проверить ключ Finam?", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_FallbackUsed_NoteAppended()
    {
        var markdown = Render(Analysis(fallbackUsed: true));

        Assert.Contains("LLM вернул ответ без JSON-схемы (fallback)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_FindingBlock_RendersAllFieldsWithWireEnums()
    {
        var finding = new Finding
        {
            Id = "F7",
            Severity = FindingSeverity.Critical,
            Title = "Out of memory",
            Evidence = "RSS 95%",
            Impact = "Crash",
            Recommendation = "Restart",
            Confidence = Confidence.High
        };

        var markdown = Render(Analysis(findings: [finding]));

        Assert.Contains("### F7: Out of memory", markdown, StringComparison.Ordinal);
        Assert.Contains("**Серьёзность:** critical | **Уверенность:** high", markdown, StringComparison.Ordinal);
        Assert.Contains("**Доказательство:** RSS 95%", markdown, StringComparison.Ordinal);
        Assert.Contains("**Влияние:** Crash", markdown, StringComparison.Ordinal);
        Assert.Contains("**Рекомендация:** Restart", markdown, StringComparison.Ordinal);
    }

    // ---------- Агентские разделы ----------

    [Fact]
    public void Render_AgentReportHeader_OrchestratorAndCalls()
    {
        var report = AgentReport.Empty with
        {
            Orchestrator = "native",
            ToolCalls = 4,
            UnavailableSources = ["confluence"]
        };

        var markdown = Render(Analysis(), agentReport: report);

        Assert.Contains("## Работа агента", markdown, StringComparison.Ordinal);
        Assert.Contains("**Оркестратор:** native", markdown, StringComparison.Ordinal);
        Assert.Contains("**Вызовов инструментов:** 4", markdown, StringComparison.Ordinal);
        Assert.Contains("**Недоступные источники:** confluence", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_AgentReportEmptyOrchestrator_SectionOmitted()
    {
        var markdown = Render(Analysis(), agentReport: AgentReport.Empty);

        Assert.DoesNotContain("## Работа агента", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_HypothesesWithReferences_Rendered()
    {
        var report = AgentReport.Empty with
        {
            Orchestrator = "native",
            Hypotheses =
            [
                new Hypothesis("таймаут Finam", Confidence.High, "лог", ["log:1", "log:2"]),
                new Hypothesis("утечка", Confidence.Other, "метрика", [], "almost-sure")
            ]
        };

        var markdown = Render(Analysis(), agentReport: report);

        Assert.Contains("### Гипотезы о причине", markdown, StringComparison.Ordinal);
        Assert.Contains("- **[high]** таймаут Finam", markdown, StringComparison.Ordinal);
        Assert.Contains("  - Доказательства: лог", markdown, StringComparison.Ordinal);
        Assert.Contains("  - Ссылки: log:1, log:2", markdown, StringComparison.Ordinal);
        // Вариант (б) этапа 1.4: нераспознанный confidence — исходная строка.
        Assert.Contains("- **[almost-sure]** утечка", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_HypothesisWithoutReferences_SkipLineOmitted()
    {
        var report = AgentReport.Empty with
        {
            Orchestrator = "native",
            Hypotheses = [new Hypothesis("причина", Confidence.Low, "доказательство", [])]
        };

        var markdown = Render(Analysis(), agentReport: report);

        Assert.Contains("- **[low]** причина", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Ссылки:", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ContextDocuments_RenderedWithScoreAndLength()
    {
        var report = AgentReport.Empty with
        {
            Orchestrator = "native",
            ContextDocuments = [new ContextDocument("local-folder", "docs/runbook.md", "Runbook", "abcde", 0.5)]
        };

        var markdown = Render(Analysis(), agentReport: report);

        // Числовое значение score не проверяется по строке: прод рендерит :F2 в
        // текущей культуре (ru-RU → запятая), см. замечание о культуре в плане 22.
        Assert.Contains("### Прочитанный контекст", markdown, StringComparison.Ordinal);
        Assert.Contains("- `docs/runbook.md` — Runbook (score ", markdown, StringComparison.Ordinal);
        Assert.Contains(", 5 симв.)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_CodeStructure_FencedBlock()
    {
        var report = AgentReport.Empty with { Orchestrator = "native", CodeStructure = "Project: 3 types" };

        var markdown = Render(Analysis(), agentReport: report);

        Assert.Contains("### Структура кода", markdown, StringComparison.Ordinal);
        // AppendLine даёт CRLF на Windows — нормализуем перед поиском блока кода.
        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("```\nProject: 3 types\n```", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ChangeOutcome_AllFieldsAndPushWarning()
    {
        var report = AgentReport.Empty with
        {
            Orchestrator = "native",
            FixPlanSummary = "добавить null-check",
            ChangeMessage = "коммит создан",
            ChangeBranch = "agent/run-42",
            ChangeCommit = "abcdef123",
            PatchPath = "reports/patch.diff"
        };

        var markdown = Render(Analysis(), agentReport: report);

        Assert.Contains("### Правки кода", markdown, StringComparison.Ordinal);
        Assert.Contains("**План:** добавить null-check", markdown, StringComparison.Ordinal);
        Assert.Contains("**Результат:** коммит создан", markdown, StringComparison.Ordinal);
        Assert.Contains("**Ветка:** `agent/run-42`", markdown, StringComparison.Ordinal);
        Assert.Contains("**Коммит:** `abcdef123`", markdown, StringComparison.Ordinal);
        Assert.Contains("**Патч:** `reports/patch.diff`", markdown, StringComparison.Ordinal);
        Assert.Contains("Push не выполнялся", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ChangeOutcomePartialFields_OnlyPresentLines()
    {
        var report = AgentReport.Empty with
        {
            Orchestrator = "native",
            ChangeMessage = "изменений нет"
        };

        var markdown = Render(Analysis(), agentReport: report);

        Assert.Contains("### Правки кода", markdown, StringComparison.Ordinal);
        Assert.Contains("**Результат:** изменений нет", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("**План:**", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("**Ветка:**", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("**Коммит:**", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("**Патч:**", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_NoChangeFields_ChangeSectionOmitted()
    {
        var report = AgentReport.Empty with { Orchestrator = "native" };

        var markdown = Render(Analysis(), agentReport: report);

        Assert.DoesNotContain("### Правки кода", markdown, StringComparison.Ordinal);
    }
}
