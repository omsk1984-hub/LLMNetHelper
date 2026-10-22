namespace LLMNetHelper.Abstractions;

/// <summary>
/// Параметры LLM: провайдер, endpoint, модель, ключ, лимиты.
/// </summary>
public sealed class LlmOptions
{
    /// <summary>
    /// Реализация транспорта: "custom" (свой HttpClient) или "meai" (Microsoft.Extensions.AI).
    /// По умолчанию "custom".
    /// </summary>
    public string Provider { get; set; } = "custom";

    /// <summary>Endpoint [OI]-совместимого API (по умолчанию пустой — обязателен).</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>API-ключ (по умолчанию пустой — обязателен).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Название модели (по умолчанию "gpt-4.1-mini").</summary>
    public string Model { get; set; } = "gpt-4.1-mini";

    /// <summary>Максимальное число токенов в ответе (по умолчанию 4000).</summary>
    public int MaxTokens { get; set; } = 4000;

    /// <summary>Температура генерации (по умолчанию 0.1 — воспроизводимый отчёт).</summary>
    public double Temperature { get; set; } = 0.1;

    /// <summary>Таймаут запроса в секундах (по умолчанию 120).</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Максимальное число повторных попыток (по умолчанию 3).</summary>
    public int MaxRetries { get; set; } = 3;
}
