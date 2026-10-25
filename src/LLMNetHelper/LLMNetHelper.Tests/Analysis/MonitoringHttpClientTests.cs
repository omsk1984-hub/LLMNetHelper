using System.Net;
using System.Text;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Infrastructure.Loki;
using LLMNetHelper.Infrastructure.Prometheus;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMNetHelper.Tests.Analysis;

/// <summary>
/// Тесты HTTP-клиентов мониторинга (план 22, этап 6.7): покрывают риск-хотспоты
/// <c>LokiHttpClient.QueryAggregationsAsync/IsReadyAsync/GetMetricsAsync</c> и
/// <c>PrometheusHttpClient.QueryAsync/QueryRangeAsync/GetAlertsAsync/GetMetricsAsync</c>.
/// <para>
/// Сеть не используется: транспорт подменён <see cref="ScriptedHandler"/>
/// (internal-конструкторы, добавленные этапом 1.6). Handler записывает URL
/// запроса, поэтому проверяется и формат запроса (window/limit/escape), а не
/// только разбор ответа.
/// </para>
/// <para>
/// Каждый тест фиксирует контракт «разбор внешнего JSON → типизированная модель»
/// или «сбой → пустой результат без исключения» — именно эту ветку молча
/// ломал рефакторинг парсеров на этапах 1.6/6.3.
/// </para>
/// </summary>
public class MonitoringHttpClientTests
{
    // ---------- Loki: агрегирующий запрос ----------

    /// <summary>
    /// Instant-vector (resultType "vector"): одна точка в <c>value</c>, метки в <c>metric</c>.
    /// Прежняя реализация читала только <c>values</c>/<c>stream</c> и давала пустой словарь.
    /// </summary>
    [Fact]
    public async Task LokiQueryAggregations_InstantVector_SumsPerMetricLabels()
    {
        var handler = new ScriptedHandler(("""
            {"status":"success","data":{"resultType":"vector","result":[
              {"metric":{"job":"artas_writer","lvl":"fail"},"value":[1767225600,"42"]}
            ]}}
            """, HttpStatusCode.OK));

        using var client = new LokiHttpClient(
            Options.Create(new LokiOptions { BaseUrl = "http://loki.test:3100" }), handler);

        var agg = await client.QueryAggregationsAsync("sum(count_over_time({job=\"artas_writer\"}[1h]))");

        Assert.Single(agg);
        Assert.Equal(42L, agg["job=\"artas_writer\",lvl=\"fail\""]);
        // Запрос идёт на instant-эндпоинт с экранированным LogQL.
        Assert.Contains("/loki/api/v1/query?query=", handler.LastUrl, StringComparison.Ordinal);
        Assert.Contains("sum", handler.LastUrl, StringComparison.Ordinal);
    }

    /// <summary>
    /// Range-matrix (resultType "matrix"): сумма точек ряда по окну.
    /// </summary>
    [Fact]
    public async Task LokiQueryAggregations_RangeMatrix_SumsWindowPoints()
    {
        var handler = new ScriptedHandler(("""
            {"status":"success","data":{"resultType":"matrix","result":[
              {"metric":{"job":"w"},"values":[[1,"10"],[2,"20"],[3,"5"]]}
            ]}}
            """, HttpStatusCode.OK));

        using var client = new LokiHttpClient(
            Options.Create(new LokiOptions { BaseUrl = "http://loki.test:3100" }), handler);

        var agg = await client.QueryAggregationsAsync("q");

        Assert.Equal(35L, agg["job=\"w\""]);
    }

    /// <summary>
    /// Нераспознаваемое число (+Inf/NaN/мусор) не должно ронять парсер:
    /// запись просто отбрасывается.
    /// </summary>
    [Fact]
    public async Task LokiQueryAggregations_NonFiniteValue_Omitted()
    {
        var handler = new ScriptedHandler(("""
            {"status":"success","data":{"resultType":"vector","result":[
              {"metric":{"job":"a"},"value":[1,"+Inf"]},
              {"metric":{"job":"b"},"value":[1,"7"]}
            ]}}
            """, HttpStatusCode.OK));

        using var client = new LokiHttpClient(
            Options.Create(new LokiOptions { BaseUrl = "http://loki.test:3100" }), handler);

        var agg = await client.QueryAggregationsAsync("q");

        Assert.False(agg.ContainsKey("job=\"a\""));
        Assert.Equal(7L, agg["job=\"b\""]);
    }

    /// <summary>
    /// Ошибка транспорта (5xx) агрегирующего запроса → пустой словарь, без исключения.
    /// </summary>
    [Fact]
    public async Task LokiQueryAggregations_ServerError_ReturnsEmpty()
    {
        var handler = new ScriptedHandler(("", HttpStatusCode.InternalServerError));

        using var client = new LokiHttpClient(
            Options.Create(new LokiOptions { BaseUrl = "http://loki.test:3100" }), handler);

        var agg = await client.QueryAggregationsAsync("q");

        Assert.Empty(agg);
    }

    // ---------- Loki: health / metrics ----------

    [Fact]
    public async Task LokiIsReady_Ok_ReturnsTrue()
    {
        var handler = new ScriptedHandler(("""{"version":"3.0"}""", HttpStatusCode.OK));
        using var client = new LokiHttpClient(
            Options.Create(new LokiOptions { BaseUrl = "http://loki.test:3100" }), handler);

        Assert.True(await client.IsReadyAsync());
        Assert.Contains("/loki/api/v1/status/buildinfo", handler.LastUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LokiIsReady_ServerError_ReturnsFalse()
    {
        var handler = new ScriptedHandler(("", HttpStatusCode.ServiceUnavailable));
        using var client = new LokiHttpClient(
            Options.Create(new LokiOptions { BaseUrl = "http://loki.test:3100" }), handler);

        Assert.False(await client.IsReadyAsync());
    }

    [Fact]
    public async Task LokiGetMetrics_Ok_ReturnsBody()
    {
        var handler = new ScriptedHandler(("loki_build_info 1", HttpStatusCode.OK));
        using var client = new LokiHttpClient(
            Options.Create(new LokiOptions { BaseUrl = "http://loki.test:3100" }), handler);

        Assert.Equal("loki_build_info 1", await client.GetMetricsAsync());
    }

    [Fact]
    public async Task LokiGetMetrics_NotFound_ReturnsNull()
    {
        var handler = new ScriptedHandler(("", HttpStatusCode.NotFound));
        using var client = new LokiHttpClient(
            Options.Create(new LokiOptions { BaseUrl = "http://loki.test:3100" }), handler);

        Assert.Null(await client.GetMetricsAsync());
    }

    // ---------- Prometheus: instant / range ----------

    [Fact]
    public async Task PrometheusQuery_InstantVector_ReturnsFirstValue()
    {
        var handler = new ScriptedHandler(("""
            {"status":"success","data":{"resultType":"vector","result":[
              {"metric":{"__name__":"up"},"value":[1767225600,"1"]}
            ]}}
            """, HttpStatusCode.OK));

        using var client = new PrometheusHttpClient(
            Options.Create(new PrometheusOptions { BaseUrl = "http://prometheus.test:9090" }), handler);

        Assert.Equal(1.0, await client.QueryAsync("up"));
        Assert.Contains("/api/v1/query?query=up", handler.LastUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrometheusQuery_EmptyResult_ReturnsNull()
    {
        var handler = new ScriptedHandler(("""
            {"status":"success","data":{"resultType":"vector","result":[]}}
            """, HttpStatusCode.OK));

        using var client = new PrometheusHttpClient(
            Options.Create(new PrometheusOptions { BaseUrl = "http://prometheus.test:9090" }), handler);

        Assert.Null(await client.QueryAsync("nothing"));
    }

    [Fact]
    public async Task PrometheusQuery_ServerError_ReturnsNull()
    {
        var handler = new ScriptedHandler(("", HttpStatusCode.BadGateway));
        using var client = new PrometheusHttpClient(
            Options.Create(new PrometheusOptions { BaseUrl = "http://prometheus.test:9090" }), handler);

        Assert.Null(await client.QueryAsync("q"));
    }

    /// <summary>
    /// Range-запрос: timestamp приходит ЧИСЛОМ, значение — СТРОКОЙ; парсер обязан
    /// пройти по всем сериям и не упасть на числовом timestamp (прежний дефект).
    /// </summary>
    [Fact]
    public async Task PrometheusQueryRange_MultiSeriesNumericTimestamp_ParsesAll()
    {
        var handler = new ScriptedHandler(("""
            {"status":"success","data":{"resultType":"matrix","result":[
              {"metric":{"instance":"a"},"values":[[1767225600,"10.5"],[1767225660,"11"]]},
              {"metric":{"instance":"b"},"values":[[1767225600,"20"]]}
            ]}}
            """, HttpStatusCode.OK));

        using var client = new PrometheusHttpClient(
            Options.Create(new PrometheusOptions { BaseUrl = "http://prometheus.test:9090" }), handler);

        var points = await client.QueryRangeAsync(
            "process_private_memory_bytes",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromMinutes(1));

        // 3 точки из двух серий.
        Assert.Equal(3, points.Count);
        Assert.Equal(10.5, points[0].Value);
        Assert.Equal("1767225600", points[0].Time);
        Assert.Equal(20.0, points[2].Value);
        // Окно и шаг попали в URL.
        Assert.Contains("start=1767225600", handler.LastUrl, StringComparison.Ordinal);
        Assert.Contains("end=1767247200", handler.LastUrl, StringComparison.Ordinal);
        Assert.Contains("step=60", handler.LastUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrometheusQueryRange_ServerError_ReturnsEmpty()
    {
        var handler = new ScriptedHandler(("", HttpStatusCode.InternalServerError));
        using var client = new PrometheusHttpClient(
            Options.Create(new PrometheusOptions { BaseUrl = "http://prometheus.test:9090" }), handler);

        var points = await client.QueryRangeAsync(
            "q",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromMinutes(1));

        Assert.Empty(points);
    }

    // ---------- Prometheus: alerts / metrics ----------

    [Fact]
    public async Task PrometheusGetAlerts_MalformedJson_ReturnsEmpty()
    {
        // Не-JSON в ответе: парсер обязан проглотить и вернуть пустой список.
        var handler = new ScriptedHandler(("not json at all", HttpStatusCode.OK));
        using var client = new PrometheusHttpClient(
            Options.Create(new PrometheusOptions { BaseUrl = "http://prometheus.test:9090" }), handler);

        Assert.Empty(await client.GetAlertsAsync());
    }

    [Fact]
    public async Task PrometheusGetMetrics_Ok_ReturnsBody()
    {
        var handler = new ScriptedHandler(("go_goroutines 12", HttpStatusCode.OK));
        using var client = new PrometheusHttpClient(
            Options.Create(new PrometheusOptions { BaseUrl = "http://prometheus.test:9090" }), handler);

        Assert.Equal("go_goroutines 12", await client.GetMetricsAsync());
    }

    [Fact]
    public async Task PrometheusGetMetrics_ServerError_ReturnsNull()
    {
        var handler = new ScriptedHandler(("", HttpStatusCode.InternalServerError));
        using var client = new PrometheusHttpClient(
            Options.Create(new PrometheusOptions { BaseUrl = "http://prometheus.test:9090" }), handler);

        Assert.Null(await client.GetMetricsAsync());
    }

    /// <summary>
    /// Мок-обработчик: отдаёт заготовленную последовательность (тело, статус) и
    /// записывает URL последнего запроса. Офлайн, без сети.
    /// </summary>
    private sealed class ScriptedHandler(params (string Body, HttpStatusCode Status)[] responses) : HttpMessageHandler
    {
        private int _index;

        public string LastUrl { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri?.PathAndQuery ?? "";

            var (body, status) = _index < responses.Length
                ? responses[_index]
                : responses[^1];
            _index++;

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
