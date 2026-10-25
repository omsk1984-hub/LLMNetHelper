using LLMNetHelper.Agent.AgentLoop;
using LLMNetHelper.Agent.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LLMNetHelper.Chat;

/// <summary>
/// ReAct-цикл чата для режима <c>/tools</c> (план 22, этап 3.5).
/// <para>
/// Тонкий адаптер над общим движком <see cref="ReActLoopEngine"/> с
/// <see cref="ChatReActStrategy"/>: финальный ответ — свободный текст,
/// модель получает полный текст результата инструмента, лимиты/сбои —
/// прежние формулировки. Транскрипт и логи ведёт приватный наблюдатель.
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

        var engine = new ReActLoopEngine();
        var strategy = new ChatReActStrategy(_registry);

        var outcome = await engine.RunAsync(
            _chat,
            messages,
            strategy,
            new ReActOptions(_options.MaxSteps, _options.ToolCallLimit),
            new TranscriptObserver(_transcript, _logger),
            ct).ConfigureAwait(false);

        return outcome.ResponseText ?? "[Движок не вернул ответа].";
    }

    /// <summary>
    /// Наблюдатель: воспроизводит прежние вызовы транскрипта и лог-записи
    /// (план 22, этап 3.5).
    /// </summary>
    private sealed class TranscriptObserver(IChatTranscript transcript, ILogger<ChatAgentLoop> logger) : IReActObserver
    {
        public void OnStepModelResponse(int step, string responseText, int? inputTokens, int? outputTokens)
        {
            transcript.LogModelStep(step, responseText, inputTokens, outputTokens);
        }

        public void OnStepToolCall(int step, string toolName, string argumentsJson)
        {
            transcript.LogToolCall(step, toolName, argumentsJson);
        }

        public void OnStepToolResult(int step, string toolName, string resultText, bool succeeded)
        {
            transcript.LogToolResult(step, toolName, resultText, succeeded);
            logger.LogInformation(
                "Чат: инструмент {Tool}: {Chars} символов, успех={Ok}",
                toolName, resultText.Length, succeeded);
        }

        public void OnFinalAnswer(int step, string responseText, int? inputTokens, int? outputTokens)
        {
            transcript.LogAssistantAnswer(responseText, inputTokens, outputTokens);
        }

        public void OnLlmFailure(int step, string message)
        {
            logger.LogError("Чат: сбой вызова LLM на шаге {Step}: {Message}", step, message);
            transcript.LogNote($"[Сбой LLM на шаге {step + 1}: {message}]");
        }

        public void OnEmptyResponse(int step)
        {
            transcript.LogNote("[Модель вернула пустой ответ]");
        }

        public void OnToolCallLimit(int step)
        {
            logger.LogWarning("Чат: достигнут лимит вызовов инструментов");
            transcript.LogNote("[Достигнут лимит вызовов инструментов — ответ может быть неполным.]");
        }

        public void OnStepLimit(int step)
        {
            logger.LogWarning("Чат: достигнут лимит шагов агента");
            transcript.LogNote("[Достигнут лимит шагов агента — переформулируйте вопрос или отключите /tools.]");
        }
    }
}
