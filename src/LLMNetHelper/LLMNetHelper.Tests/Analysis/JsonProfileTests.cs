using System.Reflection;
using System.Text.Json;
using LLMNetHelper.Core.Json;

namespace LLMNetHelper.Tests.Analysis;

/// <summary>
/// Контрактный тест профиля сериализации отчётов (план 22, этапы 6.3/6.6).
/// <para>
/// Фиксирует семантику wire-формата отчёта и единственный источник истины.
/// Профиль не подменяется DI: <see cref="JsonSerializerOptions"/> —
/// неизменяемая после первого использования настройка форматтера, а не сервис
/// с поведением.
/// </para>
/// <para>
/// Аудит 6.6 удалил четыре мёртвых дубликата <c>JsonOpts</c>
/// (<c>ResponseParser</c>, <c>LokiHttpClient</c>, <c>PrometheusHttpClient</c>,
/// <c>NativeAgentOrchestrator</c>): они не читались, потому что разборы идут
/// через <see cref="JsonDocument"/>. Профиль чтения поэтому не вводился.
/// </para>
/// </summary>
public class JsonProfileTests
{
    private sealed record Payload(string? Verdict, string? Summary);

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
    public void ReportProfile_IsStrictAboutPropertyNames()
    {
        // Схема отчёта известна: регистронезависимость чтения здесь не нужна
        // и не должна просочиться в записываемый JSON.
        Assert.False(JsonProfiles.Report.PropertyNameCaseInsensitive);
    }

    [Fact]
    public void JsonReportRenderer_UsesSharedReportProfile()
    {
        // Инвариант этапа: рендерер отчёта не держит независимой копии настроек.
        var field = typeof(LLMNetHelper.Core.Reporting.JsonReportRenderer)
            .GetField("_JsonOpts", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.Same(JsonProfiles.Report, field.GetValue(null));
    }
}
