using LLMNetHelper.Abstractions;
using Microsoft.Extensions.Logging;

namespace LLMNetHelper.Running;

/// <summary>
/// Пайплайн прогона агента: последовательное выполнение шагов.
/// </summary>
public interface IAgentPipeline
{
    Task<RunContext> ExecuteAsync(
        long runId,
        DateTime windowFrom,
        DateTime windowTo,
        bool dryRun,
        CancellationToken ct);
}

/// <summary>
/// Реализация пайплайна.
/// </summary>
public sealed class AgentPipeline : IAgentPipeline
{
    private readonly IEnumerable<IRunStep> _steps;
    private readonly IFactStore _factStore;
    private readonly ILogger<AgentPipeline> _logger;

    public AgentPipeline(
        IEnumerable<IRunStep> steps,
        IFactStore factStore,
        ILogger<AgentPipeline> logger)
    {
        _steps = steps;
        _factStore = factStore;
        _logger = logger;
    }

    public async Task<RunContext> ExecuteAsync(
        long runId,
        DateTime windowFrom,
        DateTime windowTo,
        bool dryRun,
        CancellationToken ct)
    {
        var context = new RunContext
        {
            RunId = runId,
            WindowFrom = windowFrom,
            WindowTo = windowTo,
            DryRun = dryRun
        };

        foreach (var step in _steps)
        {
            ct.ThrowIfCancellationRequested();

            _logger.LogDebug("Шаг прогона: {Step}", step.Name);

            try
            {
                await step.ExecuteAsync(context, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой шага прогона: {Step}", step.Name);
                throw;
            }
        }

        return context;
    }
}
