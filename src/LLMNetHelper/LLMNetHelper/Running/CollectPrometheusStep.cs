using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Collection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLMNetHelper.Running;

/// <summary>
/// Шаг: сбор фактов из Prometheus.
/// </summary>
public sealed class CollectPrometheusStep : IRunStep
{
    public string Name => "CollectPrometheus";

    private readonly IPrometheusClient _client;
    private readonly IOptions<PrometheusOptions> _prometheusOptions;
    private readonly ILogger<CollectPrometheusStep> _logger;

    public CollectPrometheusStep(
        IPrometheusClient client,
        IOptions<PrometheusOptions> prometheusOptions,
        ILogger<CollectPrometheusStep> logger)
    {
        _client = client;
        _prometheusOptions = prometheusOptions;
        _logger = logger;
    }

    public async Task ExecuteAsync(RunContext context, CancellationToken ct)
    {
        _logger.LogInformation("Сбор фактов из Prometheus");

        var collector = new PrometheusFactCollector(_client, _prometheusOptions);
        var facts = await collector.CollectAsync(context.WindowFrom, context.WindowTo, ct).ConfigureAwait(false);
        context.PrometheusFacts = facts;

        _logger.LogInformation(
            "Prometheus: {Metrics} метрик, {Alerts} алертов",
            facts.MetricFacts.Count, facts.Alerts.Count);
    }
}
