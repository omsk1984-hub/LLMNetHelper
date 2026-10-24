using System.Text;
using System.Text.Json;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Roslyn;

namespace LLMNetHelper.Chat.Tools;

/// <summary>
/// Инструмент <c>repo_call_graph</c>: кто вызывает метод и что вызывает он —
/// область влияния правки в выбранном репозитории. Требует <c>/repo <имя></c>.
/// </summary>
public sealed class RepoCallGraphTool : IAgentTool
{
    private readonly ChatScope _scope;
    private readonly CallGraphService _graph;

    public RepoCallGraphTool(ChatScope scope, CallGraphService graph)
    {
        _scope = scope;
        _graph = graph;
    }

    public string Name => "repo_call_graph";

    public string Description =>
        "Показывает, кто вызывает метод (callers) и что вызывает он (callees) " +
        "в выбранном репозитории — область влияния правки. Совпадение по имени " +
        "метода (синтаксический анализ), поэтому возможны одноимённые методы. " +
        "Требует /repo <имя>.";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "method": { "type": "string", "description": "Имя метода" },
            "direction": {
              "type": "string",
              "enum": ["callers", "callees", "both"],
              "description": "Направление обхода (по умолчанию both)"
            }
          },
          "required": ["method"]
        }
        """;

    public async Task<string> InvokeAsync(string parametersJson, CancellationToken ct = default)
    {
        if (!_scope.TryGetSolutionPath(out var solutionPath))
        {
            return "Roslyn недоступен: выберите репозиторий командой /repo <имя> " +
                   "(анализ работает только для одного репозитория).";
        }

        string method;
        string direction;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson);
            method = doc.RootElement.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
            direction = doc.RootElement.TryGetProperty("direction", out var d) ? d.GetString() ?? "both" : "both";
        }
        catch (JsonException ex)
        {
            return $"Ошибка разбора аргументов: {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            return "Укажите method.";
        }

        var sb = new StringBuilder();

        if (direction is "callers" or "both")
        {
            var callers = await _graph
                .FindCallersAsync(method, solutionPath, ct)
                .ConfigureAwait(false);

            sb.AppendLine($"Callers ({callers.Count}):");
            foreach (var c in callers)
            {
                sb.AppendLine($"  {c.FullName} — {ToRelative(c.FileName)}:{c.Line}");
            }
        }

        if (direction is "callees" or "both")
        {
            var callees = await _graph
                .FindCalleesAsync(method, solutionPath, ct)
                .ConfigureAwait(false);

            sb.AppendLine($"Callees ({callees.Count}):");
            foreach (var c in callees)
            {
                sb.AppendLine($"  {c}");
            }
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
