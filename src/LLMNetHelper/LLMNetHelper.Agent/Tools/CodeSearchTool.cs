using System.Text;
using System.Text.Json;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Roslyn;

namespace LLMNetHelper.Agent.Tools;

/// <summary>
/// Поиск символов в коде решения через Roslyn.
/// </summary>
public sealed class CodeSearchTool : IAgentTool
{
    private readonly CodeSearchService _search;

    public CodeSearchTool(CodeSearchService search)
    {
        _search = search;
    }

    /// <inheritdoc />
    public string Name => "code_search_symbol";

    /// <inheritdoc />
    public string Description =>
        "Ищет типы, методы и свойства в исходном коде решения по подстроке имени. " +
        "Возвращает файл и строку — используйте их в code_read_member.";

    /// <inheritdoc />
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

    /// <inheritdoc />
    public async Task<string> InvokeAsync(string parametersJson, CancellationToken ct = default)
    {
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

        var symbols = await _search.SearchAsync(name, ct).ConfigureAwait(false);

        if (symbols.Count == 0)
        {
            return "Символы не найдены (анализ кода может быть отключён).";
        }

        var sb = new StringBuilder($"Найдено: {symbols.Count}\n");

        foreach (var symbol in symbols)
        {
            sb.AppendLine($"{symbol.Kind} {symbol.FullName} — {symbol.FileName}:{symbol.Line}");
        }

        return sb.ToString();
    }
}

/// <summary>
/// Чтение исходного текста члена.
/// </summary>
public sealed class CodeReadTool : IAgentTool
{
    private readonly CodeReader _reader;

    public CodeReadTool(CodeReader reader)
    {
        _reader = reader;
    }

    /// <inheritdoc />
    public string Name => "code_read_member";

    /// <inheritdoc />
    public string Description =>
        "Читает исходный код метода, начиная с указанного файла и строки. " +
        "Используйте координаты из code_search_symbol.";

    /// <inheritdoc />
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "file": { "type": "string", "description": "Путь к файлу" },
            "line": { "type": "integer", "description": "Номер строки (1-based)" }
          },
          "required": ["file", "line"]
        }
        """;

    /// <inheritdoc />
    public async Task<string> InvokeAsync(string parametersJson, CancellationToken ct = default)
    {
        string file;
        int line;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson);
            file = doc.RootElement.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "";
            line = doc.RootElement.TryGetProperty("line", out var l) && l.TryGetInt32(out var v) ? v : 0;
        }
        catch (JsonException ex)
        {
            return $"Ошибка разбора аргументов: {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(file) || line <= 0)
        {
            return "Укажите file и line (> 0).";
        }

        var snippet = await _reader.ReadAsync(file, line, ct).ConfigureAwait(false);

        return snippet is null
            ? $"Не удалось прочитать {file}:{line}"
            : snippet.Text;
    }
}

/// <summary>
/// Поиск вызывающих и вызываемых методов.
/// </summary>
public sealed class CodeCallGraphTool : IAgentTool
{
    private readonly CallGraphService _graph;

    public CodeCallGraphTool(CallGraphService graph)
    {
        _graph = graph;
    }

    /// <inheritdoc />
    public string Name => "code_call_graph";

    /// <inheritdoc />
    public string Description =>
        "Показывает, кто вызывает метод (callers) и что вызывает он (callees). " +
        "Помогает понять область влияния правки.";

    /// <inheritdoc />
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "method": { "type": "string", "description": "Имя метода" },
            "direction": {
              "type": "string",
              "enum": ["callers", "callees", "both"],
              "description": "Направление обхода"
            }
          },
          "required": ["method"]
        }
        """;

    /// <inheritdoc />
    public async Task<string> InvokeAsync(string parametersJson, CancellationToken ct = default)
    {
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
            var callers = await _graph.FindCallersAsync(method, ct).ConfigureAwait(false);
            sb.AppendLine($"Callers ({callers.Count}):");
            foreach (var c in callers)
            {
                sb.AppendLine($"  {c.FullName} — {c.FileName}:{c.Line}");
            }
        }

        if (direction is "callees" or "both")
        {
            var callees = await _graph.FindCalleesAsync(method, ct).ConfigureAwait(false);
            sb.AppendLine($"Callees ({callees.Count}):");
            foreach (var c in callees)
            {
                sb.AppendLine($"  {c}");
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Поиск проблемных мест в коде (пустые catch, catch(Exception) без логов, throw null).
/// </summary>
public sealed class CodeFailurePointsTool : IAgentTool
{
    private readonly FailurePointAnalyzer _analyzer;

    public CodeFailurePointsTool(FailurePointAnalyzer analyzer)
    {
        _analyzer = analyzer;
    }

    /// <inheritdoc />
    public string Name => "code_failure_points";

    /// <inheritdoc />
    public string Description =>
        "Находит потенциальные источники сбоев: пустой catch, catch(Exception) без логирования, throw null.";

    /// <inheritdoc />
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "file_filter": {
              "type": "string",
              "description": "Необязательный фильтр по части пути файла"
            }
          }
        }
        """;

    /// <inheritdoc />
    public async Task<string> InvokeAsync(string parametersJson, CancellationToken ct = default)
    {
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

        var points = await _analyzer.AnalyzeAsync(filter, ct).ConfigureAwait(false);

        if (points.Count == 0)
        {
            return "Проблемных мест не найдено (или анализ кода отключён).";
        }

        var sb = new StringBuilder($"Найдено: {points.Count}\n");

        foreach (var p in points)
        {
            sb.AppendLine($"[{p.Kind}] {p.FileName}:{p.Line} в {p.ContainingMember} — {p.Snippet}");
        }

        return sb.ToString();
    }
}
