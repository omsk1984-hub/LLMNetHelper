using LLMNetHelper.Core.Analysis;
using Xunit;

namespace LLMNetHelper.Tests.Analysis;

/// <summary>
/// Тесты единого разбора вызова инструмента (план 22, этап 3.2).
/// <para>
/// Паритет с прежними копиями (<c>NativeAgentOrchestrator.TryParseToolCall</c>,
/// <c>ChatAgentLoop.TryParseToolCall</c>): тот же формат, те же правила. Тесты
/// были перенесены из <c>ChatAgentLoopTests</c> и расширены — покрывают обе
/// формы аргументов (объект и строка) и markdown-обёртку.
/// </para>
/// </summary>
public class ToolCallParserTests
{
    [Theory]
    [InlineData("""{"tool":"repo_search","arguments":{"query":"x"}}""", "repo_search")]
    [InlineData("```json\n{\"tool\":\"repo_read_file\",\"arguments\":{\"path\":\"a.cs\"}}\n```", "repo_read_file")]
    [InlineData("""{"tool":"repo_search","arguments":"{\"query\":\"x\"}"}""", "repo_search")]
    public void TryParse_ValidForms_FindsTool(string text, string expectedTool)
    {
        Assert.True(ToolCallParser.TryParse(text, out var name, out var args));
        Assert.Equal(expectedTool, name);
        Assert.False(string.IsNullOrWhiteSpace(args));
    }

    [Theory]
    [InlineData("Обычный текст без JSON")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{broken")]
    public void TryParse_InvalidForms_ReturnsFalse(string text)
    {
        Assert.False(ToolCallParser.TryParse(text, out _, out _));
    }

    [Fact]
    public void TryParse_ObjectArguments_RawJsonPreserved()
    {
        const string text = """{"tool":"x","arguments":{"a":1,"b":{"c":"d"}}}""";

        Assert.True(ToolCallParser.TryParse(text, out var name, out var args));

        Assert.Equal("x", name);
        Assert.Contains("\"c\":\"d\"", args, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_StringArguments_RawJsonPreserved()
    {
        const string text = """{"tool":"x","arguments":"{\"a\":1}"}""";

        Assert.True(ToolCallParser.TryParse(text, out _, out var args));

        Assert.Equal("""{"a":1}""", args);
    }

    [Fact]
    public void TryParse_EmptyToolName_ReturnsFalse()
    {
        Assert.False(ToolCallParser.TryParse("""{"tool":"","arguments":{}}""", out _, out _));
    }

    [Fact]
    public void TryParse_MissingTool_ReturnsFalse()
    {
        Assert.False(ToolCallParser.TryParse("""{"arguments":{}}""", out _, out _));
    }
}