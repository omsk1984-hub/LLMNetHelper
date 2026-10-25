namespace LLMNetHelper.Abstractions;

/// <summary>
/// Каталог инструментов — запросы к белому списку того, что агент имеет право вызывать
/// (план 22, этап 2.6). Извлечён из <see cref="IToolRegistry"/> по ISP: запросы
/// отделены от команды вызова с аудитом (<see cref="IToolInvoker"/>).
/// </summary>
public interface IToolCatalog
{
    /// <summary>Все зарегистрированные инструменты.</summary>
    IReadOnlyList<IAgentTool> Tools { get; }

    /// <summary>Находит инструмент по имени; null, если такого нет.</summary>
    IAgentTool? Find(string name);

    /// <summary>
    /// Формирует текст-описание доступных инструментов для системного промпта.
    /// Часть контракта каталога — оркестратор обязан использовать этот метод,
    /// а не собирать описание самому по <see cref="Tools"/> (устранение проверки
    /// типа <c>_registry is ToolRegistry</c>).
    /// </summary>
    string DescribeForPrompt();
}

/// <summary>
/// Вызов инструментов с аудитом (план 22, этап 2.6). Извлечён из
/// <see cref="IToolRegistry"/> по ISP: команда отделена от запросов
/// (<see cref="IToolCatalog"/>).
/// </summary>
public interface IToolInvoker
{
    /// <summary>
    /// Вызов инструмента по имени с аудитом.
    /// Имя вне реестра — отказ без выполнения.
    /// </summary>
    Task<ToolCallRecord> InvokeAsync(
        string toolName,
        string argumentsJson,
        CancellationToken ct = default);

    /// <summary>
    /// Сбрасывает журнал вызовов инструментов. Вызывается в начале каждого
    /// прогона: реестр — singleton, и без сброса журнал одного прогона
    /// подмешивается в следующий (дефект D4, план 18).
    /// </summary>
    void Reset();
}
