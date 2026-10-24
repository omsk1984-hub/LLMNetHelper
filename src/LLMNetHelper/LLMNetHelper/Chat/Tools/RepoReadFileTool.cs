using System.Text.Json;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Snapshot;

namespace LLMNetHelper.Chat.Tools;

/// <summary>
/// Инструмент <c>repo_read_file</c>: чтение файла из текущей области чата.
/// Guard: путь должен лежать внутри области; секреты вырезаются <see cref="Redactor"/>.
/// </summary>
public sealed class RepoReadFileTool : IAgentTool
{
    private readonly ChatScope _scope;
    private readonly ChatOptions _options;

    public RepoReadFileTool(ChatScope scope, ChatOptions options)
    {
        _scope = scope;
        _options = options;
    }

    public string Name => "repo_read_file";

    public string Description =>
        "Читает содержимое файла из репозиториев. Путь — относительный от корня " +
        "области (CloneRoot или CloneRoot/<repo>) или абсолютный внутри области. " +
        "Возвращает текст файла (обрезанный по размеру и очищенный от секретов).";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Путь к файлу (относительный от корня области или абсолютный внутри области)"
            },
            "maxChars": {
              "type": "integer",
              "description": "Максимум символов для возврата (по умолчанию — из конфигурации чата)"
            }
          },
          "required": ["path"]
        }
        """;

    public async Task<string> InvokeAsync(string parametersJson, CancellationToken ct = default)
    {
        string path;
        int maxChars;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson);
            path = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
            maxChars = doc.RootElement.TryGetProperty("maxChars", out var m) && m.TryGetInt32(out var v) && v > 0
                ? v
                : _options.MaxChars;
        }
        catch (JsonException ex)
        {
            return $"Ошибка разбора аргументов: {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return "Пустой путь: укажите path.";
        }

        if (!_scope.TryResolveInside(path, out var fullPath))
        {
            return $"Отказ: путь вне допустимой области ({_scope.RootPath}).";
        }

        if (!File.Exists(fullPath))
        {
            return $"Файл не найден: {path}";
        }

        try
        {
            var text = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
            var redacted = Redactor.Redact(text);

            if (redacted.Length > maxChars)
            {
                return redacted[..maxChars] + $"\n… (обрезано, всего {redacted.Length} символов)";
            }

            return redacted;
        }
        catch (IOException ex)
        {
            return $"Ошибка чтения файла: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"Нет доступа к файлу: {ex.Message}";
        }
    }
}
