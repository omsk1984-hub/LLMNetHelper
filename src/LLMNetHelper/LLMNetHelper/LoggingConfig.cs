using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using JsonSerializerOptions = System.Text.Json.JsonSerializerOptions;

namespace LLMNetHelper;

/// <summary>
/// Настройка Serilog: консоль + текстовый файл (ротация по дням) + JSON-файл.
/// </summary>
public static class LoggingConfig
{
    private const string DefaultLogDir = "logs";
    private const int DefaultRetentionDays = 30;
    private const int ErrorRetentionDays = 90;

    /// <summary>
    /// Создает и настраивает Logger (готовый к использованию).
    /// </summary>
    public static ILogger CreateLogger(
        string? logDir = null,
        int retentionDays = DefaultRetentionDays,
        int errorRetentionDays = ErrorRetentionDays,
        bool enabled = true)
    {
        var logsDir = string.IsNullOrEmpty(logDir) ? DefaultLogDir : logDir;
        var textTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {CorrelationId,-8:} {SourceContext,-45} {Message:lj}{NewLine}{Exception}";

        var logger = new LoggerConfiguration();

        if (!enabled)
        {
            return logger
                .WriteTo.Console(outputTemplate: "[LOGGING DISABLED]")
                .MinimumLevel.Information()
                .CreateLogger();
        }

        // --- Основной текстовый лог (Information и выше) ---
        logger.WriteTo.File(
            path: Path.Combine(logsDir, "agent-.txt"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: retentionDays,
            outputTemplate: textTemplate,
            encoding: System.Text.Encoding.UTF8);

        // --- JSON-лог для структурированного парсинга ---
        logger.WriteTo.File(
            path: Path.Combine(logsDir, "agent-.json"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: retentionDays,
            formatter: new JsonLogFormatter());

        // --- Лог ошибок (Error и выше, отдельный файл) ---
        logger.WriteTo.File(
            path: Path.Combine(logsDir, "error-.txt"),
            restrictedToMinimumLevel: LogEventLevel.Error,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: errorRetentionDays,
            outputTemplate: textTemplate,
            encoding: System.Text.Encoding.UTF8);

        return logger
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .CreateLogger();
    }
}

/// <summary>
/// Форматтер для записи логов в JSON.
/// </summary>
internal sealed class JsonLogFormatter : ITextFormatter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public void Format(LogEvent logEvent, TextWriter output)
    {
        var obj = new
        {
            timestamp = logEvent.Timestamp.ToString("o"),
            level = logEvent.Level.ToString(),
            messageId = logEvent.Properties.ContainsKey("MessageId") ? logEvent.Properties["MessageId"] : (object?)null,
            correlationId = logEvent.Properties.ContainsKey("CorrelationId") ? logEvent.Properties["CorrelationId"] : (object?)null,
            runId = logEvent.Properties.ContainsKey("RunId") ? logEvent.Properties["RunId"] : (object?)null,
            dryRun = logEvent.Properties.ContainsKey("DryRun") ? logEvent.Properties["DryRun"] : (object?)null,
            catchUp = logEvent.Properties.ContainsKey("CatchUp") ? logEvent.Properties["CatchUp"] : (object?)null,
            sourceContext = logEvent.Properties.ContainsKey("SourceContext") ? logEvent.Properties["SourceContext"] : (object?)null,
            message = logEvent.RenderMessage(),
            exception = logEvent.Exception?.ToString(),
            properties = logEvent.Properties
                .Where(p => p.Key is not ("MessageTemplate" or "SourceContext" or "Level" or "CorrelationId" or "RunId" or "MessageId" or "DryRun" or "CatchUp"))
                .ToDictionary(p => p.Key, p => p.Value.ToString())
        };

        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(obj, JsonOpts));
    }
}
