using LLMNetHelper.Abstractions;
using Microsoft.Extensions.AI;

namespace LLMNetHelper.Agent.AgentLoop;

/// <summary>
/// Результат одного прогона общего ReAct-цикла (план 22, этап 3.4).
/// <para>
/// Цикл общий для <c>NativeAgentOrchestrator</c> и <c>ChatAgentLoop</c> и
/// параметризован стратегией <see cref="IReActStrategy"/>, которая отвечает
/// за три точки вариации: финализацию обычного ответа, форматирование
/// результата инструмента в истории и формулировки терминальных исходов
/// (сбой LLM, пустой ответ, лимиты). Размещён в Agent, т.к. контракт
/// использует MEAI (<see cref="IChatClient"/>/<see cref="ChatMessage"/>),
/// которого нет в Core.
/// </para>
/// </summary>
public interface IAgentLoopEngine
{
    /// <summary>
    /// Выполняет ReAct-цикл над историей сообщений.
    /// </summary>
    /// <param name="chat">LLM-клиент.</param>
    /// <param name="history">Стартовая история (system + user промпты).</param>
    /// <param name="strategy">Стратегия: финализация, исполнение инструмента, формат результата, терминальные исходы.</param>
    /// <param name="options">Лимиты цикла.</param>
    /// <param name="observer">Наблюдатель за событиями (транскрипт/usage; может быть null).</param>
    /// <param name="ct">Токен отмены.</param>
    Task<ReActOutcome> RunAsync(
        IChatClient chat,
        IReadOnlyList<ChatMessage> history,
        IReActStrategy strategy,
        ReActOptions options,
        IReActObserver? observer,
        CancellationToken ct);
}

/// <summary>Лимиты ReAct-цикла (общие для оркестратора и чата).</summary>
public sealed record ReActOptions(int MaxSteps, int ToolCallLimit);

/// <summary>Итог прогона цикла. Для диагноза заполнен <see cref="Diagnosis"/>,
/// для свободного текста — <see cref="ResponseText"/>.</summary>
public sealed record ReActOutcome(
    string? ResponseText,
    Diagnosis? Diagnosis,
    int ToolCalls,
    int? PromptTokens,
    int? CompletionTokens);

/// <summary>
/// Стратегия ReAct-цикла, инкапсулирующая все поведенческие различия между
/// оркестратором и чатом. Возвраты <c>false</c> из <see cref="TryFinalize"/>
/// означают «это запрос инструмента» — движок разбирает его общим
/// <c>ToolCallParser</c> и продолжает цикл.
/// </summary>
public interface IReActStrategy
{
    /// <summary>
    /// Является ли ответ модели финальным результатом. Если да — заполняет
    /// <paramref name="outcome"/> и возвращает <c>true</c>. Если это запрос
    /// инструмента — возвращает <c>false</c> (движок продолжит цикл).
    /// <para>
    /// Оркестратор: диагноз → финал; иначе tool-call → false; свободный текст
    /// → Fallback-диагноз (финал). Чат: tool-call → false; свободный текст → финал.
    /// </para>
    /// </summary>
    bool TryFinalize(string responseText, int toolCalls, int? promptTokens, int? completionTokens, out ReActOutcome outcome);

    /// <summary>
    /// Выполняет инструмент и возвращает понятный результат (для оркестратора —
    /// подпись с числом символов; для чата — полный текст). Исполнение живёт в
    /// стратегии, т.к. пути вызова различаются (<see cref="IToolInvoker"/> vs
    /// <c>ToolRegistry.Find</c>+<c>InvokeAsync</c>).
    /// </summary>
    Task<(string ResultText, bool Succeeded)> InvokeToolAsync(string toolName, string argumentsJson, CancellationToken ct);

    /// <summary>Формат результата инструмента для возврата в историю (роль User).</summary>
    string FormatToolResult(string toolName, string resultText, bool succeeded);

    /// <summary>Исход при сбое вызова LLM.</summary>
    ReActOutcome OnLlmFailure(string message, int toolCalls, int? promptTokens, int? completionTokens);

    /// <summary>Исход при пустом ответе модели.</summary>
    ReActOutcome OnEmptyResponse(int toolCalls, int? promptTokens, int? completionTokens);

    /// <summary>Исход при достижении лимита вызовов инструментов.</summary>
    ReActOutcome OnToolCallLimitReached(string responseText, int toolCalls, int? promptTokens, int? completionTokens);

    /// <summary>Исход при достижении лимита шагов.</summary>
    ReActOutcome OnStepLimitReached(int toolCalls, int? promptTokens, int? completionTokens);
}

/// <summary>Наблюдатель за событиями цикла (транскрипт, usage). Все — no-op по умолчанию.</summary>
public interface IReActObserver
{
    void OnModelResponse(int? inputTokens, int? outputTokens);
    void OnToolCall(int step, string toolName, string argumentsJson);
    void OnToolResult(int step, string toolName, string resultText, bool succeeded);
}

/// <summary>No-op наблюдатель, чтобы избежать null-проверок в движке.</summary>
public sealed class NullReActObserver : IReActObserver
{
    public static readonly NullReActObserver Instance = new();

    public void OnModelResponse(int? inputTokens, int? outputTokens)
    {
    }

    public void OnToolCall(int step, string toolName, string argumentsJson)
    {
    }

    public void OnToolResult(int step, string toolName, string resultText, bool succeeded)
    {
    }
}