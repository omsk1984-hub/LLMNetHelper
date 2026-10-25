namespace LLMNetHelper.Abstractions;

/// <summary>
/// Статус прогона агента (план 22, этап 1.2): вместо магических строк
/// <c>"in_progress" | "ok" | "partial" | "failed"</c>.
/// </summary>
public enum RunStatus
{
    /// <summary>Прогон начат, ещё не завершён.</summary>
    InProgress,

    /// <summary>Прогон завершён успешно.</summary>
    Ok,

    /// <summary>Прогон завершён частично (некоторые шаги не выполнены).</summary>
    Partial,

    /// <summary>Прогон завершился сбоем.</summary>
    Failed
}

/// <summary>
/// Граничный конвертер статуса прогона (план 22, этап 1.2). Wire-формат не меняется:
/// нижний регистр с подчёркиванием (<c>"in_progress"</c>, <c>"ok"</c>, <c>"partial"</c>, <c>"failed"</c>) —
/// именно так статусы хранятся в колонке <c>runs.status</c>.
/// </summary>
public static class RunStatusFormat
{
    /// <summary>
    /// Разбор строки статуса из БД (регистронезависимо).
    /// Нераспознанное значение — <see cref="RunStatus.Failed"/> (консервативно).
    /// </summary>
    public static RunStatus Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "in_progress" => RunStatus.InProgress,
        "ok" => RunStatus.Ok,
        "partial" => RunStatus.Partial,
        "failed" => RunStatus.Failed,
        _ => RunStatus.Failed
    };

    /// <summary>Wire-строка статуса (для SQLite).</summary>
    public static string ToWire(RunStatus status) => status switch
    {
        RunStatus.InProgress => "in_progress",
        RunStatus.Ok => "ok",
        RunStatus.Partial => "partial",
        RunStatus.Failed => "failed",
        _ => "failed"
    };
}
