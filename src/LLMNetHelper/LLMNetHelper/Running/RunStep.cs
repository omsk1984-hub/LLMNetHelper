using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Collection;
using LLMNetHelper.CodeChange;

namespace LLMNetHelper.Running;

/// <summary>
/// Контекст текущего прогона: передаётся между шагами.
/// </summary>
public sealed class RunContext
{
    public long RunId { get; set; }
    public DateTime WindowFrom { get; set; }
    public DateTime WindowTo { get; set; }
    public bool DryRun { get; set; }

    /// <summary>Вердикт прогона (итоговый, после коррекции по находкам), для финализации.</summary>
    public Verdict? RunVerdict { get; set; }

    // Facts
    public LokiFacts? LokiFacts { get; set; }
    public PrometheusFacts? PrometheusFacts { get; set; }

    // Snapshot
    public string? SnapshotText { get; set; }
    public string? SnapshotPath { get; set; }

    // Context
    public ContextSearchResult? ContextResult { get; set; }
    public string? CodeStructureSummary { get; set; }

    // Diagnosis
    public Diagnosis? Diagnosis { get; set; }
    public FixPlan? FixPlan { get; set; }

    // Changes
    public ChangeOutcome? ChangeOutcome { get; set; }

    // Report
    public string? PatchPath { get; set; }
}

/// <summary>
/// Шаг прогона агента.
/// </summary>
public interface IRunStep
{
    /// <summary>Краткое имя шага для логов.</summary>
    string Name { get; }

    /// <summary>
    /// Выполняет шаг. Бросает исключение при сбое — шаг отменяется.
    /// </summary>
    Task ExecuteAsync(RunContext context, CancellationToken ct);
}
