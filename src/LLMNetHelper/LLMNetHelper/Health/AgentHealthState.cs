namespace LLMNetHelper.Health;

/// <summary>
/// Состояние здоровья агента для healthcheck-эндпоинта.
/// </summary>
public sealed class AgentHealthState
{
    public string Status { get; set; } = "starting";
    public string? Error { get; set; }
    public DateTime? LastRun { get; set; }
    public int RunCount { get; set; }
}
