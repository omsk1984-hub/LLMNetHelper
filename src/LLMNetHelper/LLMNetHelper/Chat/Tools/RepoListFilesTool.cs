using System.Text;
using System.Text.Json;
using LLMNetHelper.Abstractions;

namespace LLMNetHelper.Chat.Tools;

/// <summary>
/// Инструмент <c>repo_list_files</c>: список файлов в подкаталоге текущей области.
/// Guard: путь должен лежать внутри области.
/// </summary>
public sealed class RepoListFilesTool : IAgentTool
{
    private readonly ChatScope _scope;
    private readonly ChatOptions _options;

    public RepoListFilesTool(ChatScope scope, ChatOptions options)
    {
        _scope = scope;
        _options = options;
    }

    public string Name => "repo_list_files";

    public string Description =>
        "Перечисляет файлы в подкаталоге репозиториев (по умолчанию — корень области). " +
        "Возвращает относительные пути файлов, подходящих под Include-паттерны чата.";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Относительный путь подкаталога внутри области (пусто — корень области)"
            },
            "limit": {
              "type": "integer",
              "description": "Максимум файлов для возврата (по умолчанию — из конфигурации чата)"
            }
          },
          "required": []
        }
        """;

    public Task<string> InvokeAsync(string parametersJson, CancellationToken ct = default)
    {
        string? subPath;
        int limit;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson);
            subPath = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() : null;
            limit = doc.RootElement.TryGetProperty("limit", out var l) && l.TryGetInt32(out var v) && v > 0
                ? v
                : _options.ListLimit;
        }
        catch (JsonException ex)
        {
            return Task.FromResult($"Ошибка разбора аргументов: {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(subPath) && !_scope.TryResolveInside(subPath, out _))
        {
            return Task.FromResult($"Отказ: путь вне допустимой области ({_scope.RootPath}).");
        }

        var files = _scope.ListFiles(subPath, limit, ct);

        if (files.Count == 0)
        {
            return Task.FromResult("Файлы не найдены (проверьте путь или Include-паттерны).");
        }

        var sb = new StringBuilder($"Найдено файлов: {files.Count}");
        if (files.Count >= limit)
        {
            sb.Append($" (лимит {limit})");
        }

        sb.AppendLine();

        foreach (var f in files)
        {
            sb.AppendLine(f.RelativePath);
        }

        return Task.FromResult(sb.ToString());
    }
}
