using LLMNetHelper.Core.Analysis;

namespace LLMNetHelper.Tests.Llm;

/// <summary>
/// Тесты контракта LLM: проверяем что модели данных корректны.
/// </summary>
public class LlmContractParityTests
{
    [Fact]
    public void LlmCompletion_HasAllProperties()
    {
        var completion = new Abstractions.LlmCompletion(
            Text: "test",
            Json: "{\"verdict\":\"OK\"}",
            PromptTokens: 100,
            CompletionTokens: 50,
            FinishReason: "stop",
            Model: "gpt-4.1-mini",
            FallbackUsed: false,
            Duration: TimeSpan.FromSeconds(5));

        Assert.Equal("test", completion.Text);
        Assert.Equal("{\"verdict\":\"OK\"}", completion.Json);
        Assert.Equal(100, completion.PromptTokens);
        Assert.Equal(50, completion.CompletionTokens);
        Assert.Equal("stop", completion.FinishReason);
        Assert.Equal("gpt-4.1-mini", completion.Model);
        Assert.False(completion.FallbackUsed);
        Assert.Equal(TimeSpan.FromSeconds(5), completion.Duration);
    }

    [Fact]
    public void LlmRequest_Immutability()
    {
        var request = new Abstractions.LlmRequest(
            SystemPrompt: "sys",
            UserPrompt: "user",
            ExpectedJsonSchema: "{}",
            MaxTokens: 4000,
            Temperature: 0.1);

        Assert.Equal("sys", request.SystemPrompt);
        Assert.Equal("user", request.UserPrompt);
        Assert.Equal("{}", request.ExpectedJsonSchema);
        Assert.Equal(4000, request.MaxTokens);
        Assert.Equal(0.1, request.Temperature);
    }

    [Fact]
    public void AnalysisResult_DefaultValues()
    {
        var result = new AnalysisResult();

        Assert.Equal("UNKNOWN", result.Verdict);
        Assert.Equal("", result.Summary);
        Assert.Empty(result.Findings);
        Assert.Empty(result.NoiseCandidates);
        Assert.Empty(result.QuestionsForOwner);
        Assert.False(result.FallbackUsed);
    }

    [Fact]
    public void Finding_AllPropertiesSettable()
    {
        var finding = new Finding
        {
            Id = "F1",
            Severity = "critical",
            Title = "Test",
            Evidence = "test evidence",
            Impact = "test impact",
            Recommendation = "test rec",
            Confidence = "high"
        };

        Assert.Equal("F1", finding.Id);
        Assert.Equal("critical", finding.Severity);
        Assert.Equal("Test", finding.Title);
        Assert.Equal("test evidence", finding.Evidence);
        Assert.Equal("test impact", finding.Impact);
        Assert.Equal("test rec", finding.Recommendation);
        Assert.Equal("high", finding.Confidence);
    }
}
