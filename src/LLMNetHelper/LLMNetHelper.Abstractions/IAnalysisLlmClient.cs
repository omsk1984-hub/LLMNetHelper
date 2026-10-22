namespace LLMNetHelper.Abstractions;

/// <summary>
/// Контракт клиента LLM для генерации отчётов.
/// Две реализации: CustomOpenAiCompatibleClient и MeaiLlmClient.
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
