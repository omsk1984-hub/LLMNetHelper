using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Collection;
using Microsoft.Extensions.Options;

namespace LLMNetHelper.Core.Collection;

/// <summary>
/// Фабрика для создания коллекторов фактов.
/// Позволяет подменять реализацию в тестах и обеспечивает Dependency Inversion.
/// </summary>
public interface IFactCollectorFactory
{
    /// <summary>
    /// Создаёт коллектор для Loki.
    /// </summary>
    LokiFactCollector CreateLoki(ILokiClient client, IOptions<LokiOptions> lokiOptions, IOptions<AgentOptions> agentOptions);

    /// <summary>
    /// Создаёт коллектор для Prometheus.
    /// </summary>
    PrometheusFactCollector CreatePrometheus(IPrometheusClient client, IOptions<PrometheusOptions> options);
}

/// <summary>
/// Стандартная реализация фабрики.
/// </summary>
public sealed class FactCollectorFactory : IFactCollectorFactory
{
    public LokiFactCollector CreateLoki(ILokiClient client, IOptions<LokiOptions> lokiOptions, IOptions<AgentOptions> agentOptions)
    {
        return new LokiFactCollector(client, lokiOptions, agentOptions);
    }

    public PrometheusFactCollector CreatePrometheus(IPrometheusClient client, IOptions<PrometheusOptions> options)
    {
        return new PrometheusFactCollector(client, options);
    }
}
