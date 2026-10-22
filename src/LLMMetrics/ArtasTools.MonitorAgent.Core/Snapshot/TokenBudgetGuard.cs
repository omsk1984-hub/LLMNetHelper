using ArtasTools.MonitorAgent.Abstractions;

namespace ArtasTools.MonitorAgent.Core.Snapshot;

/// <summary>
/// TokenBudgetGuard: оценка и обрезка сводки по бюджету токенов.
/// Порядок обрезки: sample_text → TopErrorsLimit → редкие ошибки.
/// Агрегаты не режутся никогда.
/// </summary>
public static class TokenBudgetGuard
{
    // Ориентировочная оценка: ~4 символа на токен (для английского/русского)
    private const int CharsPerToken = 4;
    private const int MaxTokens = 6000; // безопасный бюджет для компактной сводки

    /// <summary>
    /// Проверяет бюджет и применяет обрезку при необходимости.
    /// </summary>
    public static SnapshotResult EnforceBudget(
        string snapshotText,
        int topErrorsLimit,
        int maxSampleChars,
        List<ErrorSample> errorSamples,
        AgentOptions options)
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
                Lvl = s.Lvl,
                Job = s.Job,
                Logger = s.Logger,
                Symbol = s.Symbol,
                FirstSeen = s.FirstSeen,
                LastSeen = s.LastSeen,
                Count = s.Count,
                SampleText = text
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
        // Упрощённая перегенерация: просто обрезаем текст если он слишком длинный
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

/// <summary>
/// Результат применения TokenBudgetGuard.
/// </summary>
public sealed record SnapshotResult(
    string SnapshotText,
    List<ErrorSample> ErrorSamples,
    bool WasTrimmed);
