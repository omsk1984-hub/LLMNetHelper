using System.Reflection;
using System.Text.Json;
using LLMNetHelper.Core.Json;

namespace LLMNetHelper.Tests.Analysis;

/// <summary>
/// Контрактные тесты единых профилей сериализации JSON (план 22, этап 6.3).
/// <para>
/// До этапа настройки <c>JsonSerializerOptions</c> были продублированы в четырёх
/// независимых объектах (<c>ResponseParser</c>, <c>LokiHttpClient</c>,
/// <c>PrometheusHttpClient</c>, <c>NativeAgentOrchestrator</c>) и ещё одном в
/// <c>JsonReportRenderer</c>: дрейф значений был вопросом времени, потому что
/// ни один тест не связывал их с единым источником.
/// </para>
/// <para>
/// Профили не подменяются DI: <see cref="JsonSerializerOptions"/> — неизменяемая
/// после первого использования настройка форматтера, а не сервис с поведением.
/// Тесты фиксируют её семантику и единственный источник истины.
/// </para>
/// </summary>
public class JsonProfileTests
{
    private sealed record Payload(string? Verdict, string? Summary);

    [Fact]
    public void ReadProfile_IsCaseInsensitive()
    {
        // Ответы LLM/Loki/Prometheus приходят в произвольном регистре имён свойств.
        var parsed = JsonSerializer.Deserialize<Payload>(
            """{"VERDICT":"Warning","summary":"рост ошибок"}""", JsonProfiles.Read);

        Assert.NotNull(parsed);
        Assert.Equal("Warning", parsed.Verdict);
        Assert.Equal("рост ошибок", parsed.Summary);
    }

    [Fact]
    public void ReportProfile_WritesIndentedAndOmitsNulls()
    {
        var json = JsonSerializer.Serialize(new Payload("OK", null), JsonProfiles.Report);

        // Отчёт человекочитаемый: отступы присутствуют.
        Assert.Contains(Environment.NewLine, json);
        // Null-поля не попадают в тело отчёта (wire-формат не растёт).
        Assert.DoesNotContain("Summary", json);
        Assert.Contains("\"Verdict\": \"OK\"", json);
    }

    [Fact]
    public void ReportProfile_DoesNotReuseReadProfileInstance()
    {
        // Разные назначения (чтение/запись) — разные экземпляры; иначе
        // PropertyNameCaseInsensitive просочился бы в записываемые отчёты.
        Assert.False(ReferenceEquals(JsonProfiles.Read, JsonProfiles.Report));
        Assert.False(JsonProfiles.Report.PropertyNameCaseInsensitive);
    }

    [Fact]
    public void Consumers_UseSharedInstances()
    {
        // Главный инвариант этапа: потребители не держат независимых копий.
        AssertSameInstance(
            typeof(LLMNetHelper.Core.Analysis.ResponseParser).GetField("JsonOpts", BindingFlags.NonPublic | BindingFlags.Static),
            JsonProfiles.Read);

        AssertSameInstance(
            typeof(LLMNetHelper.Infrastructure.Loki.LokiHttpClient).GetField("JsonOpts", BindingFlags.NonPublic | BindingFlags.Static),
            JsonProfiles.Read);

        AssertSameInstance(
            typeof(LLMNetHelper.Infrastructure.Prometheus.PrometheusHttpClient).GetField("JsonOpts", BindingFlags.NonPublic | BindingFlags.Static),
            JsonProfiles.Read);

        AssertSameInstance(
            typeof(LLMNetHelper.Agent.Orchestration.NativeAgentOrchestrator).GetField("JsonOpts", BindingFlags.NonPublic | BindingFlags.Static),
            JsonProfiles.Read);

        AssertSameInstance(
            typeof(LLMNetHelper.Core.Reporting.JsonReportRenderer).GetField("JsonOpts", BindingFlags.NonPublic | BindingFlags.Static),
            JsonProfiles.Report);
    }

    private static void AssertSameInstance(FieldInfo? field, JsonSerializerOptions expected)
    {
        Assert.NotNull(field);
        Assert.Same(expected, field.GetValue(null));
    }
}
