using System.Text.Json;
using ArtasTools.MonitorAgent.Abstractions;
using ArtasTools.MonitorAgent.Context;

namespace ArtasTools.MonitorAgent.Agent.Tools;

/// <summary>
/// Инструмент поиска по внешней документации.
/// Обходит <b>все</b> зарегистрированные <see cref="IContextSource"/>,
/// поэтому добавление Confluence/Jira не требует правок здесь.
/// </summary>
public sealed class DocsSearchTool : IAgentTool
{
    private readonly ContextAggregator _aggregator;

    public DocsSearchTool(ContextAggregator aggregator)
    {
        _aggregator = aggregator;
    }

    /// <inheritdoc />
    public string Name => "docs_search";

    /// <inheritdoc />
    public string Description =>
        "Ищет в документации проекта (runbooks, архитектурные решения, ADR) фрагменты, " +
        "относящиеся к описанной проблеме. Возвращает найденные фрагменты с путями.";

    /// <inheritdoc />
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "Поисковый запрос: текст ошибки, имя модуля, название метрики"
            }
          },
          "required": ["query"]
        }
        """;

    /// <inheritdoc />
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

        var result = await _aggregator.SearchAsync(new ContextQuery(query), ct).ConfigureAwait(false);

        if (result.Documents.Count == 0)
        {
            var unavailable = result.UnavailableSources.Count > 0
                ? $"; недоступные источники: {string.Join(", ", result.UnavailableSources)}"
                : "";
            return $"Ничего не найдено{unavailable}.";
        }

        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"Найдено документов: {result.Documents.Count}");

        foreach (var doc in result.Documents)
        {
            builder.AppendLine();
            builder.AppendLine($"## {doc.Title} (score {doc.Score:F2})");
            builder.AppendLine(doc.Content);
        }

        return builder.ToString();
    }
}
