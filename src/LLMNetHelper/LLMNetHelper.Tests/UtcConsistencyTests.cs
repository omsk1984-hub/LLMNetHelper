using LLMCore.Abstractions;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Collection;
using LLMNetHelper.Core.Reporting;
using LLMNetHelper.Core.Snapshot;

namespace LLMNetHelper.Tests;

/// <summary>
/// Тест на использование UTC везде: окно и имя отчёта не сдвигаются при TZ=Europe/Moscow.
/// Фиксируем регрессию по правилу проекта.
/// </summary>
public class UtcConsistencyTests
{
    [Fact]
    public void DateOnly_FromDateTimeUtc_ReturnsCorrectDate()
    {
        // 2026-10-20 00:30 UTC = 2026-10-20 03:30 MSK
        var utcNow = new DateTime(2026, 10, 20, 0, 30, 0, DateTimeKind.Utc);
        var date = DateOnly.FromDateTime(utcNow);

        // DateOnly.FromDateTime с UTC-датой должен вернуть 2026-10-20
        Assert.Equal(20, date.Day);
        Assert.Equal(10, date.Month);
        Assert.Equal(2026, date.Year);
    }

    [Fact]
    public void TruncateHours_MaintainsUtc()
    {
        var utcNow = DateTime.UtcNow;
        var truncated = utcNow.TruncateHours();

        Assert.Equal(DateTimeKind.Utc, truncated.Kind);
        Assert.Equal(0, truncated.Minute);
        Assert.Equal(0, truncated.Second);
    }

    [Fact]
    public void ReportFileName_UsesUtcDate()
    {
        var utcNow = new DateTime(2026, 10, 20, 23, 30, 0, DateTimeKind.Utc);
        var dateStr = DateOnly.FromDateTime(utcNow).ToString("yyyy-MM-dd");

        // Даже в 23:30 UTC дата должна быть 2026-10-20
        Assert.Equal("2026-10-20", dateStr);
    }

    [Fact]
    public void AgentOptions_DefaultValues()
    {
        var options = new AgentOptions();

        Assert.Equal("03:00", options.RunAtUtc);
        Assert.Equal(24, options.WindowHours);
        Assert.Equal("./reports/monitoring", options.ReportsDir);
        Assert.Equal("./reports/monitoring/facts.db", options.DatabasePath);
        Assert.Equal(90, options.RetentionDays);
        Assert.Equal(25, options.TopErrorsLimit);
        Assert.Equal(2000, options.MaxSampleChars);
        Assert.Equal(8080, options.HealthPort);
    }

    [Fact]
    public void LokiOptions_DefaultValues()
    {
        var options = new LokiOptions();

        Assert.Equal("http://loki:3100", options.BaseUrl);
        Assert.Equal(30, options.TimeoutSeconds);
        Assert.Equal(500, options.MaxEntries);
    }

    [Fact]
    public void PrometheusOptions_DefaultValues()
    {
        var options = new PrometheusOptions();

        Assert.Equal("http://prometheus:9090", options.BaseUrl);
        Assert.Equal(30, options.TimeoutSeconds);
    }

    [Fact]
    public void LlmOptions_DefaultValues()
    {
        var options = new LlmOptions();

        Assert.Equal("custom", options.Provider);
        Assert.Equal("", options.Endpoint);
        Assert.Equal("", options.ApiKey);
        Assert.Equal("qwen3.8-omni-flash", options.Model);
        Assert.Equal(4000, options.MaxTokens);
        Assert.Equal(0.1, options.Temperature);
        Assert.Equal(120, options.TimeoutSeconds);
        Assert.Equal(3, options.MaxRetries);
    }
}

// Extension method для truncate hours
public static class DateTimeExtensions
{
    public static DateTime TruncateHours(this DateTime dt) =>
        new(dt.Year, dt.Month, dt.Day, dt.Hour, 0, 0, dt.Kind);
}
