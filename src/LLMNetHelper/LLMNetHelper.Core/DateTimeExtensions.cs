namespace LLMNetHelper.Core;

/// <summary>
/// Вспомогательные методы расширения для DateTime.
/// </summary>
public static class DateTimeExtensions
{
    /// <summary>
    /// Обрезает DateTime до начала часа.
    /// </summary>
    public static DateTime TruncateHours(this DateTime dt) =>
        new(dt.Year, dt.Month, dt.Day, dt.Hour, 0, 0, dt.Kind);
}
