namespace LLMNetHelper.Abstractions;

/// <summary>
/// Запрос к LLM: системный промпт, пользовательский промпт (сводка), схема JSON, параметры генерации.
/// </summary>
public sealed record LlmRequest(
    string SystemPrompt,
    string UserPrompt,
    string ExpectedJsonSchema,
    int MaxTokens,
    double Temperature);

/// <summary>
/// Ответ LLM: текст, опциональный JSON, метрики использования, время.
/// </summary>
public sealed record LlmCompletion(
    string Text,
    string? Json,
    int? PromptTokens,
    int? CompletionTokens,
    string? FinishReason,
    string Model,
    bool FallbackUsed,
    TimeSpan Duration);
