using System.Net;
using System.Text;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Infrastructure.Loki;
using LLMNetHelper.Infrastructure.Prometheus;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMNetHelper.Tests.Analysis;

/// <summary>
/// Тесты типизированных моделей Loki/Prometheus (план 22, этап 1.6):
/// контракты <see cref="ILokiClient.QueryEntriesAsync"/> и
/// <see cref="IPrometheusClient.GetAlertsAsync"/> возвращают записи
/// <see cref="LokiEntry"/>/<see cref="PrometheusAlert"/> вместо
/// <c>Dictionary<string, object></c>; <c>GetAlertingRulesAsync</c>
/// (мёртвый код, 0 потребителей) удалён из контракта.
/// Тесты офлайн: транспорт подменён мок-обработчиком (internal-конструкторы).
/// </summary>
public class MonitoringTypedModelsTests
{
    /// <summary>
    /// Loki query_range: streams → список LokiEntry с метками, timestamp и текстом.
    /// </summary>
    [Fact]
    public async Task LokiQueryEntries_ReturnsTypifiedEntries()
    {
        var lokiJson = """
            {"status":"success","data":{"resultType":"streams","result":[
              {"stream":{"job":"artas_writer","lvl":"fail"},
               "values":[["1767225600000000000","boom: timeout"],["1767225601000000000","second line"]]}
            ]}}
            """;

        var client = new LokiHttpClient(
            Options.Create(new LokiOptions { BaseUrl = "http://loki.test:3100" }),
            new CannedHandler(lokiJson));

        var entries = await client.QueryEntriesAsync(
            "{job=\"artas_writer\"}",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc),
            100);

        Assert.Equal(2, entries.Count);
        Assert.Equal("job=\"artas_writer\",lvl=\"fail\"", entries[0].Stream);
        Assert.Equal("1767225600000000000", entries[0].Timestamp);
        Assert.Equal("boom: timeout", entries[0].Text);
        Assert.Equal("second line", entries[1].Text);
    }

    /// <summary>
    /// Prometheus /api/v1/alerts → список PrometheusAlert (имя, состояние, метки).
    /// </summary>
    [Fact]
    public async Task PrometheusGetAlerts_ReturnsTypifiedAlerts()
    {
        var promJson = """
            {"status":"success","data":{"alerts":[
              {"labels":{"alertname":"HighMemory","job":"artas_writer","severity":"critical"},
               "state":"firing"},
              {"labels":{"alertname":"DiskFull"},
               "state":"pending"}
            ]}}
            """;

        var client = new PrometheusHttpClient(
            Options.Create(new PrometheusOptions { BaseUrl = "http://prometheus.test:9090" }),
            new CannedHandler(promJson));

        var alerts = await client.GetAlertsAsync();

        Assert.Equal(2, alerts.Count);
        Assert.Equal("HighMemory", alerts[0].Alertname);
        Assert.Equal("firing", alerts[0].State);
        Assert.Equal("artas_writer", alerts[0].Labels["job"]);
        Assert.Equal("critical", alerts[0].Labels["severity"]);
        Assert.Equal("DiskFull", alerts[1].Alertname);
        Assert.Equal("pending", alerts[1].State);
        // Метки второго алерта: только alertname (job/severity отсутствуют).
        Assert.Equal(new Dictionary<string, string> { ["alertname"] = "DiskFull" }, alerts[1].Labels);
    }

    /// <summary>
    /// Пустой ответ Prometheus — пустой список без исключения.
    /// </summary>
    [Fact]
    public async Task PrometheusGetAlerts_EmptyPayload_ReturnsEmpty()
    {
        var client = new PrometheusHttpClient(
            Options.Create(new PrometheusOptions { BaseUrl = "http://prometheus.test:9090" }),
            new CannedHandler("""{"status":"success","data":{"alerts":[]}}"""));

        var alerts = await client.GetAlertsAsync();

        Assert.Empty(alerts);
    }

    /// <summary>
    /// Мок-обработчик: отдаёт фиксированный JSON на любой запрос (офлайн).
    /// </summary>
    private sealed class CannedHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
