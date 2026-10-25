namespace LLMNetHelper.Abstractions;

/// <summary>
/// Репозиторий запусков агента (план 22, этап 2.7). Извлечён из жирного
/// <see cref="IFactStore"/> по ISP: жизненный цикл прогона (создание,
/// завершение, чтение последнего успешного, очистка по retention).
/// </summary>
public interface IRunRepository
{
    /// <summary>Создаёт новый запуск и возвращает его ID (границы окна — UTC).</summary>
    Task<long> CreateRunAsync(DateTime windowFrom, DateTime windowTo, CancellationToken ct = default);

    /// <summary>Завершает запуск с результатом (status/verdict — перечисления).</summary>
    Task FinishRunAsync(long runId, RunStatus status, Verdict? verdict, string? error, CancellationToken ct = default);

    /// <summary>Последний успешный запуск за последние N часов.</summary>
    Task<RunInfo?> GetLastSuccessfulRunAsync(int hoursBack, CancellationToken ct = default);

    /// <summary>Очищает историю старше RetentionDays.</summary>
    Task<int> PurgeOldRunsAsync(int retentionDays, CancellationToken ct = default);
}

/// <summary>
/// Репозиторий агрегатов логов (план 22, этап 2.7).
/// </summary>
public interface ILogFactRepository
{
    /// <summary>Сохраняет агрегаты логов прогона.</summary>
    Task SaveLogFactsAsync(long runId, IEnumerable<LogFact> facts, CancellationToken ct = default);

    /// <summary>Агрегаты логов за прогон (для диффа).</summary>
    Task<List<LogFact>> GetPreviousLogFactsAsync(long runId, CancellationToken ct = default);
}

/// <summary>
/// Репозиторий уникальных ошибок (план 22, этап 2.7).
/// </summary>
public interface IErrorSampleRepository
{
    /// <summary>Сохраняет уникальные ошибки прогона.</summary>
    Task SaveErrorSamplesAsync(long runId, IEnumerable<ErrorSample> samples, CancellationToken ct = default);

    /// <summary>Ошибки за прогон (для диффа).</summary>
    Task<List<ErrorSample>> GetPreviousErrorSamplesAsync(long runId, CancellationToken ct = default);
}

/// <summary>
/// Репозиторий метрик Prometheus (план 22, этап 2.7).
/// </summary>
public interface IMetricFactRepository
{
    /// <summary>Сохраняет метрики прогона.</summary>
    Task SaveMetricFactsAsync(long runId, IEnumerable<MetricFact> facts, CancellationToken ct = default);
}

/// <summary>
/// Репозиторий записей вызовов LLM (план 22, этап 2.7).
/// </summary>
public interface ILlmRunRepository
{
    /// <summary>Сохраняет информацию о вызове LLM.</summary>
    Task SaveLlmRunAsync(long runId, LlmRunInfo info, CancellationToken ct = default);
}
