using LLMNetHelper.Agent.Tools;
using LLMNetHelper.Core.Analysis;

namespace LLMNetHelper.Agent.AgentLoop;

/// <summary>
/// Стратегия ReAct-цикла для чата (план 22, этап 3.5): режим <c>/tools</c>.
/// <para>
/// Воспроизводит прежнее поведение <c>ChatAgentLoop.RunAsync</c>:
/// <list type="bullet">
///   <item>финальный ответ — свободный текст (не JSON-диагноз): если ответ не
///   распознан как запрос инструмента, он становится финалом;</item>
///   <item>инструмент ищется через <see cref="ToolRegistry.Find"/> и вызывается
///   напрямую — модель получает ПОЛНЫЙ текст результата (в отличие от
///   оркестратора, который отдаёт подпись с числом символов);</item>
///   <item>лимиты шагов/вызовов и сбои — прежние формулировки.</item>
/// </list>
/// </para>
/// </summary>
public sealed class ChatReActStrategy : IReActStrategy
{
    private readonly ToolRegistry _registry;

    public ChatReActStrategy(ToolRegistry registry)
    {
        _registry = registry;
    }

    public bool TryFinalize(
        string responseText,
        int toolCalls,
        int? promptTokens,
        int? completionTokens,
        int? stepInputTokens,
        int? stepOutputTokens,
        out ReActOutcome outcome)
    {
        if (ToolCallParser.TryParse(responseText, out _, out _))
        {
            // Запрос инструмента — цикл продолжается.
            outcome = null!;
            return false;
        }

        // Свободный текст — финальный ответ.
        outcome = new ReActOutcome(
            responseText,
            null,
            toolCalls,
            promptTokens,
            completionTokens);
        return true;
    }

    public async Task<(string ResultText, bool Succeeded)> InvokeToolAsync(
        string toolName,
        string argumentsJson,
        CancellationToken ct)
    {
        var tool = _registry.Find(toolName);
        if (tool is null)
        {
            return ($"`{toolName}` не найден в белом списке", false);
        }

        try
        {
            var result = await tool.InvokeAsync(argumentsJson ?? "{}", ct).ConfigureAwait(false);
            return (result, true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ($"ошибка: {ex.Message}", false);
        }
    }

    public string FormatToolResult(string toolName, string resultText, bool succeeded) =>
        succeeded
            ? $"Результат инструмента `{toolName}`:\n{resultText}"
            : $"Инструмент `{toolName}` не выполнен: {resultText}. " +
              "Используй только инструменты из системного промпта.";

    public ReActOutcome OnLlmFailure(string message, int toolCalls, int? promptTokens, int? completionTokens, int? stepInputTokens = null, int? stepOutputTokens = null) =>
        new($"[Сбой LLM: {message}]", null, toolCalls, promptTokens, completionTokens);

    public ReActOutcome OnEmptyResponse(int toolCalls, int? promptTokens, int? completionTokens, int? stepInputTokens = null, int? stepOutputTokens = null) =>
        new("[Модель вернула пустой ответ]", null, toolCalls, promptTokens, completionTokens);

    public ReActOutcome OnToolCallLimitReached(string responseText, int toolCalls, int? promptTokens, int? completionTokens, int? stepInputTokens = null, int? stepOutputTokens = null) =>
        new($"{responseText}\n\n[Достигнут лимит вызовов инструментов — ответ может быть неполным.]",
            null, toolCalls, promptTokens, completionTokens);

    public ReActOutcome OnStepLimitReached(int toolCalls, int? promptTokens, int? completionTokens, int? stepInputTokens = null, int? stepOutputTokens = null) =>
        new("[Достигнут лимит шагов агента — переформулируйте вопрос или отключите /tools.]",
            null, toolCalls, promptTokens, completionTokens);
}