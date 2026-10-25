namespace LLMNetHelper.Abstractions;

/// <summary>
/// Модель факта из Loki: агрегация логов по bucket, меткам и уровню.
/// <para>
/// План 22, этап 1.5: метки времени — <see cref="DateTime"/> (UTC) вместо строк;
/// конвертация в TEXT SQLite — внутри хранилища.
/// </para>
/// </summary>
// План 22, этап 1.7: record с init-свойствами — неизменяемая модель контрактного слоя.
public sealed record LogFact
{
    public long Id { get; init; }
    public long RunId { get; init; }
    public DateTime BucketStart { get; init; }
    public string Job { get; init; } = "";
    public string Svc { get; init; } = "";
    public string Lvl { get; init; } = "";
    public string Logger { get; init; } = "";
    public string Symbol { get; init; } = "";
    public int Count { get; init; }
}

/// <summary>
/// Модель уникальной ошибки: нормализованный signature + репрезентативный sample.
/// </summary>
public sealed record ErrorSample
{
    public long Id { get; init; }
    public long RunId { get; init; }
    public string Signature { get; init; } = "";
    public string Lvl { get; init; } = "";
    public string Job { get; init; } = "";
    public string Logger { get; init; } = "";
    public string Symbol { get; init; } = "";
    /// <summary>Первая встреча (UTC). <see cref="DateTime.MinValue"/> — неизвестно.</summary>
    public DateTime FirstSeen { get; init; }
    /// <summary>Последняя встреча (UTC). <see cref="DateTime.MinValue"/> — неизвестно.</summary>
    public DateTime LastSeen { get; init; }
    public int Count { get; init; }
    public string SampleText { get; init; } = "";
}

/// <summary>
/// Модель факта из Prometheus: метрика с метками, значением и трендом.
/// </summary>
public sealed record MetricFact
{
    public long Id { get; init; }
    public long RunId { get; init; }
    public string Name { get; init; } = "";
    public string? Labels { get; init; }
    public double Value { get; init; }
    public double SlopePerHour { get; init; }
    public double R2 { get; init; }
    /// <summary>Начало окна метрики (UTC, план 22, этап 1.5).</summary>
    public DateTime? WindowFrom { get; init; }
    /// <summary>Конец окна метрики (UTC, план 22, этап 1.5).</summary>
    public DateTime? WindowTo { get; init; }
}

/// <summary>
/// Модель запуска: метаданные прогона и вердикт.
/// </summary>
public sealed record RunInfo
{
    public long Id { get; init; }
    /// <summary>Начало прогона (UTC, план 22, этап 1.5).</summary>
    public DateTime StartedAt { get; init; }
    /// <summary>Конец прогона (UTC); null — не завершён.</summary>
    public DateTime? FinishedAt { get; init; }
    public DateTime WindowFrom { get; init; }
    public DateTime WindowTo { get; init; }
    public RunStatus Status { get; init; } = RunStatus.InProgress; // план 22, этап 1.2: перечисление
    public Verdict? Verdict { get; init; } // план 22, этап 1.1: перечисление
    public string? LlmProvider { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Модель записи вызова LLM.
/// </summary>
public sealed record LlmRunInfo
{
    public long Id { get; init; }
    public long RunId { get; init; }
    public string Provider { get; init; } = "";
    public string? Endpoint { get; init; }
    public string? Model { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public int? DurationMs { get; init; }
    public string? FinishReason { get; init; }
    public bool FallbackUsed { get; init; }
    public string? RawResponse { get; init; }
}
