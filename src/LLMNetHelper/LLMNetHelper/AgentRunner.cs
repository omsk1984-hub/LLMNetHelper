using System.Text;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Agent.Orchestration;
using LLMNetHelper.CodeChange;
using LLMNetHelper.Context;
using LLMNetHelper.Core.Analysis;
using LLMNetHelper.Core.Collection;
using LLMNetHelper.Core.Reporting;
using LLMNetHelper.Core.Snapshot;
using LLMNetHelper.Running;
using LLMNetHelper.Roslyn;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLMNetHelper;

/// <summary>
/// Контракт прогона агента — позволяет подменять в тестах и вызывать из планировщика.
/// </summary>
public interface IAgentRunner
{
    /// <summary>
    /// Выполняет один прогон.
    /// </summary>
    /// <returns>Код возврата: 0 — OK/WARNING, 1 — сбой, 2 — CRITICAL.</returns>
    Task<int> RunAsync(
        bool dryRun,
        bool catchUp,
        DateTime? since,
        int? hours,
        CancellationToken ct);
}

/// <summary>
/// Один прогон агента: делегирует пайплайну, обрабатывает ошибки.
/// <para>
/// Отличия от прежнего <c>NightlyRunner</c>:
/// <list type="bullet">
///   <item>все зависимости приходят через DI, а не создаются через <c>new</c>;</item>
///   <item>пайплайн инкапсулирует шаги: сбор фактов → контекст → диагноз → план правки → отчёт;</item>
///   <item>ошибки шагов не роняют прогон полностью — обрабатываются на уровне шага;</item>
///   <item>опционально применяются правки через <see cref="ChangeTransaction"/>.</item>
/// </list>
/// </para>
/// </summary>
public sealed class AgentRunner : IAgentRunner
{
    private readonly IOptions<AgentOptions> _options;
    private readonly IAgentPipeline _pipeline;
    private readonly IFactStore _factStore;
    private readonly ILogger<AgentRunner> _logger;

    public AgentRunner(
        IOptions<AgentOptions> options,
        IAgentPipeline pipeline,
        IFactStore factStore,
        ILogger<AgentRunner> logger)
    {
        _options = options;
        _pipeline = pipeline;
        _factStore = factStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> RunAsync(
        bool dryRun,
        bool catchUp,
        DateTime? since,
        int? hours,
        CancellationToken ct)
    {
        // Окно анализа — всегда по UTC.
        var windowTo = DateTime.UtcNow;
        var windowFrom = since ?? windowTo.AddHours(-(hours ?? _options.Value.WindowHours));

        _logger.LogInformation(
            "Начало прогона агента: окно {From:o} — {To:o}, dry-run={DryRun}, catch-up={CatchUp}",
            windowFrom, windowTo, dryRun, catchUp);

        var runId = await _factStore.CreateRunAsync(
            windowFrom.ToString("o"), windowTo.ToString("o"), ct).ConfigureAwait(false);

        try
        {
            // Делегируем пайплайну
            var context = await _pipeline
                .ExecuteAsync(runId, windowFrom, windowTo, dryRun, ct)
                .ConfigureAwait(false);

            // Dry-run завершается после snapshot (шаг BuildSnapshot проверяет flag)
            if (dryRun)
            {
                await _factStore.FinishRunAsync(runId, "ok", "OK", null, ct).ConfigureAwait(false);
                return 0;
            }

            var verdict = context.Diagnosis?.Verdict ?? "UNKNOWN";
            _logger.LogInformation("Вердикт прогона: {Verdict}", verdict);

            return verdict == "CRITICAL" ? 2 : 0;
        }
        catch (OperationCanceledException)
        {
            await _factStore.FinishRunAsync(runId, "failed", null, "отменено", CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Сбой прогона агента");
            await _factStore.FinishRunAsync(runId, "failed", null, ex.Message, ct).ConfigureAwait(false);
            return 1;
        }
    }
}
