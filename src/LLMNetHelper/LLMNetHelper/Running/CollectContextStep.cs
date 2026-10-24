using LLMNetHelper.Abstractions;
using LLMNetHelper.Context;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLMNetHelper.Running;

/// <summary>
/// Шаг: поиск внешнего контекста по фактам инцидента.
/// </summary>
public sealed class CollectContextStep : IRunStep
{
    public string Name => "CollectContext";

    private readonly ContextAggregator _contextAggregator;
    private readonly ContextOptions _options;
    private readonly ILogger<CollectContextStep> _logger;

    public CollectContextStep(
        ContextAggregator contextAggregator,
        IOptions<ContextOptions> options,
        ILogger<CollectContextStep> logger)
    {
        _contextAggregator = contextAggregator;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ExecuteAsync(RunContext context, CancellationToken ct)
    {
        // ВАЖНО: проверка через `?.Count == 0` не ловила случай null-фактов
        // (null == 0 -> false), и последующее разыменование падало с NRE
        // (это же подсвечивал варнинг CS8602). Поэтому null-ветки явные.
        if (_options.Enabled is false ||
            context.LokiFacts is null || context.LokiFacts.ErrorSamples.Count == 0 ||
            context.PrometheusFacts is null || context.PrometheusFacts.MetricFacts.Count == 0)
        {
            _logger.LogInformation("Контекст не запрачивается: нет ошибок и аномальных метрик");
            context.ContextResult = new ContextSearchResult([], [], []);
            return;
        }

        var queryText = ContextQueryBuilder.FromFacts(
            context.LokiFacts.ErrorSamples,
            context.PrometheusFacts.MetricFacts);

        if (string.IsNullOrWhiteSpace(queryText))
        {
            _logger.LogInformation("Контекст не запрачивается: нет ошибок и аномальных метрик");
            context.ContextResult = new ContextSearchResult([], [], []);
            return;
        }

        var query = new ContextQuery(queryText, _options.TopK, _options.MaxChars);
        var result = await _contextAggregator.SearchAsync(query, ct).ConfigureAwait(false);
        context.ContextResult = result;

        _logger.LogInformation(
            "Контекст: {Count} документов из {Sources}",
            result.Documents.Count, result.QueriedSources.Count);
    }
}
