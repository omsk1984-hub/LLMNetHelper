using System.Text;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Snapshot;
using Microsoft.Extensions.Options;

namespace LLMNetHelper.Core.Collection;

/// <summary>
/// Сбор фактов из Loki: L1-L5 запросы из плана.
/// </summary>
public class LokiFactCollector : FactCollector<LogFact, LokiFacts>
{
    private readonly ILokiClient _client;
    private readonly LokiOptions _options;
    private readonly AgentOptions _agentOptions;

    public LokiFactCollector(
        ILokiClient client,
        IOptions<LokiOptions> lokiOptions,
        IOptions<AgentOptions> agentOptions)
    {
        _client = client;
        _options = lokiOptions.Value;
        _agentOptions = agentOptions.Value;
    }

    protected override async Task<LokiFacts> CollectCoreAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var logFacts = new List<LogFact>();
        var errorSamples = new List<ErrorSample>();
        long stderrCount = 0;

        var overlapFrom = from.AddHours(-3); // перекрытие для Promtail

        // L1: динамика логов по часам
        var l1 = await CollectL1Async(overlapFrom, to, ct);
        logFacts.AddRange(l1);

        // L2: ошибки по модулям
        var l2 = await CollectL2Async(overlapFrom, to, ct);
        logFacts.AddRange(l2);

        // L3: ошибки по инструментам
        var l3 = await CollectL3Async(overlapFrom, to, ct);
        logFacts.AddRange(l3);

        // L4: сырые записи для error_samples
        errorSamples = await CollectL4Async(overlapFrom, to, ct, ct);

        // L5: объём stderr
        stderrCount = await CollectL5Async(overlapFrom, to, ct);

        return new LokiFacts(logFacts, errorSamples, stderrCount);
    }

    private async Task<IEnumerable<LogFact>> CollectL1Async(DateTime from, DateTime to, CancellationToken ct)
    {
        var query = @"sum by (job, svc, lvl) (count_over_time({job=~""artas_writer|artas_stat""}[1h]))";
        var results = await _client.QueryAggregationsAsync(query, ct);

        var facts = new List<LogFact>();
        var bucketStart = DateTime.UtcNow.TruncateHours();

        foreach (var (key, count) in results)
        {
            facts.Add(new LogFact
            {
                BucketStart = bucketStart.ToString("yyyy-MM-ddTHHH:00:00Z"),
                Job = ExtractLabel(key, "job"),
                Svc = ExtractLabel(key, "svc"),
                Lvl = ExtractLabel(key, "lvl"),
                Count = (int)Math.Min(count, int.MaxValue)
            });
        }
        return facts;
    }

    private async Task<IEnumerable<LogFact>> CollectL2Async(DateTime from, DateTime to, CancellationToken ct)
    {
        var query = @"sum by (job, logger) (count_over_time({job=~""artas_writer|artas_stat"", lvl=~""fail|crit""}[1h]))";
        var results = await _client.QueryAggregationsAsync(query, ct);

        var facts = new List<LogFact>();
        var bucketStart = DateTime.UtcNow.TruncateHours();

        foreach (var (key, count) in results)
        {
            facts.Add(new LogFact
            {
                BucketStart = bucketStart.ToString("yyyy-MM-ddTHHH:00:00Z"),
                Job = ExtractLabel(key, "job"),
                Logger = ExtractLabel(key, "logger"),
                Lvl = "fail|crit",
                Count = (int)Math.Min(count, int.MaxValue)
            });
        }
        return facts;
    }

    private async Task<IEnumerable<LogFact>> CollectL3Async(DateTime from, DateTime to, CancellationToken ct)
    {
        var windowStr = $"{to.Subtract(from).TotalHours}h";
        var query = $@"sum by (job, symbol) (count_over_time({{job=~""artas_writer|artas_stat"", lvl=~""fail|crit""}}[{windowStr}]))";
        var results = await _client.QueryAggregationsAsync(query, ct);

        var facts = new List<LogFact>();
        var bucketStart = DateTime.UtcNow.TruncateHours();

        foreach (var (key, count) in results)
        {
            facts.Add(new LogFact
            {
                BucketStart = bucketStart.ToString("yyyy-MM-ddTHHH:00:00Z"),
                Job = ExtractLabel(key, "job"),
                Symbol = ExtractLabel(key, "symbol"),
                Lvl = "fail|crit",
                Count = (int)Math.Min(count, int.MaxValue)
            });
        }
        return facts;
    }

    private async Task<List<ErrorSample>> CollectL4Async(DateTime from, DateTime to, CancellationToken ct, CancellationToken ct2)
    {
        var query = @"{job=~""artas_writer|artas_stat"", lvl=~""fail|crit""}";
        var entries = await _client.QueryEntriesAsync(query, from, to, _options.MaxEntries, ct2);
        return NormalizeErrorSamples(entries, from, to);
    }

    private async Task<long> CollectL5Async(DateTime from, DateTime to, CancellationToken ct)
    {
        var windowStr = $"{to.Subtract(from).TotalHours}h";
        var query = $@"sum by (lvl) (count_over_time({{log_stream=""stderr""}}[{windowStr}]))";
        var results = await _client.QueryAggregationsAsync(query, ct);
        return results.Values.Sum();
    }

    // План 22, этап 1.6: типизированный LokiEntry вместо Dictionary<string, object>.
    private static List<ErrorSample> NormalizeErrorSamples(
        List<LokiEntry> entries, DateTime from, DateTime to)
    {
        var grouped = new Dictionary<string, (List<string> texts, int count, string firstSeen, string lastSeen)>();

        foreach (var entry in entries)
        {
            var text = entry.Text;

            var normalized = ErrorSignatureNormalizer.Normalize(text);
            var signature = ErrorSignatureNormalizer.ComputeHash(normalized);

            if (!grouped.TryGetValue(signature, out var group))
            {
                group = (new List<string>(), 0, entry.Timestamp, entry.Timestamp);
                grouped[signature] = group;
            }

            group.count++;
            group.lastSeen = entry.Timestamp;
            if (group.texts.Count == 0)
                group.texts.Add(text);
        }

        var samples = new List<ErrorSample>();
        foreach (var (signature, group) in grouped)
        {
            if (samples.Count >= 200) break;

            samples.Add(new ErrorSample
            {
                Signature = signature,
                SampleText = group.texts.FirstOrDefault() ?? "",
                Count = group.count,
                FirstSeen = group.firstSeen,
                LastSeen = group.lastSeen,
                Lvl = "fail",
                Job = "",
                Logger = "",
                Symbol = ""
            });
        }

        return samples.OrderByDescending(s => s.Count).ToList();
    }

    private static string ExtractLabel(string labelString, string name)
    {
        var prefix = $"{name}=";
        var start = labelString.IndexOf(prefix);
        if (start < 0) return "";

        start += prefix.Length;
        var end = labelString.IndexOf(',', start);
        if (end < 0) end = labelString.Length;

        var value = labelString.Substring(start, end - start);
        value = value.Trim('"');
        return value;
    }
}

/// <summary>
/// Результат сбора фактов из Loki.
/// </summary>
public sealed record LokiFacts(
    List<LogFact> LogFacts,
    List<ErrorSample> ErrorSamples,
    long StderrCount);
