using System.Text.Json;

namespace LLMNetHelper.Core.Analysis;

/// <summary>
/// Разбор запроса на вызов инструмента из ответа модели (план 22, этап 3.2).
/// <para>
/// Единственная реализация вместо двух копий (<c>NativeAgentOrchestrator</c> и
/// <c>ChatAgentLoop</c>). Формат:
/// <list type="bullet">
///   <item><c>{"tool":"docs_search","arguments":{"query":"..."}}</c>;</item>
///   <item><c>{"tool":"docs_search","arguments":"{\"query\":\"...\"}"}</c>.</item>
/// </list>
/// Размещён в Core: не использует доменных типов, потребители (Agent, Chat)
/// ссылаются на Core.
/// </para>
/// </summary>
public static class ToolCallParser
{
    /// <summary>
    /// Пытается извлечь имя инструмента и JSON-строку аргументов.
    /// Возвращает <c>false</c> при отсутствии корректного объекта, имени или
    /// при не-строковом/не-объектном <c>arguments</c>.
    /// </summary>
    public static bool TryParse(string text, out string toolName, out string argumentsJson)
    {
        toolName = "";
        argumentsJson = "{}";

        var json = JsonText.Extract(text);
        if (json is null)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tool", out var toolProp) || toolProp.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            toolName = toolProp.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return false;
            }

            if (root.TryGetProperty("arguments", out var args))
            {
                argumentsJson = args.ValueKind switch
                {
                    JsonValueKind.String => args.GetString() ?? "{}",
                    JsonValueKind.Object => args.GetRawText(),
                    _ => "{}"
                };
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}