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

    /// <summary>
    /// Предметное описание анализируемой системы для системного промпта.
    /// <para>
    /// Доменная картина мира (что за система, какие у неё подсистемы и хранилища)
    /// является частью развёртывания, а не кода: при пустом значении агент
    /// получает нейтральное описание и строит гипотезы только на фактах из
    /// сводки и документации. Заполняется в конфигурации
    /// (<c>Agent:Orchestration:DomainContext</c> / <c>AGENT__ORCHESTRATION__DOMAINCONTEXT</c>).
    /// </para>
    /// </summary>
    public string DomainContext { get; set; } = "";
}
