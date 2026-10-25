namespace LLMNetHelper.Abstractions;

/// <summary>
/// Вид правки кода (план 22, этап 1.3): вместо магических строк
/// <c>"ReplaceMethodBody" | "InsertNullCheck"</c>.
/// </summary>
public enum ChangeKind
{
    /// <summary>Заменить тело метода (сигнатуру менять нельзя).</summary>
    ReplaceMethodBody,

    /// <summary>Добавить проверку на null в начало тела метода.</summary>
    InsertNullCheck,

    /// <summary>
    /// Нераспознанный вид правки из ответа модели. Такой вид обязан привести
    /// к отказу применения (<c>PatchResult.Fail</c>), как и раньше для
    /// произвольной строки — поведение транзакции не меняется.
    /// </summary>
    Unknown
}

/// <summary>
/// Граничный конвертер вида правки (план 22, этап 1.3). Wire-формат не меняется:
/// канонические имена <c>"ReplaceMethodBody"</c>/<c>"InsertNullCheck"</c>.
/// Разбор — точное совпадение (как раньше в switch-выражении <c>AstPatcher</c>).
/// </summary>
public static class ChangeKindFormat
{
    /// <summary>
    /// Разбор строки <c>change_kind</c> из ответа модели.
    /// Нераспознанное значение — <see cref="ChangeKind.Unknown"/> (без исключения).
    /// </summary>
    public static ChangeKind Parse(string? value) => value switch
    {
        "ReplaceMethodBody" => ChangeKind.ReplaceMethodBody,
        "InsertNullCheck" => ChangeKind.InsertNullCheck,
        _ => ChangeKind.Unknown
    };

    /// <summary>Wire-строка вида правки (для сообщений коммита и логов).</summary>
    public static string ToWire(ChangeKind kind) => kind switch
    {
        ChangeKind.ReplaceMethodBody => "ReplaceMethodBody",
        ChangeKind.InsertNullCheck => "InsertNullCheck",
        _ => "Unknown"
    };
}
