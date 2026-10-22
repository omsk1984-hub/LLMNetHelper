using System.Text.RegularExpressions;

namespace LLMNetHelper.Core.Snapshot;

/// <summary>
/// Redactor: вырезание секретов из сводки перед отправкой в LLM.
/// Страховка — пропускать нельзя, т.к. данные уходят наружу.
/// </summary>
public static class Redactor
{
    private static readonly Regex[] SecretPatterns =
    [
        // API keys: tapi_sk_..., etc.
        new Regex(@"tapi_sk_[A-Za-z0-9]+", RegexOptions.IgnoreCase),
        // password=xxx
        new Regex(@"(?i)(password|passwd|pwd)\s*=\s*[^\s;&,]+", RegexOptions.IgnoreCase),
        // Bearer tokens
        new Regex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase),
        // Connection strings with secrets
        new Regex(@"(?i)(Data Source|Server|Host|Endpoint)[^;]*;[^;]*(Password|PWD|Secret)[^;]*", RegexOptions.IgnoreCase),
        // JSON API keys
        new Regex(@"(?i)""(api[_-]?key|apikey|access_token)""\s*:\s*""[^""]+"""),
        // Azure storage keys
        new Regex(@"AccountKey=[A-Za-z0-9+/=]+"),
    ];

    /// <summary>
    /// Очищает текст от секретов, заменяя их на [REDACTED].
    /// </summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        var result = text;
        foreach (var pattern in SecretPatterns)
        {
            result = pattern.Replace(result, "[REDACTED]");
        }
        return result;
    }
}
