using LLMNetHelper.Abstractions;
using LLMNetHelper.Chat;

namespace LLMNetHelper.Tests.Chat;

public class ChatPromptBuilderTests
{
    // План 22, этап 6.1: ChatPromptBuilder — экземплярный класс (IChatPromptBuilder).
    private static readonly ChatPromptBuilder Builder = new();

    [Fact]
    public void SystemPrompt_ContainsScopeAndCitationRules()
    {
        var prompt = Builder.BuildSystemPrompt("все репозитории (C:\\repos\\GPM)", withTools: false);

        Assert.Contains("ассистент", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("C:\\repos\\GPM", prompt, StringComparison.Ordinal);
        Assert.Contains("путь/к/файлу", prompt, StringComparison.Ordinal);
        Assert.Contains("ТОЛЬКО на основе предоставленных фрагментов", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemPrompt_WithoutTools_HasNoToolSection()
    {
        var prompt = Builder.BuildSystemPrompt("область", withTools: false);

        Assert.DoesNotContain("repo_search", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Инструменты", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemPrompt_WithTools_IncludesDescriptionsAndJsonFormat()
    {
        var tools = "- `repo_search`: ищет по файлам\n- `repo_read_file`: читает файл\n";
        var prompt = Builder.BuildSystemPrompt("область", withTools: true, toolDescriptions: tools);

        Assert.Contains("repo_search", prompt, StringComparison.Ordinal);
        Assert.Contains("{\"tool\":", prompt, StringComparison.Ordinal);
        Assert.Contains("\"arguments\"", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void UserPrompt_ContainsQuestionAndDocuments()
    {
        var docs = new List<ContextDocument>
        {
            new("local-folder", "alpha/Program.cs#0", "alpha/Program.cs — Program", "class Program { }", 1.5),
            new("local-folder", "beta/README.md#0", "beta/README.md — README", "# Beta сервис", 0.9)
        };

        var prompt = Builder.BuildUserPrompt("Что делает Program?", docs);

        Assert.Contains("Что делает Program?", prompt, StringComparison.Ordinal);
        Assert.Contains("class Program { }", prompt, StringComparison.Ordinal);
        Assert.Contains("alpha/Program.cs — Program", prompt, StringComparison.Ordinal);
        Assert.Contains("Фрагменты из репозиториев", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void UserPrompt_NoDocuments_StatesNothingFound()
    {
        var prompt = Builder.BuildUserPrompt("вопрос", []);

        Assert.Contains("ничего не найдено", prompt, StringComparison.Ordinal);
        Assert.Contains("вопрос", prompt, StringComparison.Ordinal);
    }
}
