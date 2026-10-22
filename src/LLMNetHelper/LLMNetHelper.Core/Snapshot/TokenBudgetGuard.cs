using LLMNetHelper.Abstractions;

namespace LLMNetHelper.Core.Snapshot;

/// <summary>
/// Реализация <see cref="ISnapshotBudgetEnforcer"/>.
/// Порядок обрезки: sample_text → TopErrorsLimit → редкие ошибки.
/// Агрегаты не режутся никогда.
/// </summary>
public sealed class TokenBudgetGuard : ISnapshotBudgetEnforcer
{
    private static readonly TokenBudgetGuard _instance = new();

    /// <summary>
    /// Экземпляр для статического доступа (тесты).
    /// </summary>
    public static TokenBudgetGuard Instance => _instance;

    // Ориентировочная оценка: ~4 символа на токен (для английского/русского)
    private const int CharsPerToken = 4;
    private const int MaxTokens = 6000; // безопасный бюджет для компактной сводки

    /// <inheritdoc />
    public SnapshotResult Enforce(
        string snapshotText,
        int topErrorsLimit,
        int maxSampleChars,
        List<ErrorSample> errorSamples)
    {
        var estimatedTokens = snapshotText.Length / CharsPerToken;

        if (estimatedTokens <= MaxTokens)
            return new SnapshotResult(snapshotText, errorSamples, false);

        // Шаг 1: режем sample_text до 800 символов
        var reducedSamples = errorSamples.Select(s =>
        {
            var text = s.SampleText;
            if (text.Length > 800)
                text = text.Substring(0, 700) + "\n... [truncated]";
            return new ErrorSample
            {
                Signature = s.Signature,
                SampleText = text,
                Count = s.Count,
                FirstSeen = s.FirstSeen,
                LastSeen = s.LastSeen,
                Lvl = s.Lvl,
                Job = s.Job,
                Logger = s.Logger,
                Symbol = s.Symbol
            };
        }).ToList();

        var rebuiltSnapshot = RebuildSnapshot(snapshotText, maxSampleChars: 800);
        estimatedTokens = rebuiltSnapshot.Length / CharsPerToken;

        if (estimatedTokens <= MaxTokens)
            return new SnapshotResult(rebuiltSnapshot, reducedSamples, true);

        // Шаг 2: уменьшаем TopErrorsLimit
        var reducedLimit = Math.Max(5, topErrorsLimit / 2);
        var limitedSamples = reducedSamples.Take(reducedLimit).ToList();

        rebuiltSnapshot = RebuildSnapshot(snapshotText, maxSampleChars: 800, topErrorsLimit: reducedLimit);
        estimatedTokens = rebuiltSnapshot.Length / CharsPerToken;

        if (estimatedTokens <= MaxTokens)
            return new SnapshotResult(rebuiltSnapshot, limitedSamples, true);

        // Шаг 3: отбрасываем редкие ошибки (count = 1)
        var frequentSamples = limitedSamples.Where(s => s.Count > 1).ToList();
        rebuiltSnapshot = RebuildSnapshot(snapshotText, maxSampleChars: 800, topErrorsLimit: reducedLimit, keepFrequentOnly: true);

        return new SnapshotResult(rebuiltSnapshot, frequentSamples, true);
    }

    private static string RebuildSnapshot(
        string snapshotText,
        int? maxSampleChars = null,
        int? topErrorsLimit = null,
        bool keepFrequentOnly = false)
    {
        var estimatedTokens = snapshotText.Length / CharsPerToken;
        if (estimatedTokens <= MaxTokens)
            return snapshotText;

        // Фолбэк: обрезаем до безопасного размера
        var maxChars = MaxTokens * CharsPerToken;
        if (snapshotText.Length > maxChars)
            return snapshotText.Substring(0, maxChars) + "\n\n... [snapshot truncated due to token budget]";

        return snapshotText;
    }
}
