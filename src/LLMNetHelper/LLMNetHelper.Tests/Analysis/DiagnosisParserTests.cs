using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Analysis;
using Xunit;

namespace LLMNetHelper.Tests.Analysis;

/// <summary>
/// Тесты разбора диагноза (план 22, этапы 3.2/3.3). Паритет с прежней логикой
/// <c>NativeAgentOrchestrator.TryParseDiagnosis</c>: тот же JSON-формат, те же
/// правила коррекции вердикта (дефект D5), сохранение raw confidence (этап 1.4).
/// </summary>
public class DiagnosisParserTests
{
    private const string Orchestrator = "native";

    [Fact]
    public void TryParse_ValidJson_ReturnsDiagnosis()
    {
        const string text = """
            {
              "verdict": "WARNING",
              "summary": "рост ошибок в Writer",
              "findings": [{ "id": "F1", "severity": "warning", "title": "t", "evidence": "e", "impact": "i", "recommendation": "r", "confidence": "high" }],
              "hypotheses": [
                { "root_cause": "таймаут Finam", "confidence": "high", "evidence": "лог", "references": ["log:1", "log:2"] }
              ],
              "questions_for_owner": ["проверить ключ?"]
            }
            """;

        var diagnosis = DiagnosisParser.TryParse(text, Orchestrator, toolCalls: 2, promptTokens: 10, completionTokens: 5);

        Assert.NotNull(diagnosis);
        Assert.Equal(Verdict.Warning, diagnosis.Verdict);
        Assert.Equal("рост ошибок в Writer", diagnosis.Summary);
        Assert.Single(diagnosis.Findings);
        Assert.Equal("F1", diagnosis.Findings[0].Id);
        Assert.Single(diagnosis.Hypotheses);
        Assert.Equal("таймаут Finam", diagnosis.Hypotheses[0].RootCause);
        Assert.Equal("high", FindingFormat.ToWire(diagnosis.Hypotheses[0].Confidence, diagnosis.Hypotheses[0].RawConfidence));
        Assert.Equal(2, diagnosis.Hypotheses[0].References.Count);
        Assert.Equal("лог", diagnosis.Hypotheses[0].Evidence);
        Assert.Equal(["проверить ключ?"], diagnosis.QuestionsForOwner);
        Assert.Equal(2, diagnosis.ToolCallCount);
        Assert.Equal(10, diagnosis.PromptTokens);
        Assert.Equal(5, diagnosis.CompletionTokens);
        Assert.Equal(Orchestrator, diagnosis.Orchestrator);
    }

    [Fact]
    public void TryParse_UnknownHypothesisConfidence_RawPreserved()
    {
        const string text = """
            {
              "verdict": "OK",
              "summary": "s",
              "hypotheses": [
                { "root_cause": "утечка", "confidence": "almost-sure", "evidence": "e", "references": [] }
              ]
            }
            """;

        var diagnosis = DiagnosisParser.TryParse(text, Orchestrator, 0, null, null);

        Assert.NotNull(diagnosis);
        Assert.Equal(Confidence.Other, diagnosis.Hypotheses[0].Confidence);
        Assert.Equal("almost-sure", diagnosis.Hypotheses[0].RawConfidence);
    }

    [Fact]
    public void TryParse_CriticalFinding_RaisesVerdictToCritical()
    {
        // Вердикт OK, но есть critical находка → коррекция D5 повышает до Critical.
        const string text = """
            {
              "verdict": "OK",
              "summary": "всё хорошо",
              "findings": [{ "id": "F1", "severity": "critical", "title": "t", "evidence": "e", "impact": "i", "recommendation": "r", "confidence": "high" }]
            }
            """;

        var diagnosis = DiagnosisParser.TryParse(text, Orchestrator, 0, null, null);

        Assert.NotNull(diagnosis);
        Assert.Equal(Verdict.Critical, diagnosis.Verdict);
    }

    [Theory]
    [InlineData("Обычный текст")]
    [InlineData("[1,2,3]")]
    [InlineData("{broken")]
    [InlineData("""{"summary":"без вердикта"}""")]
    public void TryParse_NotDiagnosis_ReturnsNull(string text)
    {
        Assert.Null(DiagnosisParser.TryParse(text, Orchestrator, 0, null, null));
    }
}