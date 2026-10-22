using LLMNetHelper.Core.Analysis;

namespace LLMNetHelper.Tests.Analysis;

public class ResponseParserTests
{
    [Fact]
    public void Parse_ValidJson_ReturnsAnalysis()
    {
        var json = @"{
            ""verdict"": ""WARNING"",
            ""summary"": ""Some issues found"",
            ""findings"": [{
                ""id"": ""F1"",
                ""severity"": ""warning"",
                ""title"": ""High memory"",
                ""evidence"": ""RSS growing at 100MB/h"",
                ""impact"": ""OOM risk"",
                ""recommendation"": ""Check for leaks"",
                ""confidence"": ""high""
            }],
            ""noise_candidates"": [],
            ""questions_for_owner"": []
        }";

        var result = ResponseParser.Parse(json, null, false);

        Assert.Equal("WARNING", result.Verdict);
        Assert.Equal("Some issues found", result.Summary);
        Assert.Single(result.Findings);
        Assert.Equal("F1", result.Findings[0].Id);
        Assert.False(result.FallbackUsed);
    }

    [Fact]
    public void Parse_FencedJsonBlock_ExtractsFromText()
    {
        var text = @"Here is the analysis:

```json
{
    ""verdict"": ""CRITICAL"",
    ""summary"": ""Server down"",
    ""findings"": [],
    ""noise_candidates"": [],
    ""questions_for_owner"": []
}
```";

        var result = ResponseParser.Parse(null, text, false);

        Assert.Equal("CRITICAL", result.Verdict);
        Assert.Equal("Server down", result.Summary);
    }

    [Fact]
    public void Parse_MarkdownFallback_ReturnsUnknown()
    {
        var text = "The server seems to be having issues with memory.";

        var result = ResponseParser.Parse(null, text, true);

        Assert.Equal("UNKNOWN", result.Verdict);
        Assert.Single(result.Findings);
        Assert.Equal("low", result.Findings[0].Confidence);
    }

    [Fact]
    public void Parse_CorrectsVerdict_WhenMismatched()
    {
        // Есть critical finding, но verdict OK — должен быть повышен
        var json = @"{
            ""verdict"": ""OK"",
            ""summary"": ""All fine"",
            ""findings"": [{
                ""id"": ""F1"",
                ""severity"": ""critical"",
                ""title"": ""Out of memory"",
                ""evidence"": ""RSS at 95%"",
                ""impact"": ""Crash imminent"",
                ""recommendation"": ""Restart"",
                ""confidence"": ""high""
            }],
            ""noise_candidates"": [],
            ""questions_for_owner"": []
        }";

        var result = ResponseParser.Parse(json, null, false);

        Assert.Equal("CRITICAL", result.Verdict); // Понижен/повышен кодом
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsUnknown()
    {
        var result = ResponseParser.Parse(null, null, false);

        Assert.Equal("UNKNOWN", result.Verdict);
        Assert.Empty(result.Findings);
    }
}
