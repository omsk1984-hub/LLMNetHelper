namespace LLMNetHelper.Core.Analysis;

/// <summary>
/// Извлечение JSON-объекта из ответа модели (план 22, этап 3.2).
/// <para>
/// Единая реализация вместо трёх копий (<c>NativeAgentOrchestrator</c>,
/// <c>ChatAgentLoop</c>, частично <c>ResponseParser.ExtractJsonBlock</c>):
/// убирает markdown-обёртку и берёт подстроку между первым <c>{</c> и
/// последним <c>}</c>.
/// </para>
/// <para>
/// Размещён в Core (не в Abstractions): сервисная утилита без доменных типов,
/// её слияние в общий слой абстракций засорило бы контракты.
/// </para>
/// </summary>
public static class JsonText
{
    /// <summary>
    /// Возвращает подстроку между первым <c>{</c> и последним <c>}</c>,
    /// либо <c>null</c>, если корректный объект не найден.
    /// </summary>
    public static string? Extract(string text)
    {
        if (text is null)
        {
            return null;
        }

        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var end = text.LastIndexOf('}');
        if (end <= start)
        {
            return null;
        }

        return text[start..(end + 1)];
    }
}