namespace ArtasTools.MonitorAgent.Context;

/// <summary>
/// Компоновщик результатов по всем зарегистрированным источникам контекста.
/// Отвечает за:
/// <list type="bullet">
///   <item>опрос источников с изоляцией сбоев (один упавший не ломает остальные);</item>
///   <item>склейку, дедупликацию и общий бюджет символов.</item>
/// </list>
/// </summary>
public sealed class ContextAggregator
{
    private readonly IReadOnlyList<Abstractions.IContextSource> _sources;
    private readonly ContextOptions _options;

    public ContextAggregator(
        IEnumerable<Abstractions.IContextSource> sources,
        ContextOptions options)
    {
        _sources = sources.ToArray();
        _options = options;
    }

    /// <summary>
    /// Список зарегистрированных источников (для диагностики и отчёта).
    /// </summary>
    public IReadOnlyList<string> SourceNames => _sources.Select(static s => s.Name).ToArray();

    /// <summary>
    /// Ищет контекст по всем доступным источникам.
    /// </summary>
    public async Task<Abstractions.ContextSearchResult> SearchAsync(
        Abstractions.ContextQuery query,
        CancellationToken ct = default)
    {
        if (!_options.Enabled || _sources.Count == 0 || string.IsNullOrWhiteSpace(query.Text))
        {
            return new Abstractions.ContextSearchResult([], [], []);
        }

        var queried = new List<string>();
        var unavailable = new List<string>();
        var collected = new List<Abstractions.ContextDocument>();

        foreach (var source in _sources)
        {
            ct.ThrowIfCancellationRequested();

            bool available;
            try
            {
                available = await source.IsAvailableAsync(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Источник обязан сам гасить свои ошибки, но подстрахуемся.
                available = false;
            }

            if (!available)
            {
                unavailable.Add(source.Name);
                continue;
            }

            queried.Add(source.Name);

            try
            {
                var documents = await source.SearchAsync(query, ct).ConfigureAwait(false);
                collected.AddRange(documents);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Сбой одного источника не должен ронять прогон.
                unavailable.Add(source.Name);
            }
        }

        var limited = ApplyBudget(collected);

        return new Abstractions.ContextSearchResult(limited, queried, unavailable);
    }

    /// <summary>
    /// Дедупликация по (Source, Id) с сохранением лучшей оценки,
    /// сортировка по убыванию и обрезка по общему бюджету символов.
    /// </summary>
    private IReadOnlyList<Abstractions.ContextDocument> ApplyBudget(
        IReadOnlyList<Abstractions.ContextDocument> documents)
    {
        var best = new Dictionary<(string Source, string Id), Abstractions.ContextDocument>();

        foreach (var doc in documents)
        {
            var key = (doc.Source, doc.Id);
            if (!best.TryGetValue(key, out var existing) || doc.Score > existing.Score)
            {
                best[key] = doc;
            }
        }

        var ordered = best.Values
            .OrderByDescending(static d => d.Score)
            .ThenBy(static d => d.Source, StringComparer.Ordinal)
            .ThenBy(static d => d.Id, StringComparer.Ordinal)
            .ToList();

        var result = new List<Abstractions.ContextDocument>(ordered.Count);
        var used = 0;

        foreach (var doc in ordered)
        {
            if (used + doc.Length > _options.TotalCharsBudget && result.Count > 0)
            {
                break;
            }

            result.Add(doc);
            used += doc.Length;
        }

        return result;
    }
}
