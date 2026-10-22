namespace ArtasTools.MonitorAgent.Abstractions;

/// <summary>
/// Оркестратор агента. Две реализации:
/// <list type="bullet">
///   <item><c>NativeAgentOrchestrator</c> — собственный ReAct-цикл поверх Microsoft.Extensions.AI;</item>
///   <item><c>SemanticKernelAgentOrchestrator</c> — на Semantic Kernel (фаза 5).</item>
/// </list>
/// Переключение — конфигурацией <c>Agent:Orchestrator</c>, без изменения прикладного кода.
/// </summary>
public interface IAgentOrchestrator
{
    /// <summary>Имя реализации: "native" | "semantic-kernel".</summary>
    string ProviderName { get; }

    /// <summary>
    /// Диагностирует инцидент по контексту прогона.
    /// Не бросает исключений: при сбое возвращает вердикт UNKNOWN.
    /// </summary>
    Task<Diagnosis> DiagnoseAsync(AgentContext context, CancellationToken ct = default);

    /// <summary>
    /// Строит план исправления по диагнозу.
    /// Возвращает <see cref="FixPlan.Empty"/>, если правки не требуются.
    /// </summary>
    Task<FixPlan> PlanFixAsync(
        Diagnosis diagnosis,
        AgentContext context,
        CancellationToken ct = default);
}

/// <summary>
/// Реестр инструментов — белый список того, что агент имеет право вызывать.
/// </summary>
public interface IToolRegistry
{
    /// <summary>Все зарегистрированные инструменты.</summary>
    IReadOnlyList<IAgentTool> Tools { get; }

    /// <summary>Находит инструмент по имени; null, если такого нет.</summary>
    IAgentTool? Find(string name);

    /// <summary>
    /// Вызов инструмента по имени с аудитом.
    /// Имя вне реестра — отказ без выполнения.
    /// </summary>
    Task<ToolCallRecord> InvokeAsync(
        string toolName,
        string argumentsJson,
        CancellationToken ct = default);
}
