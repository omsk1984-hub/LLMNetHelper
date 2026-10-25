using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMNetHelper.Core.Json;

/// <summary>
/// Единые профили сериализации JSON (план 22, этап 6.3).
/// <para>
/// До этапа настройки были продублированы в пяти независимых объектах
/// (<c>ResponseParser</c>, <c>LokiHttpClient</c>, <c>PrometheusHttpClient</c>,
/// <c>NativeAgentOrchestrator</c>, <c>JsonReportRenderer</c>): любое изменение
/// семантики (регистронезависимость, отступы, игнор null-ей) требовало синхронной
/// правки в пяти местах и нигде не проверялось. Теперь — единственный источник
/// истины; потребители держат ссылки на общие экземпляры.
/// </para>
/// <para>
/// DI-регистрация не предусмотрена: <see cref="JsonSerializerOptions"/> —
/// неизменяемая после первого использования настройка форматтера, а не сервис
/// с поведением; подмена в тестах не имеет смысла (профили фиксируются
/// контрактными тестами <c>JsonProfileTests</c>).
/// </para>
/// <para>
/// Профили с иной семантикой (CamelCase-логи <c>JsonLogFormatter</c>,
/// Web-дефолты <c>GitLabApiClient</c>) сюда не сводятся: они уникальны и их
/// слияние изменило бы wire-формат.
/// </para>
/// </summary>
public static class JsonProfiles
{
    /// <summary>
    /// Профиль чтения внешних JSON-ответов (LLM, Loki, Prometheus): имена
    /// свойств регистронезависимы, потому что провайдеры не гарантируют регистр.
    /// </summary>
    public static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = true
    };

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
