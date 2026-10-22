using System.Text.Json;

namespace ArtasTools.MonitorAgent.Infrastructure;

/// <summary>
/// Расширения для <see cref="JsonElement"/>: безопасное чтение массива.
/// </summary>
internal static class JsonElementExtensions
{
    /// <summary>
    /// Пытается получить значение как массив JSON-элементов.
    /// Возвращает false для не-массивов и для Undefined.
    /// </summary>
    public static bool TryGetArray(this JsonElement element, out JsonElement[] array)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            array = element.EnumerateArray().ToArray();
            return true;
        }

        array = [];
        return false;
    }
}
