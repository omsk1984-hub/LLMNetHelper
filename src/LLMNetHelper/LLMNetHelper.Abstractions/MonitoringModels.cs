namespace LLMNetHelper.Abstractions;

/// <summary>
/// Запись сырых логов из Loki (план 22, этап 1.6): типизированная замена
/// <c>Dictionary<string, object></c> из контракта
/// <see cref="ILokiClient.QueryEntriesAsync"/>.
/// </summary>
/// <param name="Stream">Строка меток потока вида <c>job="artas_writer",lvl="fail"</c>.</param>
/// <param name="Timestamp">Метка времени записи (наносекунды unix, строкой — как отдаёт Loki).</param>
/// <param name="Text">Текст записи лога.</param>
public sealed record LokiEntry(string Stream, string Timestamp, string Text);

/// <summary>
/// Активный (firing) алерт Prometheus (план 22, этап 1.6): типизированная замена
/// <c>Dictionary<string, object?></c> из контракта
/// <see cref="IPrometheusClient.GetAlertsAsync"/>.
/// Поля — только те, что реально читают потребители; остальные поля ответа
/// Prometheus игнорируются (раньше раскладывались в словарь «на всякий случай»).
/// </summary>
/// <param name="Alertname">Имя правила алерта.</param>
/// <param name="State">Состояние: <c>firing</c> | <c>pending</c> | <c>inactive</c>.</param>
/// <param name="Labels">Метки алерта (job, instance, severity и т.п.).</param>
public sealed record PrometheusAlert(string Alertname, string State, IReadOnlyDictionary<string, string> Labels);
