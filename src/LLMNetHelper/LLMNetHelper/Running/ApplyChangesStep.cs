using LLMNetHelper.Abstractions;
using LLMNetHelper.CodeChange;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLMNetHelper.Running;

/// <summary>
/// Шаг: применение правок (ChangeTransaction).
/// </summary>
public sealed class ApplyChangesStep : IRunStep
{
    public string Name => "ApplyChanges";

    private readonly ChangeTransaction _changeTransaction;
    private readonly CodeChangeOptions _options;
    private readonly IOptions<AgentOptions> _agentOptions;
    private readonly IReportWriter _reportWriter;
    private readonly ILogger<ApplyChangesStep> _logger;

    public ApplyChangesStep(
        ChangeTransaction changeTransaction,
        IOptions<CodeChangeOptions> options,
        IOptions<AgentOptions> agentOptions,
        IReportWriter reportWriter,
        ILogger<ApplyChangesStep> logger)
    {
        _changeTransaction = changeTransaction;
        _options = options.Value;
        _agentOptions = agentOptions;
        _reportWriter = reportWriter;
        _logger = logger;
    }

    public async Task ExecuteAsync(RunContext context, CancellationToken ct)
    {
        var changeOutcome = ChangeOutcome.Skipped(
            ChangeFailure.Disabled,
            _options.Enabled
                ? "правок нет"
                : "CodeChange:Enabled=false — правки не применяются");

        if (context.FixPlan?.HasChanges == true && _options.Enabled)
        {
            changeOutcome = await _changeTransaction
                .ApplyAsync(context.RunId, context.FixPlan, ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Транзакция правок: {Failure}, ветка={Branch}, применено={Applied}",
                changeOutcome.Failure, changeOutcome.BranchName, changeOutcome.AppliedChanges);

            if (!string.IsNullOrWhiteSpace(changeOutcome.Diff))
            {
                context.PatchPath = Path.Combine(_agentOptions.Value.ReportsDir, $"patch_{context.RunId}.diff");
                await _reportWriter
                    .WriteSnapshotAsync(context.PatchPath, changeOutcome.Diff, ct)
                    .ConfigureAwait(false);
            }
        }

        context.ChangeOutcome = changeOutcome;
    }
}
