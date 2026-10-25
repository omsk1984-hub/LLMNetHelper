using LLMNetHelper.Core.Analysis;
using Xunit;

namespace LLMNetHelper.Tests.Analysis;

/// <summary>
/// Тесты разбора плана правки (план 22, этап 3.2). Паритет с прежней логикой
/// <c>NativeAgentOrchestrator.TryParseFixPlan</c>: тот же формат, те же правила
/// пропуска невалидных правок (план 22, этап 1.3 — нераспознанный change_kind).
/// </summary>
public class FixPlanParserTests
{
    [Fact]
    public void TryParse_ValidPlan_RendersAllFields()
    {
        const string text = """
            {
              "rationale": "добавить null-check",
              "requires_manual_review": true,
              "changes": [
                {"file_path":"src/A.cs","symbol_name":"Foo","change_kind":"InsertNullCheck","new_code":"if (x==null) return;","explanation":"защита"}
              ],
              "risks": ["меняет поведение", "иной порядок вызовов"]
            }
            """;

        var plan = FixPlanParser.TryParse(text);

        Assert.NotNull(plan);
        Assert.Equal("добавить null-check", plan.Rationale);
        Assert.True(plan.RequiresManualReview);
        Assert.Single(plan.Changes);
        Assert.Equal("src/A.cs", plan.Changes[0].FilePath);
        Assert.Equal("Foo", plan.Changes[0].SymbolName);
        Assert.Equal("защита", plan.Changes[0].Explanation);
        Assert.Equal(2, plan.Risks.Count);
        Assert.Equal("меняет поведение", plan.Risks[0]);
    }

    [Fact]
    public void TryParse_UnknownChangeKind_SkipsThatChange()
    {
        const string text = """
            {
              "rationale": "r",
              "changes": [
                {"file_path":"src/A.cs","symbol_name":"Foo","change_kind":"ReplaceMethodBody","new_code":"x"},
                {"file_path":"src/B.cs","symbol_name":"Bar","change_kind":"MakeCoffee","new_code":"y"}
              ]
            }
            """;

        var plan = FixPlanParser.TryParse(text);

        Assert.NotNull(plan);
        // Нераспознанный change_kind MakeCoffee → правка пропущена (план 22, этап 1.3).
        Assert.Single(plan.Changes);
        Assert.Equal("src/A.cs", plan.Changes[0].FilePath);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"changes":[]}""")]
    public void TryParse_EmptyPlan_ReturnsNonNullWithNoChanges(string text)
    {
        var plan = FixPlanParser.TryParse(text);

        Assert.NotNull(plan);
        Assert.Empty(plan.Changes);
    }

    [Fact]
    public void TryParse_MissingFilePathAndSymbol_Skipped()
    {
        const string text = """
            {
              "changes": [
                {"symbol_name":"Foo","change_kind":"InsertNullCheck","new_code":"x"},
                {"file_path":"src/B.cs","symbol_name":"Bar","change_kind":"InsertNullCheck","new_code":"y"}
              ]
            }
            """;

        var plan = FixPlanParser.TryParse(text);

        Assert.NotNull(plan);
        // Первая правка без file_path → пропуск; вторая валидна.
        Assert.Single(plan.Changes);
        Assert.Equal("src/B.cs", plan.Changes[0].FilePath);
    }

    [Theory]
    [InlineData("Обычный текст")]
    [InlineData("[1,2,3]")]
    [InlineData("{broken")]
    [InlineData(null)]
    public void TryParse_InvalidText_ReturnsNull(string? text)
    {
        Assert.Null(FixPlanParser.TryParse(text ?? ""));
    }
}