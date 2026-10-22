using System.Text;
using LLMNetHelper.Abstractions;

namespace LLMNetHelper.Context;

/// <summary>
/// Строит поисковый запрос к источникам контекста из фактов инцидента.
/// Запрос детерминирован: одинаковый инцидент даёт одинаковый текст запроса
/// (важно для воспроизводимости ночного прогона).
/// </summary>
public static class ContextQueryBuilder
{
    /// <summary>
    /// Максимум ошибок, из которых берутся сигнатуры в запрос.
    /// </summary>
    private const int MaxErrorSignatures = 10;

    /// <summary>
    /// Строит запрос из названий ошибок и метрик.
    /// </summary>
    /// <param name="errorSignatures">Сигнатуры/сообщения ошибок.</param>
    /// <param name="metricNames">Имена метрик, вышедших за порог.</param>
    /// <param name="loggers">Имена логгеров/модулей.</param>
    public static string Build(
        IEnumerable<string>? errorSignatures = null,
        IEnumerable<string>? metricNames = null,
        IEnumerable<string>? loggers = null)
    {
        var sb = new StringBuilder();

        AppendDistinct(sb, errorSignatures, MaxErrorSignatures);
        AppendDistinct(sb, metricNames, MaxErrorSignatures);
        AppendDistinct(sb, loggers, MaxErrorSignatures);

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Строит запрос из уже собранных фактов Loki и Prometheus.
    /// </summary>
    public static string FromFacts(IEnumerable<ErrorSample>? errors, IEnumerable<MetricFact>? metrics)
    {
        var errorTexts = errors?
            .OrderByDescending(static e => e.Count)
            .Take(MaxErrorSignatures)
            .Select(static e => e.Signature)
            .Where(static s => !string.IsNullOrWhiteSpace(s));

        var metricTexts = metrics?
            .OrderByDescending(static m => Math.Abs(m.SlopePerHour))
            .Take(MaxErrorSignatures)
            .Select(static m => m.Name)
            .Where(static s => !string.IsNullOrWhiteSpace(s));

        return Build(errorTexts, metricTexts);
    }

    private static void AppendDistinct(StringBuilder sb, IEnumerable<string>? values, int limit)
    {
        if (values is null)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value))
            {
                continue;
            }

            sb.Append(value).Append(' ');
            added++;

            if (added >= limit)
            {
                break;
            }
        }
    }
}
