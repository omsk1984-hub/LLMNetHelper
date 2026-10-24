using System.Text;
using System.Text.Json;
using LLMNetHelper.Abstractions;

namespace LLMNetHelper.Chat.Tools;

/// <summary>
/// Инструмент <c>repo_search</c>: BM25-поиск по файлам текущей области чата.
/// </summary>
public sealed class RepoSearchTool : IAgentTool
{
    private readonly ChatScope _scope;

    public RepoSearchTool(ChatScope scope) => _scope = scope;

    public string Name => "repo_search";

    public string Description =>
        "Ищет по файлам репозиториев (код, конфиги, документация) и возвращает " +
        "наиболее релевантные фрагменты с путями. Используйте для поиска ответов " +
        "на вопросы о коде, конфигурации, архитектуре.";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "Поисковый запрос: имя класса, метода, конфигурационного ключа, текст ошибки"
            }
          },
          "required": ["query"]
        }
        """;

    public async Task<string> InvokeAsync(string parametersJson, CancellationToken ct = default)
    {
        string query;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson);
            query = doc.RootElement.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        }
        catch (JsonException ex)
        {
            return $"Ошибка разбора аргументов: {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return "Пустой запрос: укажите query.";
        }

        var result = await _scope.SearchAsync(query, ct).ConfigureAwait(false);

        if (result.Documents.Count == 0)
        {
            var unavailable = result.UnavailableSources.Count > 0
                ? $"; недоступные источники: {string.Join(", ", result.UnavailableSources)}"
                : "";
            return $"Ничего не найдено{unavailable}.";
        }

        var sb = new StringBuilder($"Найдено фрагментов: {result.Documents.Count}\n");

        foreach (var doc in result.Documents)
        {
            sb.AppendLine();
            sb.AppendLine($"## {doc.Title} (score {doc.Score:F2})");
            sb.AppendLine(doc.Content);
        }

        return sb.ToString();
    }
}
