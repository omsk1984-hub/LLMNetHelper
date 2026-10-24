using System.Text;
using System.Text.Json;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Roslyn;

namespace LLMNetHelper.Chat.Tools;

/// <summary>
/// Инструмент <c>repo_symbol_search</c>: точные координаты объявлений типов и членов
/// в решении выбранного репозитория. Требует <c>/repo <имя></c>.
/// </summary>
public sealed class RepoSymbolSearchTool : IAgentTool
{
    private readonly ChatScope _scope;
    private readonly CodeSearchService _search;

    public RepoSymbolSearchTool(ChatScope scope, CodeSearchService search)
    {
        _scope = scope;
        _search = search;
    }

    public string Name => "repo_symbol_search";

    public string Description =>
        "Ищет объявления типов, методов и свойств в выбранном репозитории по подстроке " +
        "имени и возвращает точные координаты файл:строка. В отличие от repo_search " +
        "(BM25 по фрагментам), даёт место объявления, а не релевантные чанки. " +
        "Требует /repo <имя>.";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "name": {
              "type": "string",
              "description": "Подстрока имени символа, например ProcessOrder или Writer"
            }
          },
          "required": ["name"]
        }
        """;

    public async Task<string> InvokeAsync(string parametersJson, CancellationToken ct = default)
    {
        if (!_scope.TryGetSolutionPath(out var solutionPath))
        {
            return "Roslyn недоступен: выберите репозиторий командой /repo <имя> " +
                   "(анализ работает только для одного репозитория).";
        }

        string name;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson);
            name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        }
        catch (JsonException ex)
        {
            return $"Ошибка разбора аргументов: {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return "Пустой запрос: укажите name.";
        }

        var symbols = await _search.SearchAsync(name, solutionPath, ct).ConfigureAwait(false);

        if (symbols.Count == 0)
        {
            return "Символы не найдены.";
        }

        var sb = new StringBuilder($"Найдено: {symbols.Count}\n");

        foreach (var symbol in symbols)
        {
            sb.AppendLine($"{symbol.Kind} {symbol.FullName} — {ToRelative(symbol.FileName)}:{symbol.Line}");
        }

        return sb.ToString();
    }

    private string ToRelative(string filePath)
    {
        return _scope.TryResolveInside(filePath, out _)
            ? Path.GetRelativePath(_scope.RootPath, filePath)
            : filePath;
    }
}
