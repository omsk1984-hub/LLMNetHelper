using LLMNetHelper.Abstractions;

namespace LLMNetHelper.Core.Snapshot;

/// <summary>
/// Результат применения budget guard к сводке.
/// </summary>
public sealed record SnapshotResult(
    string SnapshotText,
    List<ErrorSample> ErrorSamples,
    bool WasTrimmed);

/// <summary>
/// Контракт enforcement token budget для сводки.
/// </summary>
public interface ISnapshotBudgetEnforcer
{
    /// <summary>
    /// Проверяет бюджет токенов и применяет обрезку при необходимости.
    /// </summary>
    SnapshotResult Enforce(
        string snapshotText,
        int topErrorsLimit,
        int maxSampleChars,
        List<ErrorSample> errorSamples);
}
