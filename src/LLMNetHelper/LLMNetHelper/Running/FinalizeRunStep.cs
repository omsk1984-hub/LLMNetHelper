using LLMNetHelper.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLMNetHelper.Running;

/// <summary>
/// Шаг финализации прогона (план 22, этап 4.5): уборка истории по retention и
/// закрытие прогона со статусом OK и вердиктом. Вынесен из
/// <c>GenerateReportStep</c>, который отвечает только за отчёты.
/// </summary>
public sealed class FinalizeRunStep : IRunStep
{
    public string Name => "FinalizeRun";

    private readonly IFactStore _factStore;
    private readonly IOptions<AgentOptions> _agentOptions;
    private readonly ILogger<FinalizeRunStep> _logger;

    public FinalizeRunStep(
        IFactStore factStore,
        IOptions<AgentOptions> agentOptions,
        ILogger<FinalizeRunStep> logger)
    {
        _factStore = factStore;
        _agentOptions = agentOptions;
        _logger = logger;
    }

    public async Task ExecuteAsync(RunContext context, CancellationToken ct)
    {
        // Уборка истории и финализация. Вердикт берётся из контекста: его
        // заполняет GenerateReportStep (тот же, что попал в отчёт).
        await _factStore.PurgeOldRunsAsync(_agentOptions.Value.RetentionDays, ct).ConfigureAwait(false);
        await _factStore.FinishRunAsync(context.RunId, RunStatus.Ok, context.RunVerdict, null, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Прогон {RunId} завершён: status={Status}, verdict={Verdict}",
            context.RunId, RunStatusFormat.ToWire(RunStatus.Ok),
            context.RunVerdict is { } v ? VerdictFormat.ToWire(v) : "null");
    }
}