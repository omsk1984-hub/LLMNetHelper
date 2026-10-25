using System.Text.Json;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Reporting;
using LLMNetHelper.Running;

namespace LLMNetHelper.Tests.Reporting;

/// <summary>
/// Сквозные тесты отчётности: Diagnosis оркестратора → AnalysisResult →
/// строки Markdown/JSON отчёта. Закрывают критерий готовности 0.1 плана 18:
/// «ответ LLM с непустым findings → находки присутствуют в AnalysisResult
/// после BuildAnalysis и в строках Markdown/JSON отчёта» (дефект D1).
/// </summary>
public class ReportEndToEndTests
{
    private static Diagnosis DiagnosisWithFindings() => new(
        Verdict: "WARNING",
        Summary: "рост ошибок в Writer",
        Findings:
        [
            new Finding
            {
                Id = "F1",
                Severity = FindingSeverity.Warning,
                Title = "High memory in Writer",
                Evidence = "RSS growing at 100MB/h",
                Impact = "OOM risk",
                Recommendation = "Check for leaks",
                Confidence = Confidence.High
            }
        ],
        Hypotheses: [new Hypothesis("таймаут Finam", Confidence.High, "лог", ["log:1"])],
        QuestionsForOwner: ["проверить ключ?"],
        ToolCallCount: 2,
        Orchestrator: "native");

    private static RunInfo SampleRunInfo() => new()
    {
        Id = 42,
        StartedAt = "2026-01-01T00:00:00.0000000Z",
        WindowFrom = "2026-01-01T00:00:00.0000000Z",
        WindowTo = "2026-01-01T06:00:00.0000000Z",
        Status = "ok",
        Verdict = "WARNING"
    };

    [Fact]
    public void BuildAnalysis_PreservesFindings()
    {
        var analysis = GenerateReportStep.BuildAnalysis(DiagnosisWithFindings());

        Assert.Single(analysis.Findings);
        Assert.Equal("F1", analysis.Findings[0].Id);
        Assert.Equal("High memory in Writer", analysis.Findings[0].Title);
    }

    [Fact]
    public void MarkdownReport_ContainsFindings()
    {
        var analysis = GenerateReportStep.BuildAnalysis(DiagnosisWithFindings());

        var markdown = MarkdownReportRenderer.Render(
            analysis, SampleRunInfo(), "native", null, null, null,
            agentReport: AgentReport.Empty with { Orchestrator = "native" });

        Assert.Contains("## Находки", markdown);
        Assert.Contains("F1: High memory in Writer", markdown);
        Assert.Contains("RSS growing at 100MB/h", markdown);
        Assert.Contains("Check for leaks", markdown);
    }

    [Fact]
    public void JsonReport_ContainsFindingsAsObject()
    {
        var analysis = GenerateReportStep.BuildAnalysis(DiagnosisWithFindings());

        var json = JsonReportRenderer.Render(
            analysis, SampleRunInfo(), "native", null, null, null);

        // Валидный JSON-объект (не строка) с массивом findings.
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        var findings = doc.RootElement.GetProperty("findings");
        Assert.Equal(JsonValueKind.Array, findings.ValueKind);
        Assert.Equal(1, findings.GetArrayLength());
        Assert.Equal("F1", findings[0].GetProperty("Id").GetString());
        Assert.Equal("High memory in Writer", findings[0].GetProperty("Title").GetString());
    }
}
