using System.Text;
using System.Text.Json;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Roslyn;

namespace LLMNetHelper.Chat.Tools;

/// <summary>
/// Инструмент <c>repo_failure_points</c>: детерминированный поиск потенциальных
/// источников сбоев (пустой catch, catch без логирования, throw null) в решении
/// выбранного репозитория. Доступен только после <c>/repo <имя></c>.
/// </summary>
public sealed class RepoFailurePointsTool : IAgentTool
{
    /// <summary>Максимум находок в выводе (бюджет токенов).</summary>
    internal const int MaxResults = 30;

    private readonly ChatScope _scope;
    private readonly SolutionLoader _loader;
    private readonly FailurePointAnalyzer _analyzer;

    public RepoFailurePointsTool(ChatScope scope, SolutionLoader loader, FailurePointAnalyzer analyzer)
    {
        _scope = scope;
        _loader = loader;
        _analyzer = analyzer;
    }

    public string Name => "repo_failure_points";

    public string Description =>
        "Находит потенциальные источники сбоев в коде выбранного репозитория: " +
        "пустой catch, catch без логирования, throw null. Возвращает файл:строку " +
        "и имя члена. Требует /repo <имя>; анализ детерминированный (Roslyn).";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "file_filter": {
              "type": "string",
              "description": "Необязательная подстрока пути файла"
            }
          }
        }
        """;

    public async Task<string> InvokeAsync(string parametersJson, CancellationToken ct = default)
    {
        if (!_scope.TryGetSolutionPath(out var solutionPath))
        {
            return "Roslyn недоступен: выберите репозиторий командой /repo <имя> " +
                   "(анализ работает только для одного репозитория).";
        }

        string? filter = null;
        if (!string.IsNullOrWhiteSpace(parametersJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(parametersJson);
                if (doc.RootElement.TryGetProperty("file_filter", out var f))
                {
                    filter = f.GetString();
                }
            }
            catch (JsonException ex)
            {
                return $"Ошибка разбора аргументов: {ex.Message}";
            }
        }

        var solution = await _loader.LoadAsync(solutionPath, ct).ConfigureAwait(false);
        if (solution is null)
        {
            return $"Не удалось загрузить решение {Path.GetFileName(solutionPath)} " +
                   "(проверьте Roslyn:Enabled и наличие MSBuild SDK).";
        }

        var points = await _analyzer.AnalyzeAsync(solution, filter, ct).ConfigureAwait(false);

        if (points.Count == 0)
        {
            return "Проблемных мест не найдено.";
        }

        var truncated = points.Count > MaxResults;
        var sb = new StringBuilder($"Найдено: {points.Count}" +
            (truncated ? $" (показаны первые {MaxResults})" : "") + "\n");

        foreach (var p in points.Take(MaxResults))
        {
            // Guard: относительный путь внутри области чата (наружу — никогда).
            var display = ToRelative(p.FileName);
            sb.AppendLine($"[{p.Type}] {display}:{p.Line} в {p.Member} — {p.Description}");
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
