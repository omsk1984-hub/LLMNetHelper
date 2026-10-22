using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Collection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLMNetHelper.Running;

/// <summary>
/// Шаг: сбор фактов из Loki.
/// </summary>
public sealed class CollectLokiStep : IRunStep
{
    public string Name => "CollectLoki";

    private readonly ILokiClient _client;
    private readonly IOptions<LokiOptions> _lokiOptions;
    private readonly IOptions<AgentOptions> _agentOptions;
    private readonly ILogger<CollectLokiStep> _logger;

    public CollectLokiStep(
        ILokiClient client,
        IOptions<LokiOptions> lokiOptions,
        IOptions<AgentOptions> agentOptions,
        ILogger<CollectLokiStep> logger)
    {
        _client = client;
        _lokiOptions = lokiOptions;
        _agentOptions = agentOptions;
        _logger = logger;
    }

    public async Task ExecuteAsync(RunContext context, CancellationToken ct)
    {
        _logger.LogInformation("Сбор фактов из Loki");

        var collector = new LokiFactCollector(
            _client, _lokiOptions, _agentOptions);

        var facts = await collector.CollectAsync(context.WindowFrom, context.WindowTo, ct).ConfigureAwait(false);
        context.LokiFacts = facts;

        _logger.LogInformation(
            "Loki: {LogFacts} агрегатов, {Errors} ошибок",
            facts.LogFacts.Count, facts.ErrorSamples.Count);
    }
}
