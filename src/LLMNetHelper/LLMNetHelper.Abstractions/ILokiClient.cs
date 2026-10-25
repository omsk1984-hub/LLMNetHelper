namespace LLMNetHelper.Abstractions;

/// <summary>
/// Клиент Loki: запрос LogQL-агрегаций и выборка записей.
/// </summary>
public interface ILokiClient : IDisposable
{
    /// <summary>Запрос агрегированных счётчиков по уровням, job, svc и т.д.</summary>
    Task<Dictionary<string, long>> QueryAggregationsAsync(string query, CancellationToken ct = default);

    /// <summary>
    /// Выборка сырых записей fail/crit за указанное окно.
    /// <para>
    /// Границы окна обязательны: без <c>start</c>/<c>end</c> Loki отвечает
    /// <c>resultType: "streams"</c> с пустым списком значений, т.е. выборка
    /// всегда была бы нулевой независимо от наличия ошибок в логах.
    /// </para>
    /// <para>
    /// План 22, этап 1.6: типизированный <see cref="LokiEntry"/> вместо
    /// <c>Dictionary&lt;string, object&gt;</c>.
    /// </para>
    /// </summary>
    /// <param name="query">LogQL-выражение потока (без агрегаторов).</param>
    /// <param name="from">Начало окна (UTC).</param>
    /// <param name="to">Конец окна (UTC).</param>
    /// <param name="limit">Максимальное число записей.</param>
    Task<List<LokiEntry>> QueryEntriesAsync(
        string query, DateTime from, DateTime to, int limit, CancellationToken ct = default);

    /// <summary>Проверка readiness Loki.</summary>
    Task<bool> IsReadyAsync(CancellationToken ct = default);

    /// <summary>Получение метрик Loki для health-check.</summary>
    Task<string?> GetMetricsAsync(CancellationToken ct = default);
}
