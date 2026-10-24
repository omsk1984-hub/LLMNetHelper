namespace LLMCore.Abstractions;

/// <summary>
/// Контракт клиента LLM для генерации отчётов.
/// Реализация: <c>CustomOpenAiCompatibleClient</c> (LLMCore.Llm).
/// </summary>
public interface IAnalysisLlmClient
{
    /// <summary>Имя провайдера: "custom" или "meai".</summary>
    string ProviderName { get; }

    /// <summary>
    /// Отправляет запрос в LLM и возвращает завершённое_completion_.
    /// </summary>
    Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken ct);
}
