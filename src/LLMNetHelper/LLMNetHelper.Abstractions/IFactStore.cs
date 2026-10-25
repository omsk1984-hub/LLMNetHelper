namespace LLMNetHelper.Abstractions;

/// <summary>
/// Хранилище фактов в SQLite: сохранение, чтение, очистка по retention.
/// </summary>
public interface IFactStore : IDisposable
{
    /// <summary>
    /// Применяет SQL-миграции (если нужно). Вызывается при инициализации.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Создаёт новый запуск и возвращает его ID.
    /// <para>
    /// План 22, этап 1.5: границы окна — <see cref="DateTime"/> (UTC);
    /// конвертация в TEXT SQLite — внутри хранилища.
    /// </para>
    /// </summary>
    Task<long> CreateRunAsync(DateTime windowFrom, DateTime windowTo, CancellationToken ct = default);

    /// <summary>
    /// Завершает запуск с результатом.
    /// <para>
    /// План 22, этапы 1.1/1.2: <paramref name="status"/> и
    /// <paramref name="verdict"/> — перечисления; конвертация в wire-строки
    /// — внутри хранилища.
    /// </para>
    /// </summary>
    Task FinishRunAsync(long runId, RunStatus status, Verdict? verdict, string? error, CancellationToken ct = default);

    /// <summary>
    /// Сохраняет агрегаты логов.
    /// </summary>
    Task SaveLogFactsAsync(long runId, IEnumerable<LogFact> facts, CancellationToken ct = default);

    /// <summary>
    /// Сохраняет уникальные ошибки.
    /// </summary>
    Task SaveErrorSamplesAsync(long runId, IEnumerable<ErrorSample> samples, CancellationToken ct = default);

    /// <summary>
    /// Сохраняет метрики Prometheus.
    /// </summary>
    Task SaveMetricFactsAsync(long runId, IEnumerable<MetricFact> facts, CancellationToken ct = default);

    /// <summary>
    /// Сохраняет информацию о вызове LLM.
    /// </summary>
    Task SaveLlmRunAsync(long runId, LlmRunInfo info, CancellationToken ct = default);

    /// <summary>
    /// Получает последний успешный запуск за последние N часов.
    /// </summary>
    Task<RunInfo?> GetLastSuccessfulRunAsync(int hoursBack, CancellationToken ct = default);

    /// <summary>
    /// Получает все ошибки за последний успешный прогон (для диффа).
    /// </summary>
    Task<List<ErrorSample>> GetPreviousErrorSamplesAsync(long runId, CancellationToken ct = default);

    /// <summary>
    /// Получает агрегаты логов за последний успешный прогон.
    /// </summary>
    Task<List<LogFact>> GetPreviousLogFactsAsync(long runId, CancellationToken ct = default);

    /// <summary>
    /// Очищает историю старше RetentionDays.
    /// </summary>
    Task<int> PurgeOldRunsAsync(int retentionDays, CancellationToken ct = default);
}
