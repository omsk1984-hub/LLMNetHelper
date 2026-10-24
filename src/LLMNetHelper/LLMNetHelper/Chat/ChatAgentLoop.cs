using System.Text.Json;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Agent.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LLMNetHelper.Chat;

/// <summary>
/// ReAct-цикл чата для режима <c>/tools</c>.
/// <para>
/// Повторяет структуру <c>NativeAgentOrchestrator.DiagnoseAsync</c> (парсинг
/// <c>{"tool":..., "arguments":...}</c>, лимиты шагов/вызовов), но отличается
/// в двух существенных вещах:
/// <list type="bullet">
///   <item>финальный ответ — свободный текст (не JSON-диагноз);</item>
///   <item>модель получает <b>полный текст</b> результата инструмента, а не
///   только число символов: в чате без содержимого файлов цикл бессмысленен.</item>
/// </list>
/// Белый список сохраняется: инструмент ищется через <see cref="ToolRegistry.Find"/>,
/// неизвестное имя отклоняется.
/// </para>
/// </summary>
public sealed class ChatAgentLoop
{
    private readonly IChatClient _chat;
    private readonly ToolRegistry _registry;
    private readonly ChatOptions _options;
    private readonly ILogger<ChatAgentLoop> _logger;
    private readonly IChatTranscript _transcript;

    public ChatAgentLoop(
        IChatClient chat,
        ToolRegistry registry,
        ChatOptions options,
        ILogger<ChatAgentLoop> logger,
        IChatTranscript? transcript = null)
    {
        _chat = chat;
        _registry = registry;
        _options = options;
        _logger = logger;
        _transcript = transcript ?? NullChatTranscript.Instance;
    }

    /// <summary>
    /// Выполняет один вопрос в агентском цикле. Возвращает текст финального ответа.
    /// </summary>
    public async Task<string> RunAsync(List<ChatMessage> history, string question, CancellationToken ct)
    {
        var messages = new List<ChatMessage>(history);
        messages.Add(new ChatMessage(ChatRole.User, question));
        _transcript.LogUserMessage(question);

        var toolCalls = 0;

        for (var step = 0; step < _options.MaxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();

            string? responseText;
            int? inputTokens = null;
            int? outputTokens = null;

            try
            {
                var response = await _chat.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
                responseText = response.Text;

                // Usage из ответа транспорта (OpenAI-совместимый usage.prompt_tokens/
                // completion_tokens): CustomLlmChatClient заполняет UsageDetails.
                // Может отсутствовать (null) — провайдер не вернул токены.
                if (response.Usage is { } usage)
                {
                    inputTokens = usage.InputTokenCount is { } i ? (int)Math.Min(i, int.MaxValue) : null;
                    outputTokens = usage.OutputTokenCount is { } o ? (int)Math.Min(o, int.MaxValue) : null;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Чат: сбой вызова LLM на шаге {Step}", step);
                _transcript.LogNote($"[Сбой LLM на шаге {step + 1}: {ex.Message}]");
                return $"[Сбой LLM: {ex.Message}]";
            }

            if (string.IsNullOrWhiteSpace(responseText))
            {
                _transcript.LogNote("[Модель вернула пустой ответ]");
                return "[Модель вернула пустой ответ]";
            }

            if (!TryParseToolCall(responseText, out var toolName, out var toolArgs))
            {
                // Финальный ответ текстом.
                _transcript.LogAssistantAnswer(responseText, inputTokens, outputTokens);
                return responseText;
            }

            if (toolCalls >= _options.ToolCallLimit)
            {
                _logger.LogWarning("Чат: достигнут лимит вызовов инструментов ({Limit})", _options.ToolCallLimit);
                _transcript.LogModelStep(step, responseText, inputTokens, outputTokens);
                _transcript.LogNote("[Достигнут лимит вызовов инструментов — ответ может быть неполным.]");
                return responseText + "\n\n[Достигнут лимит вызовов инструментов — ответ может быть неполным.]";
            }

            _transcript.LogModelStep(step, responseText, inputTokens, outputTokens);
            _transcript.LogToolCall(step, toolName, toolArgs);

            var (resultText, succeeded) = await InvokeToolAsync(toolName, toolArgs, ct).ConfigureAwait(false);
            toolCalls++;

            _transcript.LogToolResult(step, toolName, resultText, succeeded);

            _logger.LogInformation(
                "Чат: инструмент {Tool}: {Chars} символов, успех={Ok}",
                toolName, resultText.Length, succeeded);

            messages.Add(new ChatMessage(ChatRole.Assistant, responseText));
            messages.Add(new ChatMessage(ChatRole.User,
                succeeded
                    ? $"Результат инструмента `{toolName}`:\n{resultText}"
                    : $"Инструмент `{toolName}` не выполнен: {resultText}. " +
                      "Используй только инструменты из системного промпта."));
        }

        _logger.LogWarning("Чат: достигнут лимит шагов ({Limit})", _options.MaxSteps);
        _transcript.LogNote("[Достигнут лимит шагов агента — переформулируйте вопрос или отключите /tools.]");
        return "[Достигнут лимит шагов агента — переформулируйте вопрос или отключите /tools.]";
    }

    /// <summary>
    /// Вызов инструмента из белого списка с возвратом ПОЛНОГО текста результата
    /// (в отличие от <c>ToolRegistry.InvokeAsync</c>, возвращающего только метрики).
    /// </summary>
    private async Task<(string ResultText, bool Succeeded)> InvokeToolAsync(
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

    /// <summary>
    /// Разбор запроса на вызов инструмента (тот же формат, что у оркестратора):
    /// <c>{"tool":"repo_search","arguments":{"query":"..."}}</c>.
    /// </summary>
    internal static bool TryParseToolCall(string text, out string toolName, out string argumentsJson)
    {
        toolName = "";
        argumentsJson = "{}";

        var json = ExtractJson(text);
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

    /// <summary>Извлекает JSON-объект из ответа модели (убирает markdown-обёртку).</summary>
    internal static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var end = text.LastIndexOf('}');
        if (end <= start)
        {
            return null;
        }

        return text[start..(end + 1)];
    }
}
