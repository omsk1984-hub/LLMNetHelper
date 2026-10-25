using LLMNetHelper.Core.Analysis;
using Microsoft.Extensions.AI;

namespace LLMNetHelper.Agent.AgentLoop;

/// <summary>
/// Общий ReAct-цикл (план 22, этап 3.4). Выполняет итерации «модель → ответ →
/// (финал | вызов инструмента)», применяя переданную стратегию для трёх точек
/// вариации (финализация, исполнение инструмента, формат результата в истории).
/// Здесь — только общая механика: лимиты шагов/вызовов, отмена, обработка
/// сбоев LLM, разбор tool-call через общий <see cref="ToolCallParser"/> и
/// уведомление наблюдателя в точках, соответствующих прежним вызовам.
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
            int? stepInputTokens = null;
            int? stepOutputTokens = null;
            try
            {
                var response = await chat.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
                responseText = response.Text;

                if (response.Usage is { } usage)
                {
                    if (usage.InputTokenCount is { } i)
                    {
                        stepInputTokens = (int)Math.Min(i, int.MaxValue);
                        promptTokens = (promptTokens ?? 0) + stepInputTokens.Value;
                    }

                    if (usage.OutputTokenCount is { } o)
                    {
                        stepOutputTokens = (int)Math.Min(o, int.MaxValue);
                        completionTokens = (completionTokens ?? 0) + stepOutputTokens.Value;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                observer.OnLlmFailure(step, ex.Message);
                return strategy.OnLlmFailure(ex.Message, toolCalls, promptTokens, completionTokens, stepInputTokens, stepOutputTokens);
            }

            if (string.IsNullOrWhiteSpace(responseText))
            {
                observer.OnEmptyResponse(step);
                return strategy.OnEmptyResponse(toolCalls, promptTokens, completionTokens, stepInputTokens, stepOutputTokens);
            }

            if (strategy.TryFinalize(responseText, toolCalls, promptTokens, completionTokens, stepInputTokens, stepOutputTokens, out var finalOutcome))
            {
                observer.OnFinalAnswer(step, responseText, stepInputTokens, stepOutputTokens);
                return finalOutcome;
            }

            // Ответ не финальный — ожидаем запрос инструмента.
            if (toolCalls >= options.ToolCallLimit)
            {
                observer.OnToolCallLimit(step);
                return strategy.OnToolCallLimitReached(responseText, toolCalls, promptTokens, completionTokens, stepInputTokens, stepOutputTokens);
            }

            if (!ToolCallParser.TryParse(responseText, out var toolName, out var toolArgs))
            {
                // Свободный текст без JSON и не распознан как финал — стратегия
                // решает сама (в оркестраторе уже вернул Fallback в TryFinalize;
                // здесь недостижимо для корректных стратегий).
                var fallback = strategy.TryFinalize(responseText, toolCalls, promptTokens, completionTokens, stepInputTokens, stepOutputTokens, out var fallbackOutcome)
                    ? fallbackOutcome
                    : strategy.OnEmptyResponse(toolCalls, promptTokens, completionTokens, stepInputTokens, stepOutputTokens);
                observer.OnFinalAnswer(step, responseText, stepInputTokens, stepOutputTokens);
                return fallback;
            }

            observer.OnStepModelResponse(step, responseText, stepInputTokens, stepOutputTokens);
            observer.OnStepToolCall(step, toolName, toolArgs);

            var (resultText, succeeded) = await strategy.InvokeToolAsync(toolName, toolArgs, ct).ConfigureAwait(false);
            toolCalls++;

            observer.OnStepToolResult(step, toolName, resultText, succeeded);

            messages.Add(new ChatMessage(ChatRole.Assistant, responseText));
            messages.Add(new ChatMessage(ChatRole.User, strategy.FormatToolResult(toolName, resultText, succeeded)));
        }

        observer.OnStepLimit(options.MaxSteps);
        return strategy.OnStepLimitReached(toolCalls, promptTokens, completionTokens);
    }
}