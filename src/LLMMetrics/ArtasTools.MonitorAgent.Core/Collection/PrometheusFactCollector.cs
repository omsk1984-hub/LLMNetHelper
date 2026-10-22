using ArtasTools.MonitorAgent.Abstractions;
using Microsoft.Extensions.Options;

namespace ArtasTools.MonitorAgent.Core.Collection;

/// <summary>
/// Сбор фактов из Prometheus: P1-P9 запросы из плана.
/// </summary>
public class PrometheusFactCollector
{
    private readonly IPrometheusClient _client;
    private readonly PrometheusOptions _options;

    public PrometheusFactCollector(
        IPrometheusClient client,
        IOptions<PrometheusOptions> options)
    {
        _client = client;
        _options = options.Value;
    }

    /// <summary>
    /// Собирает все факты из Prometheus за окно.
    /// </summary>
    public async Task<PrometheusFacts> CollectAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var metricFacts = new List<MetricFact>();
        var alerts = new List<Dictionary<string, object>>();
        var monitoringHealth = new MonitoringHealthData();

        // P1: up{job="prometheus"}
        var promUp = await _client.QueryAsync("up{job=\"prometheus\"}", ct);
        monitoringHealth.PrometheusUp = promUp == 1;

        // P2: dotnet_process_memory_working_set_bytes (instant + range)
        var heapFacts = await CollectHeapTrendAsync(from, to, ct);
        metricFacts.AddRange(heapFacts);

        // P3: dotnet_gc_last_collection_heap_size_bytes
        var gcHeapFacts = await CollectGcHeapTrendAsync(from, to, ct);
        metricFacts.AddRange(gcHeapFacts);

        // P4: dotnet_gc_heap_size_bytes{generation="gen2"}
        var gen2Facts = await CollectGen2HeapTrendAsync(from, to, ct);
        metricFacts.AddRange(gen2Facts);

        // P5: rate(dotnet_gc_collections_total[1h])
        var gcRate = await _client.QueryAsync("rate(dotnet_gc_collections_total[1h])", ct);
        if (gcRate.HasValue)
        {
            metricFacts.Add(new MetricFact
            {
                Name = "dotnet_gc_collections_rate_per_hour",
                Value = gcRate.Value,
                WindowFrom = from.ToString("o"),
                WindowTo = to.ToString("o")
            });
        }

        // P6: dotnet_thread_pool_thread_count_total
        var threadCount = await _client.QueryAsync("dotnet_thread_pool_thread_count_total", ct);
        if (threadCount.HasValue)
        {
            metricFacts.Add(new MetricFact
            {
                Name = "dotnet_thread_pool_thread_count",
                Value = threadCount.Value,
                WindowFrom = from.ToString("o"),
                WindowTo = to.ToString("o")
            });
        }

        // P7: dotnet_exceptions_total (если экспортируется)
        var exceptions = await _client.QueryAsync("sum(rate(dotnet_exceptions_total[1h]))", ct);
        if (exceptions.HasValue)
        {
            metricFacts.Add(new MetricFact
            {
                Name = "dotnet_exceptions_rate_per_hour",
                Value = exceptions.Value,
                WindowFrom = from.ToString("o"),
                WindowTo = to.ToString("o")
            });
        }

        // P8: ALERTS и /api/v1/rules
        alerts = await _client.GetAlertsAsync(ct);
        monitoringHealth.FiringAlerts = alerts.Count;

        // P9: возраст метрик
        var dotnetMetrics = await _client.QueryAsync("dotnet_process_memory_working_set_bytes", ct);
        if (dotnetMetrics.HasValue)
        {
            monitoringHealth.LastDotnetMetricAge = TimeSpan.Zero;
        }
        else
        {
            monitoringHealth.LastDotnetMetricAge = TimeSpan.FromHours(24); // не найдено
        }

        return new PrometheusFacts(metricFacts, alerts, monitoringHealth);
    }

    private async Task<List<MetricFact>> CollectHeapTrendAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var facts = new List<MetricFact>();

        // Instant
        var instant = await _client.QueryAsync("dotnet_process_memory_working_set_bytes", ct);
        if (instant.HasValue)
        {
            facts.Add(new MetricFact
            {
                Name = "dotnet_process_memory_working_set_bytes",
                Value = instant.Value,
                WindowFrom = from.ToString("o"),
                WindowTo = to.ToString("o")
            });
        }

        // Range для тренда
        var points = await _client.QueryRangeAsync(
            "dotnet_process_memory_working_set_bytes", from, to, TimeSpan.FromMinutes(15), ct);

        if (points.Count >= 3)
        {
            var trend = LinearRegression(points.Select(p => p.Value).ToArray());
            facts.Add(new MetricFact
            {
                Name = "dotnet_process_memory_working_set_bytes",
                Value = points.Last().Value,
                SlopePerHour = trend.SlopePerHour,
                R2 = trend.R2,
                WindowFrom = from.ToString("o"),
                WindowTo = to.ToString("o")
            });
        }

        return facts;
    }

    private async Task<List<MetricFact>> CollectGcHeapTrendAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var facts = new List<MetricFact>();

        var instant = await _client.QueryAsync("dotnet_gc_last_collection_heap_size_bytes", ct);
        if (instant.HasValue)
        {
            facts.Add(new MetricFact
            {
                Name = "dotnet_gc_last_collection_heap_size_bytes",
                Value = instant.Value,
                WindowFrom = from.ToString("o"),
                WindowTo = to.ToString("o")
            });
        }

        var points = await _client.QueryRangeAsync(
            "dotnet_gc_last_collection_heap_size_bytes", from, to, TimeSpan.FromMinutes(15), ct);

        if (points.Count >= 3)
        {
            var trend = LinearRegression(points.Select(p => p.Value).ToArray());
            facts.Add(new MetricFact
            {
                Name = "dotnet_gc_last_collection_heap_size_bytes",
                Value = points.Last().Value,
                SlopePerHour = trend.SlopePerHour,
                R2 = trend.R2,
                WindowFrom = from.ToString("o"),
                WindowTo = to.ToString("o")
            });
        }

        return facts;
    }

    private async Task<List<MetricFact>> CollectGen2HeapTrendAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var facts = new List<MetricFact>();

        var instant = await _client.QueryAsync("dotnet_gc_heap_size_bytes{generation=\"gen2\"}", ct);
        if (instant.HasValue)
        {
            facts.Add(new MetricFact
            {
                Name = "dotnet_gc_heap_size_bytes_gen2",
                Value = instant.Value,
                WindowFrom = from.ToString("o"),
                WindowTo = to.ToString("o")
            });
        }

        return facts;
    }

    /// <summary>
    /// Простая линейная регрессия: slope в единицах за точку, R².
    /// </summary>
    private static (double SlopePerHour, double R2) LinearRegression(double[] values)
    {
        var n = values.Length;
        if (n < 2) return (0, 0);

        var sumX = 0.0;
        var sumY = 0.0;
        var sumXY = 0.0;
        var sumX2 = 0.0;
        var sumY2 = 0.0;

        for (var i = 0; i < n; i++)
        {
            sumX += i;
            sumY += values[i];
            sumXY += i * values[i];
            sumX2 += i * i;
            sumY2 += values[i] * values[i];
        }

        var denom = n * sumX2 - sumX * sumX;
        if (Math.Abs(denom) < 1e-10) return (0, 0);

        var slope = (n * sumXY - sumX * sumY) / denom;
        var intercept = (sumY - slope * sumX) / n;

        // R²
        var ssTot = sumY2 - (sumY * sumY) / n;
        var ssRes = 0.0;
        for (var i = 0; i < n; i++)
        {
            var predicted = intercept + slope * i;
            ssRes += (values[i] - predicted) * (values[i] - predicted);
        }

        var r2 = ssTot > 1e-10 ? 1 - ssRes / ssTot : 0;

        // slopePerHour: предполагаем что шаг ~15 минут, значит за час ~4 точки
        var slopePerHour = slope * (60.0 / 15.0);

        return (slopePerHour, r2);
    }
}

/// <summary>
/// Результат сбора фактов из Prometheus.
/// </summary>
public sealed record PrometheusFacts(
    List<MetricFact> MetricFacts,
    List<Dictionary<string, object>> Alerts,
    MonitoringHealthData MonitoringHealth);

/// <summary>
/// Данные для health-check мониторинга.
/// </summary>
public sealed class MonitoringHealthData
{
    public bool PrometheusUp { get; set; }
    public int FiringAlerts { get; set; }
    public TimeSpan LastDotnetMetricAge { get; set; }
    public bool LokiReady { get; set; }
    public TimeSpan? LokiLastWriteAge { get; set; }
    public double? DiskFreeGB { get; set; }
    public bool AllHealthy => PrometheusUp && LokiReady && LastDotnetMetricAge.TotalHours < 6;
}
