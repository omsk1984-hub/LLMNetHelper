using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Snapshot;
using LLMNetHelper.Core.Reporting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLMNetHelper.Running;

/// <summary>
/// Шаг: формирование сводки фактов для LLM.
/// </summary>
public sealed class BuildSnapshotStep : IRunStep
{
    public string Name => "BuildSnapshot";

    private readonly SnapshotBuilder _snapshotBuilder;
    private readonly IReportWriter _reportWriter;
    private readonly IOptions<AgentOptions> _options;
    private readonly ILogger<BuildSnapshotStep> _logger;

    public BuildSnapshotStep(
        SnapshotBuilder snapshotBuilder,
        IReportWriter reportWriter,
        IOptions<AgentOptions> options,
        ILogger<BuildSnapshotStep> logger)
    {
        _snapshotBuilder = snapshotBuilder;
        _reportWriter = reportWriter;
        _options = options;
        _logger = logger;
    }

    public async Task ExecuteAsync(RunContext context, CancellationToken ct)
    {
        var snapshot = await _snapshotBuilder
            .BuildAsync(context.WindowFrom, context.WindowTo,
                context.LokiFacts!, context.PrometheusFacts!, ct)
            .ConfigureAwait(false);

        context.SnapshotText = snapshot.SnapshotText;

        var dateStr = DateOnly.FromDateTime(context.WindowTo).ToString("yyyy-MM-dd");
        context.SnapshotPath = Path.Combine(_options.Value.ReportsDir, $"snapshot_{dateStr}.md");
        await _reportWriter.WriteTextAsync(context.SnapshotPath, snapshot.SnapshotText, ct).ConfigureAwait(false);

        if (context.DryRun)
        {
            _logger.LogInformation("Dry-run: внешний контекст и LLM не запрашиваются");
            Console.WriteLine($"[DRY-RUN] Snapshot:\n{snapshot.SnapshotText}");
        }
    }
}
