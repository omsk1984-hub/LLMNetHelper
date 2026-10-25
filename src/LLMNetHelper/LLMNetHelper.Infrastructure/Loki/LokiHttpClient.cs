using System.Text.Json;
using LLMNetHelper.Abstractions;
using Microsoft.Extensions.Options;

namespace LLMNetHelper.Infrastructure.Loki;

/// <summary>
/// HTTP-клиент для Loki: запросы LogQL и health-check.
/// Стиль основан на PrometheusClient из MemReport.
/// </summary>
public sealed class LokiHttpClient : ILokiClient
{
    private readonly HttpClient _http;
    private readonly LokiOptions _options;
    // План 22, этап 6.3: единый профиль чтения (см. JsonProfiles).
    public LokiHttpClient(IOptions<LokiOptions> options)
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
    internal LokiHttpClient(IOptions<LokiOptions> options, HttpMessageHandler handler)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(_options.BaseUrl),
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds)
        };
    }

    /// <summary>
    /// Выполняет агрегирующий запрос и возвращает сумму по каждому набору меток.
    /// <para>
    /// Loki отдаёт агрегаты в двух форматах в зависимости от эндпоинта:
    /// <list type="bullet">
    ///   <item><c>/loki/api/v1/query</c> (instant) — <c>resultType: "vector"</c>,
    ///         одна точка в поле <c>value: [timestamp, "число"]</c>;</item>
    ///   <item><c>/loki/api/v1/query_range</c> (range) — <c>resultType: "matrix"</c>,
    ///         ряд точек в поле <c>values: [[ts, "число"], ...]</c>.</item>
    /// </list>
    /// Метки результата лежат в <c>metric</c> (а не в <c>stream</c>, как у
    /// потока логов), поэтому парсятся оба имени.
    /// </para>
    /// <para>
    /// Прежняя реализация читала только <c>values</c> и <c>stream</c>, из-за чего
    /// instant-запрос всегда давал пустой словарь: сводка формировалась как
    /// «0 агрегатов, 0 ошибок» при фактически присутствующих логах.
    /// </para>
    /// </summary>
    public async Task<Dictionary<string, long>> QueryAggregationsAsync(string query, CancellationToken ct = default)
    {
        try
        {
            var url = $"/loki/api/v1/query?query={Uri.EscapeDataString(query)}";
            // Дефект N3 (план 22): ответ и документ освобождаются через using.
            // EnsureSuccessStatusCode заменён явной проверкой: результат метода
            // не отбрасывается (DisposableDiscarded) и using-переменная не переприсваивается.
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"/loki/api/v1/query: status code {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("result", out var result) && result.TryGetArray(out var array))
            {
                var dict = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var elem in array)
                {
                    var labelsStr = BuildLabelString(elem);

                    // instant vector: value = [timestamp, "число"]
                    if (elem.TryGetProperty("value", out var value) && value.TryGetArray(out var pair) &&
                        pair.Length >= 2)
                    {
                        if (TryReadNumber(pair[1], out var single))
                        {
                            dict[labelsStr] = checked((long)single);
                        }
                        continue;
                    }

                    // range matrix: values = [[ts, "число"], ...] — суммируем окно
                    if (elem.TryGetProperty("values", out var values) && values.TryGetArray(out var vals))
                    {
                        double sum = 0;
                        foreach (var point in vals)
                        {
                            if (point.TryGetArray(out var pp) && pp.Length >= 2 && TryReadNumber(pp[1], out var v))
                            {
                                sum += v;
                            }
                        }
                        dict[labelsStr] = checked((long)sum);
                    }
                }
                return dict;
            }
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LOKI] Query error: {ex.Message}");
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Читает число из элемента массива Loki, который приходит строкой
    /// (<c>"1"</c>) и может прийти как <c>+Inf</c>/<c>NaN</c>.
    /// Культура-независимо: разделитель десятичной точки — всегда точка.
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
    /// Выборка сырых записей логов за окно <paramref name="from"/> — <paramref name="to"/>.
    /// <para>
    /// <c>start</c>/<c>end</c> обязательны: без них Loki отвечает
    /// <c>resultType: "streams"</c> (список потоков без значений), и парсер
    /// ниже не находит ни одной записи — error_samples всегда пуст.
    /// </para>
    /// </summary>
    public async Task<List<LokiEntry>> QueryEntriesAsync(
        string query, DateTime from, DateTime to, int limit, CancellationToken ct = default)
    {
        try
        {
            // Loki принимает unix-секунды; окно — по UTC (см. правила проекта).
            var startNs = ToUnixNanos(from);
            var endNs = ToUnixNanos(to);
            var url = $"/loki/api/v1/query_range?query={Uri.EscapeDataString(query)}" +
                      $"&start={startNs}&end={endNs}" +
                      $"&limit={limit}&direction=backward";
            // Дефект N3 (план 22): ответ и документ освобождаются через using.
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"/loki/api/v1/query_range: status code {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // План 22, этап 1.6: типизированный LokiEntry вместо Dictionary.
            var entries = new List<LokiEntry>();

            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("result", out var result) && result.TryGetArray(out var array))
            {
                foreach (var stream in array)
                {
                    var streamLabels = BuildLabelString(stream);
                    if (stream.TryGetProperty("values", out var values) && values.TryGetArray(out var vals))
                    {
                        foreach (var valElem in vals)
                        {
                            if (valElem.TryGetArray(out var valArray) && valArray.Length >= 2)
                            {
                                entries.Add(new LokiEntry(
                                    streamLabels,
                                    // timestamp записи Loki приходит строкой (наносекунды),
                                    // но не полагаемся на тип: приводим к строке оба варианта.
                                    TimestampToString(valArray[0]),
                                    valArray[1].ValueKind == JsonValueKind.String
                                        ? valArray[1].GetString() ?? ""
                                        : valArray[1].ToString()));
                            }
                        }
                    }
                }
            }
            return entries;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LOKI] Entries query error: {ex.Message}");
            return new List<LokiEntry>();
        }
    }

    public async Task<bool> IsReadyAsync(CancellationToken ct = default)
    {
        try
        {
            // Дефект N3 (план 22): ответ освобождается через using.
            using var response = await _http.GetAsync("/loki/api/v1/status/buildinfo", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
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

    /// <summary>
    /// Собирает строку меток вида <c>job="artas_writer",lvl="fail"</c>.
    /// <para>
    /// У результата потока логов метки находятся в <c>stream</c>, у результата
    /// агрегирующего запроса — в <c>metric</c>. Поддерживаются оба имени, иначе
    /// сборщик фактов не смог бы извлечь job/lvl из агрегатов.
    /// </para>
    /// </summary>
    private static string BuildLabelString(JsonElement elem)
    {
        var parts = new List<string>();
        foreach (var propertyName in new[] { "stream", "metric" })
        {
            if (!elem.TryGetProperty(propertyName, out var labels) ||
                labels.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var prop in labels.EnumerateObject())
            {
                var value = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? ""
                    : prop.Value.ToString();
                parts.Add($"{prop.Name}=\"{value}\"");
            }
        }
        return string.Join(",", parts);
    }

    /// <summary>
    /// Приводит timestamp записи Loki к строке, не завися от его JSON-типа
    /// (наносекунды приходят строкой, но в matrix-результате возможен числовой вид).
    /// </summary>
    private static string TimestampToString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => element.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => element.ToString()
    };

    /// <summary>
    /// Переводит UTC-момент в наносекунды unix-эпохи — формат, который
    /// ожидает Loki в параметрах <c>start</c>/<c>end</c>.
    /// </summary>
    private static long ToUnixNanos(DateTime instant)
    {
        var utc = instant.Kind == DateTimeKind.Utc
            ? instant
            : instant.ToUniversalTime();
        return new DateTimeOffset(utc).ToUnixTimeMilliseconds() * 1_000_000L;
    }

    public void Dispose()
    {
        _http?.Dispose();
    }
}
