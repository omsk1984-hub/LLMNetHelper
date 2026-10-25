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

    /// <summary>
    /// Получение firing alerts.
    /// <para>
    /// План 22, этап 1.6: типизированный <see cref="PrometheusAlert"/> вместо
    /// <c>Dictionary&lt;string, object?&gt;</c>. Метод
    /// <c>GetAlertingRulesAsync</c> удалён как мёртвый код (0 потребителей,
    /// проверено <c>find_references</c>).
    /// </para>
    /// </summary>
    Task<List<PrometheusAlert>> GetAlertsAsync(CancellationToken ct = default);

    /// <summary>Получение метрик Prometheus для health-check.</summary>
    Task<string?> GetMetricsAsync(CancellationToken ct = default);
}
