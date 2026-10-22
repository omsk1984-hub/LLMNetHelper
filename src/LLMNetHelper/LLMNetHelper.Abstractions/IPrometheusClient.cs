namespace LLMNetHelper.Abstractions;

/// <summary>
/// Клиент Prometheus: запросы instant/range и получение правил алертинга.
/// </summary>
public interface IPrometheusClient : IDisposable
{
    /// <summary>Instant-запрос: возвращает одно значение.</summary>
    Task<double?> QueryAsync(string query, CancellationToken ct = default);

    /// <summary>Range-запрос: возвращает серию точек.</summary>
    Task<List<(string Time, double Value)>> QueryRangeAsync(
        string query, DateTime from, DateTime to, TimeSpan step, CancellationToken ct = default);

    /// <summary>Получение списка правил алертинга.</summary>
    Task<List<Dictionary<string, object>>> GetAlertingRulesAsync(CancellationToken ct = default);

    /// <summary>Получение firing alerts.</summary>
    Task<List<Dictionary<string, object>>> GetAlertsAsync(CancellationToken ct = default);

    /// <summary>Получение метрик Prometheus для health-check.</summary>
    Task<string?> GetMetricsAsync(CancellationToken ct = default);
}
