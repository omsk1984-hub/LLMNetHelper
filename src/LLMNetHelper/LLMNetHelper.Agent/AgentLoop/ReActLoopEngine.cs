using LLMNetHelper.Core.Analysis;
using Microsoft.Extensions.AI;

namespace LLMNetHelper.Agent.AgentLoop;

/// <summary>
/// Общий ReAct-цикл (план 22, этап 3.4). Выполняет итерации «модель → ответ →
/// (финал | вызов инструмента)», применяя переданную стратегию для трёх точек
/// вариации (финализация, исполнение инструмента, формат результата в истории).
/// Здесь — только общая механика: лимиты шагов/вызовов, отмена, обработка
/// сбоев LLM, разбор tool-call через общий <see cref="ToolCallParser"/>.
/// </summary>
public sealed class ReActLoopEngine : IAgentLoopEngine
{
    public async Task<ReActOutcome> RunAsync(
        IChatClient chat,
        IReadOnlyList<ChatMessage> history,
        IReActStrategy strategy,
        ReActOptions options,
        IReActObserver? observer,
        CancellationToken ct)
    {
        observer = observer ?? NullReActObserver.Instance;
        var messages = new List<ChatMessage>(history);

        var toolCalls = 0;
        int? promptTokens = null;
        int? completionTokens = null;

        for (var step = 0; step < options.MaxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();

            string? responseText;
            try
            {
                var response = await chat.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
                responseText = response.Text;

                if (response.Usage is { } usage)
                {
                    if (usage.InputTokenCount is { } i)
                    {
                        promptTokens = (promptTokens ?? 0) + (int)Math.Min(i, int.MaxValue);
                    }

                    if (usage.OutputTokenCount is { } o)
                    {
                        completionTokens = (completionTokens ?? 0) + (int)Math.Min(o, int.MaxValue);
                    }
                }

                observer.OnModelResponse(promptTokens, completionTokens);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return strategy.OnLlmFailure(ex.Message, toolCalls, promptTokens, completionTokens);
            }

            if (string.IsNullOrWhiteSpace(responseText))
            {
                return strategy.OnEmptyResponse(toolCalls, promptTokens, completionTokens);
            }

            if (strategy.TryFinalize(responseText, toolCalls, promptTokens, completionTokens, out var finalOutcome))
            {
                return finalOutcome;
            }

            // Ответ не финальный — ожидаем запрос инструмента.
            if (toolCalls >= options.ToolCallLimit)
            {
                return strategy.OnToolCallLimitReached(responseText, toolCalls, promptTokens, completionTokens);
            }

            if (!ToolCallParser.TryParse(responseText, out var toolName, out var toolArgs))
            {
                // Свободный текст без JSON и не распознан как финал — стратегия
                // решает сама (в оркестраторе уже вернул Fallback в TryFinalize;
                // здесь недостижимо для корректных стратегий).
                return strategy.TryFinalize(responseText, toolCalls, promptTokens, completionTokens, out var fallbackOutcome)
                    ? fallbackOutcome
                    : strategy.OnEmptyResponse(toolCalls, promptTokens, completionTokens);
            }

            observer.OnToolCall(step, toolName, toolArgs);

            var (resultText, succeeded) = await strategy.InvokeToolAsync(toolName, toolArgs, ct).ConfigureAwait(false);
            toolCalls++;

            observer.OnToolResult(step, toolName, resultText, succeeded);

            messages.Add(new ChatMessage(ChatRole.Assistant, responseText));
            messages.Add(new ChatMessage(ChatRole.User, strategy.FormatToolResult(toolName, resultText, succeeded)));
        }

        return strategy.OnStepLimitReached(toolCalls, promptTokens, completionTokens);
    }
}