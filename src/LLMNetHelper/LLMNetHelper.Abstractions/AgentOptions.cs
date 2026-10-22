namespace LLMNetHelper.Abstractions;

/// <summary>
/// Параметры агента: расписание, окно анализа, каталоги, удержание истории.
/// </summary>
public sealed class AgentOptions
{
    /// <summary>Время запуска прогона в UTC (формат "HH:mm", по умолчанию "03:00").</summary>
    public string RunAtUtc { get; set; } = "03:00";

    /// <summary>Окно анализа в часах (по умолчанию 24).</summary>
    public int WindowHours { get; set; } = 24;

    /// <summary>Каталог для отчётов и базы данных.</summary>
    public string ReportsDir { get; set; } = "./reports/monitoring";

    /// <summary>Путь к файлу SQLite-базы фактов.</summary>
    public string DatabasePath { get; set; } = "./reports/monitoring/facts.db";

    /// <summary>Количество дней хранения истории (по умолчанию 90).</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Максимальное число уникальных ошибок в сводке (по умолчанию 25).</summary>
    public int TopErrorsLimit { get; set; } = 25;

    /// <summary>Максимальная длина sample_text в символах (по умолчанию 2000).</summary>
    public int MaxSampleChars { get; set; } = 2000;

    /// <summary>Порт health-эндпоинта (по умолчанию 8080).</summary>
    public int HealthPort { get; set; } = 8080;
}
