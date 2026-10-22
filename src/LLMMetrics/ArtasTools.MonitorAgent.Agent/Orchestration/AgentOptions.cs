namespace ArtasTools.MonitorAgent.Agent.Orchestration;

/// <summary>
/// Параметры агентской оркестрации.
/// </summary>
public sealed class AgentOrchestrationOptions
{
    /// <summary>Секция конфигурации.</summary>
    public const string SectionName = "Agent:Orchestration";

    /// <summary>
    /// Реализация оркестратора: "native" | "semantic-kernel".
    /// </summary>
    public string Orchestrator { get; set; } = "native";

    /// <summary>Максимум шагов ReAct-цикла (одна итерация = один ответ модели).</summary>
    public int MaxSteps { get; set; } = 8;

    /// <summary>Максимум вызовов инструментов за прогон.</summary>
    public int ToolCallLimit { get; set; } = 30;

    /// <summary>
    /// Разрешать ли агенту этап планирования правки.
    /// При false агент только диагностирует.
    /// </summary>
    public bool EnableFixPlanning { get; set; } = true;
}
