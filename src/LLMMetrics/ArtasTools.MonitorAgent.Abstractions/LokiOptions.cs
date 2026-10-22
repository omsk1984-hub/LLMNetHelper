namespace ArtasTools.MonitorAgent.Abstractions;

/// <summary>
/// Параметры подключения к Loki.
/// </summary>
public sealed class LokiOptions
{
    /// <summary>Базовый URL Loki (по умолчанию http://loki:3100).</summary>
    public string BaseUrl { get; set; } = "http://loki:3100";

    /// <summary>Таймаут запросов в секундах (по умолчанию 30).</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Максимальное число записей для выборки (по умолчанию 500).</summary>
    public int MaxEntries { get; set; } = 500;
}
