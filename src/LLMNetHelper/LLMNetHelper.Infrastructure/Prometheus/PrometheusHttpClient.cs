using System.Text.Json;
using LLMNetHelper.Abstractions;
using Microsoft.Extensions.Options;

namespace LLMNetHelper.Infrastructure.Prometheus;

/// <summary>
/// HTTP-клиент для Prometheus: instant/range запросы и health-check.
/// Стиль основан на PrometheusClient из MemReport.
/// </summary>
public sealed class PrometheusHttpClient : IPrometheusClient
{
    private readonly HttpClient _http;
    private readonly PrometheusOptions _options;

    public PrometheusHttpClient(IOptions<PrometheusOptions> options)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _http = new HttpClient
        {
            BaseAddress = new Uri(_options.BaseUrl),
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds)
        };
    }

    /// <summary>
    /// Internal-конструктор для тестов (план 22, этап 1.6): подмена транспорта
    /// через <see cref="HttpMessageHandler"/> без сети.
    /// </summary>
    internal PrometheusHttpClient(IOptions<PrometheusOptions> options, HttpMessageHandler handler)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _http = new HttpClient(handler, disposeHandler: true)
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
            // Дефект N3 (план 22): ответ и документ освобождаются через using.
            // EnsureSuccessStatusCode заменён явной проверкой (DisposableDiscarded +
            // запрет переприсваивания using-переменной).
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"/api/v1/query: status code {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
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

            // Дефект N3 (план 22): ответ и документ освобождаются через using.
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"/api/v1/query_range: status code {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
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

    // План 22, этап 1.6: GetAlertingRulesAsync удалён как мёртвый код
    // (0 потребителей; проверено find_references/analyze_change_impact).

    public async Task<List<PrometheusAlert>> GetAlertsAsync(CancellationToken ct = default)
    {
        try
        {
            // Дефект N3 (план 22): ответ и документ освобождаются через using.
            using var response = await _http.GetAsync("/api/v1/alerts", ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"/api/v1/alerts: status code {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            // План 22, этап 1.6: типизированный PrometheusAlert вместо Dictionary.
            // Prometheus отдаёт {"data":{"alerts":[{...}]}}.
            var alerts = new List<PrometheusAlert>();
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("alerts", out var alertsElem) &&
                alertsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var alert in alertsElem.EnumerateArray())
                {
                    if (alert.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var alertname = alert.TryGetProperty("labels", out var labels) &&
                                    labels.TryGetProperty("alertname", out var nameProp) &&
                                    nameProp.ValueKind == JsonValueKind.String
                        ? nameProp.GetString() ?? ""
                        : "";

                    var state = alert.TryGetProperty("state", out var stateProp) &&
                                stateProp.ValueKind == JsonValueKind.String
                        ? stateProp.GetString() ?? ""
                        : "";

                    var labelDict = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (alert.TryGetProperty("labels", out var labelsElem) &&
                        labelsElem.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in labelsElem.EnumerateObject())
                        {
                            labelDict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                                ? prop.Value.GetString() ?? ""
                                : prop.Value.ToString();
                        }
                    }

                    alerts.Add(new PrometheusAlert(alertname, state, labelDict));
                }
            }
            return alerts;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PROM] Alerts query error: {ex.Message}");
            return new List<PrometheusAlert>();
        }
    }

    public async Task<string?> GetMetricsAsync(CancellationToken ct = default)
    {
        try
        {
            // Дефект N3 (план 22): ответ освобождается через using.
            using var response = await _http.GetAsync("/metrics", ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(ct)
                : null;
        }
        catch
        {
            return null;
        }
    }

    // JSON-null допустим в ответах Prometheus -> значение может быть null (CS8603).
    private static object? CloneValue(JsonElement elem)
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
