namespace ArtasTools.MonitorAgent.Abstractions;

/// <summary>
/// Параметры подключения к Prometheus.
/// </summary>
public sealed class PrometheusOptions
{
    /// <summary>Базовый URL Prometheus (по умолчанию http://prometheus:9090).</summary>
    public string BaseUrl { get; set; } = "http://prometheus:9090";

    /// <summary>Таймаут запросов в секундах (по умолчанию 30).</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
