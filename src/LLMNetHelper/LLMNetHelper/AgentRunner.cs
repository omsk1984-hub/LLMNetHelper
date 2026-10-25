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
using Serilog.Context;

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
    private readonly IToolRegistry _toolRegistry;
    private readonly ILogger<AgentRunner> _logger;

    public AgentRunner(
        IOptions<AgentOptions> options,
        IAgentPipeline pipeline,
        IFactStore factStore,
        IToolRegistry toolRegistry,
        ILogger<AgentRunner> logger)
    {
        _options = options;
        _pipeline = pipeline;
        _factStore = factStore;
        _toolRegistry = toolRegistry;
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

        // Дефект D4 (план 18): реестр инструментов — singleton, журнал вызовов
        // обязан обнуляться в начале каждого прогона.
        _toolRegistry.Reset();

        // Корреляция логов с прогоном
        using (LogContext.PushProperty("RunId", (long?)null))
        using (LogContext.PushProperty("DryRun", dryRun))
        using (LogContext.PushProperty("CatchUp", catchUp))
        {
            _logger.LogInformation(
                "Начало прогона агента: окно {From:o} — {To:o}, dry-run={DryRun}, catch-up={CatchUp}",
                windowFrom, windowTo, dryRun, catchUp);

            var runId = await _factStore.CreateRunAsync(
                windowFrom.ToString("o"), windowTo.ToString("o"), ct).ConfigureAwait(false);

            // Обновляем correlation scope с реальным runId
            using (LogContext.PushProperty("RunId", runId))
            {
                try
                {
                    // Делегируем пайплайну
                    var context = await _pipeline
                        .ExecuteAsync(runId, windowFrom, windowTo, dryRun, ct)
                        .ConfigureAwait(false);

                    // Dry-run завершается после snapshot (шаг BuildSnapshot проверяет flag)
                    if (dryRun)
                    {
                        await _factStore.FinishRunAsync(runId, RunStatus.Ok, Verdict.Ok, null, ct).ConfigureAwait(false);
                        return 0;
                    }

                    var verdict = context.Diagnosis?.Verdict ?? Verdict.Unknown;
                    _logger.LogInformation("Вердикт прогона: {Verdict}", VerdictFormat.ToWire(verdict));

                    return verdict == Verdict.Critical ? 2 : 0;
                }
                catch (OperationCanceledException)
                {
                    await _factStore.FinishRunAsync(runId, RunStatus.Failed, null, "отменено", CancellationToken.None)
                        .ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Сбой прогона агента");
                    await _factStore.FinishRunAsync(runId, RunStatus.Failed, null, ex.Message, ct).ConfigureAwait(false);
                    return 1;
                }
            }
        }
    }
}
