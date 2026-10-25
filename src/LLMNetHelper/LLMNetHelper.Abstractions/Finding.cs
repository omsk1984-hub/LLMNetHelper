namespace LLMNetHelper.Abstractions;

/// <summary>
/// Серьёзность находки (план 22, этап 1.4): вместо магических строк
/// <c>"critical"/"warning"/"info"</c>.
/// </summary>
public enum FindingSeverity
{
    /// <summary>Информационная находка.</summary>
    Info,

    /// <summary>Предупреждение.</summary>
    Warning,

    /// <summary>Критичная находка — обязывает к вердикту CRITICAL.</summary>
    Critical,

    /// <summary>
    /// Нераспознанное значение из ответа модели. Исходная строка сохраняется
    /// в <see cref="Finding.RawSeverity"/>, чтобы не терять сигнал
    /// (согласовано с пользователем: вариант «б»). На коррекцию вердикта не влияет.
    /// </summary>
    Other
}

/// <summary>
/// Уверенность модели в находке/гипотезе (план 22, этап 1.4): вместо
/// магических строк <c>"high"/"medium"/"low"</c>.
/// </summary>
public enum Confidence
{
    /// <summary>Низкая уверенность.</summary>
    Low,

    /// <summary>Средняя уверенность.</summary>
    Medium,

    /// <summary>Высокая уверенность.</summary>
    High,

    /// <summary>
    /// Нераспознанное значение из ответа модели; исходная строка —
    /// в <see cref="Finding.RawConfidence"/> или <see cref="Hypothesis.RawConfidence"/>.
    /// </summary>
    Other
}

/// <summary>
/// Граничные конвертеры severity/confidence (план 22, этап 1.4).
/// Wire-формат не меняется: канонические строки — нижний регистр
/// (<c>"critical"</c>, <c>"high"</c> и т.д.); <see cref="FindingSeverity.Other"/>
/// и <see cref="Confidence.Other"/> сериализуются исходной строкой модели.
/// </summary>
public static class FindingFormat
{
    /// <summary>Разбор строки severity из ответа модели (регистронезависимо).</summary>
    public static FindingSeverity ParseSeverity(string? value, out string? raw)
    {
        raw = null;
        var normalized = value?.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "critical":
                return FindingSeverity.Critical;
            case "warning":
                return FindingSeverity.Warning;
            case "info":
                return FindingSeverity.Info;
            case null or "":
                return FindingSeverity.Info;
            default:
                raw = normalized;
                return FindingSeverity.Other;
        }
    }

    /// <summary>Разбор строки confidence из ответа модели (регистронезависимо).</summary>
    public static Confidence ParseConfidence(string? value, out string? raw)
    {
        raw = null;
        var normalized = value?.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "high":
                return Confidence.High;
            case "medium":
                return Confidence.Medium;
            case "low":
                return Confidence.Low;
            case null or "":
                return Confidence.Low;
            default:
                raw = normalized;
                return Confidence.Other;
        }
    }

    /// <summary>Wire-строка severity (для отчётов и промптов).</summary>
    public static string ToWire(FindingSeverity severity, string? raw) => severity switch
    {
        FindingSeverity.Critical => "critical",
        FindingSeverity.Warning => "warning",
        FindingSeverity.Info => "info",
        FindingSeverity.Other => string.IsNullOrWhiteSpace(raw) ? "other" : raw,
        _ => "other"
    };

    /// <summary>Wire-строка confidence (для отчётов и промптов).</summary>
    public static string ToWire(Confidence confidence, string? raw) => confidence switch
    {
        Confidence.High => "high",
        Confidence.Medium => "medium",
        Confidence.Low => "low",
        Confidence.Other => string.IsNullOrWhiteSpace(raw) ? "other" : raw,
        _ => "other"
    };
}

/// <summary>
/// Находка LLM.
/// <para>
/// Перенесена из <c>LLMNetHelper.Core.Analysis</c> в Abstractions (план 18,
/// пункт 0.1): тип участвует в контракте <see cref="Diagnosis"/> между
/// оркестратором (Agent) и отчётностью (Running/Core), поэтому должен жить
/// в общем слое абстракций, а не в реализации парсера.
/// </para>
/// <para>
/// План 22, этап 1.4: <see cref="Severity"/> и <see cref="Confidence"/> —
/// перечисления; нераспознанные значения модели сохраняются как
/// <see cref="RawSeverity"/>/<see cref="RawConfidence"/>.
/// </para>
/// </summary>
// План 22, этап 1.7: record с init-свойствами — неизменяемая модель контрактного
// слоя (value equality; мутация после создания невозможна на уровне компиляции).
public sealed record Finding
{
    public string Id { get; init; } = "";
    public FindingSeverity Severity { get; init; } = FindingSeverity.Info;
    public string Title { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string Impact { get; init; } = "";
    public string Recommendation { get; init; } = "";
    public Confidence Confidence { get; init; } = Confidence.Low;

    /// <summary>Исходная строка severity, если значение не распознано (<see cref="FindingSeverity.Other"/>).</summary>
    public string? RawSeverity { get; init; }

    /// <summary>Исходная строка confidence, если значение не распознано (<see cref="Confidence.Other"/>).</summary>
    public string? RawConfidence { get; init; }
}
