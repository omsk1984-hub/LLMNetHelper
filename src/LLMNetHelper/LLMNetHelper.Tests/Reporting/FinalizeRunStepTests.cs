using LLMNetHelper.Abstractions;
using LLMNetHelper.Running;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMNetHelper.Tests.Reporting;

/// <summary>
/// Тесты шага финализации прогона (план 22, этап 4.5): после выноса из
/// GenerateReportStep шаг выполняет уборку истории и закрытие прогона,
/// используя вердикт из контекста (переданный шагом отчёта).
/// </summary>
public class FinalizeRunStepTests
{
    [Fact]
    public async Task ExecuteAsync_PurgesAndFinishesWithRunVerdict()
    {
        var store = new RecordingFactStore();
        var step = new FinalizeRunStep(
            store,
            Options.Create(new AgentOptions { RetentionDays = 30 }),
            NullLogger<FinalizeRunStep>.Instance);

        await step.ExecuteAsync(new RunContext
        {
            RunId = 42,
            RunVerdict = Verdict.Warning
        }, CancellationToken.None);

        Assert.True(store.PurgeCalled);
        Assert.Equal(30, store.PurgeRetentionDays);
        Assert.True(store.FinishCalled);
        Assert.Equal(42, store.FinishedRunId);
        Assert.Equal(RunStatus.Ok, store.FinishedStatus);
        Assert.Equal(Verdict.Warning, store.FinishedVerdict);
    }

    [Fact]
    public async Task ExecuteAsync_NullVerdict_PassesNull()
    {
        var store = new RecordingFactStore();
        var step = new FinalizeRunStep(
            store,
            Options.Create(new AgentOptions()),
            NullLogger<FinalizeRunStep>.Instance);

        await step.ExecuteAsync(new RunContext { RunId = 1 }, CancellationToken.None);

        Assert.Null(store.FinishedVerdict);
    }

    private sealed class RecordingFactStore : IFactStore
    {
        public bool PurgeCalled { get; private set; }
        public int PurgeRetentionDays { get; private set; }
        public bool FinishCalled { get; private set; }
        public long FinishedRunId { get; private set; }
        public RunStatus FinishedStatus { get; private set; }
        public Verdict? FinishedVerdict { get; private set; }

        public Task<int> PurgeOldRunsAsync(int retentionDays, CancellationToken ct = default)
        {
            PurgeCalled = true;
            PurgeRetentionDays = retentionDays;
            return Task.FromResult(0);
        }

        public Task FinishRunAsync(long runId, RunStatus status, Verdict? verdict, string? error, CancellationToken ct = default)
        {
            FinishCalled = true;
            FinishedRunId = runId;
            FinishedStatus = status;
            FinishedVerdict = verdict;
            return Task.CompletedTask;
        }

        public Task<long> CreateRunAsync(DateTime windowFrom, DateTime windowTo, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SaveLogFactsAsync(long runId, IEnumerable<LogFact> facts, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SaveErrorSamplesAsync(long runId, IEnumerable<ErrorSample> samples, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SaveMetricFactsAsync(long runId, IEnumerable<MetricFact> facts, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SaveLlmRunAsync(long runId, LlmRunInfo info, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<RunInfo?> GetLastSuccessfulRunAsync(int hoursBack, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<List<ErrorSample>> GetPreviousErrorSamplesAsync(long runId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<List<LogFact>> GetPreviousLogFactsAsync(long runId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}