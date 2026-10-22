using System.Text;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Collection;
using Microsoft.Extensions.Options;

namespace LLMNetHelper.Core.Snapshot;

/// <summary>
/// Формирование компактной сводки фактов для отправки в LLM.
/// Порядок: метаданные → агрегаты → тренды → топ-N ошибок → дифф.
/// </summary>
public class SnapshotBuilder
{
    private readonly AgentOptions _options;
    private readonly IFactStore _factStore;

    public SnapshotBuilder(
        IOptions<AgentOptions> options,
        IFactStore factStore)
    {
        _options = options.Value;
        _factStore = factStore;
    }

    /// <summary>
    /// Формирует компактную сводку из собранных фактов.
    /// </summary>
    public async Task<SnapshotResult> BuildAsync(
        DateTime windowFrom,
        DateTime windowTo,
        LokiFacts lokiFacts,
        PrometheusFacts promFacts,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();

        // 1. Метаданные прогона
        sb.AppendLine("# Сводка для LLM-анализа");
        sb.AppendLine();
        sb.AppendLine($"**Окно анализа:** {windowFrom:yyyy-MM-dd HH:mm} UTC — {windowTo:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"**Время генерации:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"**Записей Loki:** {lokiFacts.LogFacts.Count} агрегатов, {lokiFacts.ErrorSamples.Count} уникальных ошибок");
        sb.AppendLine($"**Метрик Prometheus:** {promFacts.MetricFacts.Count}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // 2. Агрегаты логов
        sb.AppendLine("## Агрегаты логов");
        sb.AppendLine();
        sb.AppendLine("| Уровень | Job | Logger | Символ | Счётчик |");
        sb.AppendLine("|---------|-----|--------|--------|---------|");

        foreach (var fact in lokiFacts.LogFacts.Take(100))
        {
            sb.AppendLine($"| {fact.Lvl} | {fact.Job} | {fact.Logger} | {fact.Symbol} | {fact.Count} |");
        }
        sb.AppendLine();

        // 3. Тренды метрик
        var trendFacts = promFacts.MetricFacts.Where(m => m.SlopePerHour != 0 || m.R2 > 0).ToList();
        if (trendFacts.Count > 0)
        {
            sb.AppendLine("## Тренды метрик");
            sb.AppendLine();
            sb.AppendLine("| Метрика | Значение | Slope/ч | R² |");
            sb.AppendLine("|---------|----------|---------|-----|");

            foreach (var fact in trendFacts)
            {
                sb.AppendLine($"| {fact.Name} | {fact.Value:F0} | {fact.SlopePerHour:F2} | {fact.R2:F3} |");
            }
            sb.AppendLine();
        }

        // 4. Топ-N уникальных ошибок
        var topErrors = lokiFacts.ErrorSamples
            .OrderByDescending(e => e.Count)
            .Take(_options.TopErrorsLimit)
            .ToList();

        if (topErrors.Count > 0)
        {
            sb.AppendLine("## Топ уникальных ошибок");
            sb.AppendLine();

            for (var i = 0; i < topErrors.Count; i++)
            {
                var err = topErrors[i];
                var sampleText = err.SampleText.Length > _options.MaxSampleChars
                    ? err.SampleText.Substring(0, _options.MaxSampleChars) + "..."
                    : err.SampleText;

                sb.AppendLine($"### {i + 1}. [{err.Count}x] {err.Signature.Substring(0, Math.Min(40, err.Signature.Length))}...");
                sb.AppendLine();
                sb.AppendLine("```\n" + Redactor.Redact(sampleText) + "\n```");
                sb.AppendLine();
            }
        }

        // 5. Дифф с прошлым прогоном
        var previousRun = await _factStore.GetLastSuccessfulRunAsync(48, ct);
        if (previousRun != null && previousRun.Id != 0)
        {
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## Дифф с прошлым прогоном");
            sb.AppendLine();

            var previousErrors = await _factStore.GetPreviousErrorSamplesAsync(previousRun.Id, ct);
            var currentSignatures = new HashSet<string>(topErrors.Select(e => e.Signature));
            var previousSignatures = new HashSet<string>(previousErrors.Select(e => e.Signature));

            var newErrors = currentSignatures.Except(previousSignatures).ToList();
            var disappearedErrors = previousSignatures.Except(currentSignatures).ToList();

            if (newErrors.Count > 0)
            {
                sb.AppendLine($"**Новые ошибки:** {newErrors.Count}");
                foreach (var sig in newErrors.Take(10))
                    sb.AppendLine($"- `{sig}`");
                sb.AppendLine();
            }

            if (disappearedErrors.Count > 0)
            {
                sb.AppendLine($"**Исчезнувшие ошибки:** {disappearedErrors.Count}");
                foreach (var sig in disappearedErrors.Take(10))
                    sb.AppendLine($"- `{sig}`");
                sb.AppendLine();
            }

            if (newErrors.Count == 0 && disappearedErrors.Count == 0)
                sb.AppendLine("Значимых изменений относительно прошлого прогона не обнаружено.");
            sb.AppendLine();
        }

        // Применяем TokenBudgetGuard
        var snapshotText = sb.ToString();
        var budgetResult = TokenBudgetGuard.EnforceBudget(
            snapshotText,
            _options.TopErrorsLimit,
            _options.MaxSampleChars,
            topErrors,
            _options);

        return budgetResult;
    }
}
