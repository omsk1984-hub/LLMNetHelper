using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMNetHelper.Core.Json;

/// <summary>
/// Единый профиль сериализации JSON (план 22, этапы 6.3/6.6).
/// <para>
/// Аудит этапа 6.6 показал: до 6.3 настройки <c>JsonSerializerOptions</c> были
/// продублированы в пяти независимых объектах, но <see href="roslyn">семантическая
/// проверка</see> (<c>find_references</c>) выявила, что четыре из пяти —
/// <c>ResponseParser</c>, <c>LokiHttpClient</c>, <c>PrometheusHttpClient</c>,
/// <c>NativeAgentOrchestrator</c> — не читались вообще: разборы внешних
/// JSON-ответов идут через <see cref="JsonDocument"/> и опциями форматтера
/// не пользуются. Мёртвые дубликаты удалены, поэтому «объединять» оказалось
/// нечего и профиль чтения в этом классе не появился.
/// </para>
/// <para>
/// Живой единственный потребитель — <c>JsonReportRenderer</c>. Профили с иной
/// семантикой (<c>JsonLogFormatter</c> — CamelCase-логи, <c>GitLabApiClient</c> —
/// Web-дефолты) сюда не сводятся: они уникальны и их слияние изменило бы
/// wire-формат.
/// </para>
/// <para>
/// DI-регистрация не предусмотрена: <see cref="JsonSerializerOptions"/> —
/// неизменяемая после первого использования настройка форматтера, а не сервис
/// с поведением; подмена в тестах не имеет смысла (семантика фиксируется
/// контрактным тестом <c>JsonProfileTests</c>).
/// </para>
/// </summary>
public static class JsonProfiles
{
    /// <summary>
    /// Профиль записи отчётов: человекочитаемые отступы, null-поля опускаются
    /// (wire-формат отчёта не растёт), регистр имён строгий (схема известна).
    /// </summary>
    public static readonly JsonSerializerOptions Report = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
