using System.Text.Json;
using ArtasTools.MonitorAgent.Abstractions;
using Microsoft.Extensions.Options;

namespace ArtasTools.MonitorAgent.Infrastructure.Prometheus;

/// <summary>
/// HTTP-клиент для Prometheus: instant/range запросы и health-check.
/// Стиль основан на PrometheusClient из MemReport.
/// </summary>
public sealed class PrometheusHttpClient : IPrometheusClient
{
    private readonly HttpClient _http;
    private readonly PrometheusOptions _options;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public PrometheusHttpClient(IOptions<PrometheusOptions> options)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _http = new HttpClient
        {
            BaseAddress = new Uri(_options.BaseUrl),
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds)
        };
    }

    public async Task<double?> QueryAsync(string query, CancellationToken ct = default)
    {
        try
        {
            var url = $"/api/v1/query?query={Uri.EscapeDataString(query)}";
            var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("result", out var result) && result.TryGetArray(out var array) &&
                array.Length > 0)
            {
                // instant vector: value = [timestamp, "значение"]
                if (array[0].TryGetProperty("value", out var val) &&
                    val.ValueKind == JsonValueKind.Array && val.GetArrayLength() >= 2 &&
                    TryReadNumber(val[1], out var valDouble))
                {
                    return valDouble;
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PROM] Query error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Range-запрос: возвращает точки первой серии матрицы результата.
    /// <para>
    /// Prometheus отдаёт пару <c>[timestamp, "значение"]</c>, где timestamp —
    /// ЧИСЛО (unix-секунды, дробные для instant-запроса), а значение — СТРОКА.
    /// Прежняя реализация вызывала <c>GetString()</c> по индексу 0 и падала с
    /// «The requested operation requires an element of type 'String', but the
    /// target element has type 'Number'», из-за чего тренды памяти всегда были
    /// пустыми, а сводка — обеднённой.
    /// </para>
    /// </summary>
    public async Task<List<(string Time, double Value)>> QueryRangeAsync(
        string query, DateTime from, DateTime to, TimeSpan step, CancellationToken ct = default)
    {
        try
        {
            var fromTs = new DateTimeOffset(from).ToUnixTimeSeconds();
            var toTs = new DateTimeOffset(to).ToUnixTimeSeconds();
            var stepSec = (int)step.TotalSeconds;
            var url = $"/api/v1/query_range?" +
                      $"query={Uri.EscapeDataString(query)}&" +
                      $"start={fromTs}&end={toTs}&step={stepSec}";

            var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var points = new List<(string Time, double Value)>();

            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("result", out var result) && result.TryGetArray(out var array))
            {
                // Проходим по ВСЕМ сериям, а не только по array[0]: метрика может
                // прийти от нескольких экземпляров (service_instance_id), и
                // первая серия могла оказаться пустой/не той службы.
                foreach (var series in array)
                {
                    if (!series.TryGetProperty("values", out var values) ||
                        values.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var point in values.EnumerateArray())
                    {
                        if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2)
                        {
                            continue;
                        }

                        if (!TryReadNumber(point[1], out var valDouble))
                        {
                            continue;
                        }

                        points.Add((FormatTimestamp(point[0]), valDouble));
                    }
                }
            }
            return points;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PROM] Range query error: {ex.Message}");
            return new List<(string Time, double Value)>();
        }
    }

    /// <summary>
    /// Читает число из элемента JSON-массива Prometheus (значение приходит строкой,
    /// возможны <c>+Inf</c>/<c>NaN</c>). Разделитель — инвариантная точка:
    /// в культуре ru-RU <c>double.TryParse("1.5")</c> возвращает false.
    /// </summary>
    private static bool TryReadNumber(JsonElement element, out double result)
    {
        result = 0;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                result = element.GetDouble();
                return double.IsFinite(result);
            case JsonValueKind.String:
                return double.TryParse(element.GetString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out result) && double.IsFinite(result);
            default:
                return false;
        }
    }

    /// <summary>
    /// Приводит timestamp (число или строку) к строковому виду для контракта
    /// <see cref="IPrometheusClient.QueryRangeAsync"/>.
    /// </summary>
    private static string FormatTimestamp(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsonValueKind.String => element.GetString() ?? "",
        _ => element.ToString()
    };

    public async Task<List<Dictionary<string, object>>> GetAlertingRulesAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/api/v1/rules?type=alert", ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);

            var rules = new List<Dictionary<string, object>>();
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("groups", out var groups))
            {
                foreach (var group in groups.EnumerateArray())
                {
                    if (group.TryGetProperty("rules", out var rulesArr))
                    {
                        foreach (var rule in rulesArr.EnumerateArray())
                        {
                            var dict = new Dictionary<string, object>();
                            foreach (var prop in rule.EnumerateObject())
                            {
                                dict[prop.Name] = CloneValue(prop.Value);
                            }
                            rules.Add(dict);
                        }
                    }
                }
            }
            return rules;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PROM] Rules query error: {ex.Message}");
            return new List<Dictionary<string, object>>();
        }
    }

    public async Task<List<Dictionary<string, object>>> GetAlertsAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/api/v1/alerts", ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);

            var alerts = new List<Dictionary<string, object>>();
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data) && data.TryGetArray(out var array))
            {
                foreach (var alert in array)
                {
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in alert.EnumerateObject())
                    {
                        dict[prop.Name] = CloneValue(prop.Value);
                    }
                    alerts.Add(dict);
                }
            }
            return alerts;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PROM] Alerts query error: {ex.Message}");
            return new List<Dictionary<string, object>>();
        }
    }

    public async Task<string?> GetMetricsAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/metrics", ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(ct)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static object CloneValue(JsonElement elem)
    {
        return elem.ValueKind switch
        {
            JsonValueKind.String => elem.GetString()!,
            JsonValueKind.Number => elem.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => elem.ToString()
        };
    }

    public void Dispose()
    {
        _http?.Dispose();
    }
}
