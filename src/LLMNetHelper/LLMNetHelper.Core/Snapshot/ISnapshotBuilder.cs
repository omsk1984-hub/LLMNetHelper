using LLMNetHelper.Core.Collection;

namespace LLMNetHelper.Core.Snapshot;

/// <summary>
/// Контракт формирования компактной сводки фактов для LLM (план 22, этап 2.5).
/// Извлечён из <see cref="SnapshotBuilder"/>, чтобы шаг пайплайна
/// (<c>BuildSnapshotStep</c>) зависел от абстракции, а не от конкретного класса.
/// <para>
/// Размещён в Core.Snapshot (не в Abstractions): сигнатура использует типы
/// фактов из <c>Core.Collection</c> (<see cref="LokiFacts"/>, <see cref="PrometheusFacts"/>),
/// их утечка в общий слой абстракций нарушила бы границы (план 22, дефект N5).
/// </para>
/// </summary>
public interface ISnapshotBuilder
{
    /// <summary>
    /// Формирует компактную сводку из собранных фактов.
    /// </summary>
    Task<SnapshotResult> BuildAsync(
        DateTime windowFrom,
        DateTime windowTo,
        LokiFacts lokiFacts,
        PrometheusFacts promFacts,
        CancellationToken ct = default);
}
