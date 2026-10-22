using Xunit;

namespace LLMNetHelper.Tests.Scheduling;

/// <summary>
/// Тесты планировщика: проверка расчёта следующего запуска.
/// </summary>
public class NightlyScheduleTests
{
    [Fact]
    public void CalculateNextRun_TodayBefore3AM_ReturnsToday()
    {
        var now = new DateTime(2026, 10, 20, 2, 0, 0, DateTimeKind.Utc);
        var runTime = new TimeSpan(3, 0, 0);

        var next = CalculateNextRun(runTime, now);

        Assert.Equal(2026, next.Year);
        Assert.Equal(10, next.Month);
        Assert.Equal(20, next.Day);
        Assert.Equal(3, next.Hour);
        Assert.Equal(0, next.Minute);
    }

    [Fact]
    public void CalculateNextRun_After3AM_ReturnsTomorrow()
    {
        var now = new DateTime(2026, 10, 20, 4, 0, 0, DateTimeKind.Utc);
        var runTime = new TimeSpan(3, 0, 0);

        var next = CalculateNextRun(runTime, now);

        Assert.Equal(2026, next.Year);
        Assert.Equal(10, next.Month);
        Assert.Equal(21, next.Day); // Завтра
        Assert.Equal(3, next.Hour);
    }

    [Fact]
    public void CalculateNextRun_EdgeOfDay_MidnightBoundary()
    {
        var now = new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc);
        var runTime = new TimeSpan(0, 0, 0); // Полночь

        var next = CalculateNextRun(runTime, now);

        Assert.Equal(2027, next.Year);
        Assert.Equal(1, next.Month);
        Assert.Equal(1, next.Day);
    }

    [Fact]
    public void CalculateNextRun_ReturnsUtcKind()
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var runTime = new TimeSpan(3, 0, 0);

        var next = CalculateNextRun(runTime, now);

        Assert.Equal(DateTimeKind.Utc, next.Kind);
    }

    // Вспомогательный метод (копируем логику из NightlyScheduleHostedService)
    private static DateTime CalculateNextRun(TimeSpan runTime, DateTime nowUtc)
    {
        var next = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day,
            runTime.Hours, runTime.Minutes, 0, DateTimeKind.Utc);

        if (next <= nowUtc)
            next = next.AddDays(1);

        return next;
    }
}
