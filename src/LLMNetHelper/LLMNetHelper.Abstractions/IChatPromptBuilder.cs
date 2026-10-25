namespace LLMNetHelper.Abstractions;

/// <summary>
/// Контракт сборки промптов чата (план 22, этап 6.1). Извлечён из статического
/// <c>LLMNetHelper.Chat.ChatPromptBuilder</c>, чтобы <c>ChatSession</c> зависел
/// от абстракции: промпты становятся подменяемыми в тестах и верционируемыми.
/// <para>
/// В отличие от ночного агента (<see cref="IAgentPromptBuilder"/>), чат отвечает
/// в свободной форме (Markdown), а не по JSON-схеме диагноза.
/// </para>
/// </summary>
public interface IChatPromptBuilder
{
    /// <summary>
    /// Системный промпт: роль ассистента по коду репозиториев группы,
    /// ограничения (только предоставленные фрагменты, цитирование путей).
    /// </summary>
    /// <param name="scopeDescription">Описание текущей области (путь, репозиторий).</param>
    /// <param name="withTools">true — включён режим /tools (модель может вызывать инструменты).</param>
    /// <param name="toolDescriptions">Описания инструментов (для режима /tools).</param>
    string BuildSystemPrompt(string scopeDescription, bool withTools, string? toolDescriptions = null);

    /// <summary>Пользовательский промпт: вопрос + найденные фрагменты контекста.</summary>
    string BuildUserPrompt(string question, IReadOnlyList<ContextDocument> documents);
}
