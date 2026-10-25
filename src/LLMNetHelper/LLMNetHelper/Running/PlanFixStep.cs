using LLMNetHelper.Abstractions;
using LLMNetHelper.Agent.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLMNetHelper.Running;

/// <summary>
/// Шаг: планирование правки (только при CRITICAL/WARNING).
/// </summary>
public sealed class PlanFixStep : IRunStep
{
    public string Name => "PlanFix";

    private readonly IAgentOrchestrator _orchestrator;
    private readonly AgentOrchestrationOptions _options;
    private readonly ILogger<PlanFixStep> _logger;

    public PlanFixStep(
        IAgentOrchestrator orchestrator,
        IOptions<AgentOrchestrationOptions> options,
        ILogger<PlanFixStep> logger)
    {
        _orchestrator = orchestrator;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ExecuteAsync(RunContext context, CancellationToken ct)
    {
        if (_options.EnableFixPlanning is false ||
            context.Diagnosis?.Verdict is not (Verdict.Critical or Verdict.Warning))
        {
            context.FixPlan = FixPlan.Empty;
            return;
        }

        var agentContext = new AgentContext(
            RunId: context.RunId,
            WindowFrom: context.WindowFrom,
            WindowTo: context.WindowTo,
            SnapshotText: context.SnapshotText!,
            ContextDocuments: context.ContextResult?.Documents ?? [],
            CodeStructureSummary: context.CodeStructureSummary);

        context.FixPlan = await _orchestrator
            .PlanFixAsync(context.Diagnosis, agentContext, ct)
            .ConfigureAwait(false);
    }
}
