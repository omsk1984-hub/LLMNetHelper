namespace ArtasTools.MonitorAgent.Abstractions;

/// <summary>
/// Инструмент агента. Один контракт для обоих оркестраторов:
/// нативный оборачивает его в функцию Microsoft.Extensions.AI,
/// Semantic Kernel — в <c>KernelFunction</c>.
/// <para>
/// Инструменты регистрируются в белом списке (<c>ToolRegistry</c>);
/// агент не может вызвать то, чего там нет.
/// </para>
/// </summary>
public interface IAgentTool
{
    /// <summary>Уникальное имя инструмента (snake_case, как в схемах LLM).</summary>
    string Name { get; }

    /// <summary>Описание для модели: что делает и когда применять.</summary>
    string Description { get; }

    /// <summary>JSON-схема параметров (совместима с JSON Schema / tool calling).</summary>
    string ParametersJsonSchema { get; }

    /// <summary>
    /// Выполняет инструмент. Аргументы — JSON-строка по схеме <see cref="ParametersJsonSchema"/>.
    /// Возвращает текст результата; исключений наружу не бросает.
    /// </summary>
    Task<string> InvokeAsync(string parametersJson, CancellationToken ct = default);
}

/// <summary>
/// Результат вызова инструмента (для аудита).
/// </summary>
/// <param name="ToolName">Имя инструмента.</param>
/// <param name="Arguments">Аргументы (JSON).</param>
/// <param name="ResultChars">Длина результата в символах.</param>
/// <param name="DurationMs">Длительность в миллисекундах.</param>
/// <param name="Succeeded">Успешно ли выполнен.</param>
public sealed record ToolCallRecord(
    string ToolName,
    string Arguments,
    int ResultChars,
    int DurationMs,
    bool Succeeded);
