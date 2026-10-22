using System.Text;
using System.Text.RegularExpressions;

namespace LLMNetHelper.Core.Snapshot;

/// <summary>
/// Нормализация текста ошибок: маскирование GUID, чисел, путей, symbol, batch_id.
/// Без этого группировка ошибок бессмысленна — каждая запись станет «уникальной».
/// </summary>
public static class ErrorSignatureNormalizer
{
    private static readonly Regex[] Patterns =
    [
        // GUID
        new Regex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase),
        // batch_id=xxx
        new Regex(@"batch_id=[^,\s;]+", RegexOptions.IgnoreCase),
        // symbol=xxx
        new Regex(@"symbol=[^,\s;]+", RegexOptions.IgnoreCase),
        // пути (Windows и Unix)
        new Regex(@"(?:[A-Z]:\\|/)[^\s;]+", RegexOptions.IgnoreCase),
        // hex-числа (длиннее 4 символов)
        new Regex(@"0x[0-9a-fA-F]{4,}"),
        // просто длинные числа
        new Regex(@"\b\d{10,}\b"),
        // email
        new Regex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}"),
    ];

    /// <summary>
    /// Нормализует текст, заменяя переменные части на плейсхолдеры.
    /// </summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        var result = text;
        foreach (var pattern in Patterns)
        {
            result = pattern.Replace(result, "<MASKED>");
        }
        return result;
    }

    /// <summary>
    /// Вычисляет SHA256 хеш нормализованного текста.
    /// </summary>
    public static string ComputeHash(string normalizedText)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(normalizedText);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
