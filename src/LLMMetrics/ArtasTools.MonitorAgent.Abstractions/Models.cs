namespace ArtasTools.MonitorAgent.Abstractions;

/// <summary>
/// Модель факта из Loki: агрегация логов по bucket, меткам и уровню.
/// </summary>
public sealed class LogFact
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public string BucketStart { get; set; } = "";
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
    public string FirstSeen { get; set; } = "";
    public string LastSeen { get; set; } = "";
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
    public string? WindowFrom { get; set; }
    public string? WindowTo { get; set; }
}

/// <summary>
/// Модель запуска: метаданные прогона и вердикт.
/// </summary>
public sealed class RunInfo
{
    public long Id { get; set; }
    public string StartedAt { get; set; } = "";
    public string? FinishedAt { get; set; }
    public string WindowFrom { get; set; } = "";
    public string WindowTo { get; set; } = "";
    public string Status { get; set; } = ""; // ok | partial | failed
    public string? Verdict { get; set; } // OK | WARNING | CRITICAL | UNKNOWN
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
