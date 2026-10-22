namespace LLMNetHelper.Core.Collection;

/// <summary>
/// Базовый класс для коллекторов фактов.
/// Реализует шаблонный метод <c>CollectAsync</c> с общей логикой:
/// обработка <c>CancellationToken</c>, агрегация результатов.
/// </summary>
/// <typeparam name="TLogFact">Тип факта лога (LogFact, MetricFact и т.д.).</typeparam>
/// <typeparam name="TResult">Тип результата коллектора.</typeparam>
public abstract class FactCollector<TLogFact, TResult>
    where TLogFact : class
    where TResult : class
{
    /// <summary>
    /// Шаблонный метод: собирает все факты за окно.
    /// </summary>
    public async Task<TResult> CollectAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var result = await CollectCoreAsync(from, to, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Ядро сбора — реализуется в потомках.
    /// </summary>
    protected abstract Task<TResult> CollectCoreAsync(DateTime from, DateTime to, CancellationToken ct);
}
