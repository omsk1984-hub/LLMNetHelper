namespace LLMNetHelper.Abstractions;

/// <summary>
/// Вердикт диагностики инцидента (план 22, этап 1.1): вместо магических строк
/// <c>"OK" | "WARNING" | "CRITICAL" | "UNKNOWN"</c>.
/// </summary>
public enum Verdict
{
    /// <summary>Инцидентов нет.</summary>
    Ok,

    /// <summary>Есть предупреждения.</summary>
    Warning,

    /// <summary>Критичная находка — требуется вмешательство.</summary>
    Critical,

    /// <summary>
    /// Ответ не распознан (сбой модели, лимит шагов, неструктурированный ответ).
    /// Нераспознанная строка вердикта от модели также даёт <see cref="Unknown"/>.
    /// </summary>
    Unknown
}

/// <summary>
/// Граничный конвертер вердикта (план 22, этап 1.1). Wire-формат не меняется:
/// верхний регистр (<c>"OK"</c>, <c>"WARNING"</c>, <c>"CRITICAL"</c>, <c>"UNKNOWN"</c>) —
/// именно так вердикты попадают в отчёты, промпты и колонку <c>runs.verdict</c>.
/// </summary>
public static class VerdictFormat
{
    /// <summary>
    /// Разбор строки вердикта из ответа модели (регистронезависимо).
    /// Нераспознанное значение — <see cref="Verdict.Unknown"/> (без исключения).
    /// </summary>
    public static Verdict Parse(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "OK" => Verdict.Ok,
        "WARNING" => Verdict.Warning,
        "CRITICAL" => Verdict.Critical,
        "UNKNOWN" => Verdict.Unknown,
        _ => Verdict.Unknown
    };

    /// <summary>Wire-строка вердикта (для отчётов, БД, промптов и логов).</summary>
    public static string ToWire(Verdict verdict) => verdict switch
    {
        Verdict.Ok => "OK",
        Verdict.Warning => "WARNING",
        Verdict.Critical => "CRITICAL",
        Verdict.Unknown => "UNKNOWN",
        _ => "UNKNOWN"
    };
}
