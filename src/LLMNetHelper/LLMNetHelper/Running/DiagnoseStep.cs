using LLMNetHelper.Abstractions;
using Microsoft.Extensions.Logging;

namespace LLMNetHelper.Running;

/// <summary>
/// Шаг: диагностика инцидента оркестратором.
/// </summary>
public sealed class DiagnoseStep : IRunStep
{
    public string Name => "Diagnose";

    private readonly IAgentOrchestrator _orchestrator;
    private readonly ILogger<DiagnoseStep> _logger;

    public DiagnoseStep(
        IAgentOrchestrator orchestrator,
        ILogger<DiagnoseStep> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    public async Task ExecuteAsync(RunContext context, CancellationToken ct)
    {
        var agentContext = new AgentContext(
            RunId: context.RunId,
            WindowFrom: context.WindowFrom,
            WindowTo: context.WindowTo,
            SnapshotText: context.SnapshotText!,
            ContextDocuments: context.ContextResult?.Documents ?? [],
            CodeStructureSummary: context.CodeStructureSummary);

        var diagnosis = await _orchestrator
            .DiagnoseAsync(agentContext, ct)
            .ConfigureAwait(false);

        context.Diagnosis = diagnosis;

        _logger.LogInformation(
            "Диагноз: {Verdict} ({Orchestrator}), вызовов инструментов: {Calls}",
            diagnosis.Verdict, diagnosis.Orchestrator, diagnosis.ToolCallCount);
    }
}
