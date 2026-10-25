namespace LLMNetHelper.Abstractions;

/// <summary>
/// Модель факта из Loki: агрегация логов по bucket, меткам и уровню.
/// <para>
/// План 22, этап 1.5: метки времени — <see cref="DateTime"/> (UTC) вместо строк;
/// конвертация в TEXT SQLite — внутри хранилища.
/// </para>
/// </summary>
public sealed class LogFact
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public DateTime BucketStart { get; set; }
    public string Job { get; set; } = "";
    public string Svc { get; set; } = "";
    public string Lvl { get; set; } = "";
    public string Logger { get; set; } = "";
    public string Symbol { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>
/// Модель уникальной ошибки: нормализованный signature + репрезентативный sample.
/// </summary>
public sealed class ErrorSample
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public string Signature { get; set; } = "";
    public string Lvl { get; set; } = "";
    public string Job { get; set; } = "";
    public string Logger { get; set; } = "";
    public string Symbol { get; set; } = "";
    /// <summary>Первая встреча (UTC). <see cref="DateTime.MinValue"/> — неизвестно.</summary>
    public DateTime FirstSeen { get; set; }
    /// <summary>Последняя встреча (UTC). <see cref="DateTime.MinValue"/> — неизвестно.</summary>
    public DateTime LastSeen { get; set; }
    public int Count { get; set; }
    public string SampleText { get; set; } = "";
}

/// <summary>
/// Модель факта из Prometheus: метрика с метками, значением и трендом.
/// </summary>
public sealed class MetricFact
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public string Name { get; set; } = "";
    public string? Labels { get; set; }
    public double Value { get; set; }
    public double SlopePerHour { get; set; }
    public double R2 { get; set; }
    /// <summary>Начало окна метрики (UTC, план 22, этап 1.5).</summary>
    public DateTime? WindowFrom { get; set; }
    /// <summary>Конец окна метрики (UTC, план 22, этап 1.5).</summary>
    public DateTime? WindowTo { get; set; }
}

/// <summary>
/// Модель запуска: метаданные прогона и вердикт.
/// </summary>
public sealed class RunInfo
{
    public long Id { get; set; }
    /// <summary>Начало прогона (UTC, план 22, этап 1.5).</summary>
    public DateTime StartedAt { get; set; }
    /// <summary>Конец прогона (UTC); null — не завершён.</summary>
    public DateTime? FinishedAt { get; set; }
    public DateTime WindowFrom { get; set; }
    public DateTime WindowTo { get; set; }
    public RunStatus Status { get; set; } = RunStatus.InProgress; // план 22, этап 1.2: перечисление
    public Verdict? Verdict { get; set; } // план 22, этап 1.1: перечисление
    public string? LlmProvider { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Модель записи вызова LLM.
/// </summary>
public sealed class LlmRunInfo
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public string Provider { get; set; } = "";
    public string? Endpoint { get; set; }
    public string? Model { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? DurationMs { get; set; }
    public string? FinishReason { get; set; }
    public bool FallbackUsed { get; set; }
    public string? RawResponse { get; set; }
}
