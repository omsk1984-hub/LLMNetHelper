namespace LLMNetHelper.Abstractions;

/// <summary>
/// Хранилище фактов в SQLite: сохранение, чтение, очистка по retention.
/// <para>
/// План 22, этап 2.7: фасад над сегрегированными репозиториями
/// (<see cref="IRunRepository"/>, <see cref="ILogFactRepository"/>,
/// <see cref="IErrorSampleRepository"/>, <see cref="IMetricFactRepository"/>,
/// <see cref="ILlmRunRepository"/>). Потребители должны зависеть от узкого
/// контракта, а не от фасада; фасад сохранён для постепенной миграции.
/// </para>
/// <para>
/// Из контракта убраны <c>IDisposable</c> (LSP: не каждая реализация обязана
/// утилизироваться — жизненным циклом владеет DI) и <c>InitializeAsync</c>
/// (временно́е зацепление: инициализация — внутренняя деталь реализации,
/// применяется лениво при первой операции).
/// </para>
/// </summary>
public interface IFactStore :
    IRunRepository,
    ILogFactRepository,
    IErrorSampleRepository,
    IMetricFactRepository,
    ILlmRunRepository
{
}
