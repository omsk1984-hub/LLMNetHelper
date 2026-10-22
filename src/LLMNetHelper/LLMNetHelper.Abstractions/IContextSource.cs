namespace LLMNetHelper.Abstractions;

/// <summary>
/// Источник внешнего контекста (документация, runbooks, схемы БД, тикеты).
/// Агент не знает, откуда пришли документы: файловая система, Confluence, Jira
/// или pgvector. Добавление нового источника = реализация этого интерфейса
/// плюс одна строка регистрации в DI.
/// </summary>
public interface IContextSource
{
    /// <summary>
    /// Стабильное имя источника для логов и конфигурации: "local-folder", "confluence", "jira".
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Проверка доступности. <b>Не бросает исключений</b>: недоступный источник
    /// возвращает false, и агент просто работает без него.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Поиск релевантных документов по запросу.
    /// Возвращает документы, уже обрезанные по <see cref="ContextQuery.MaxChars"/>
    /// и очищенные от секретов.
    /// </summary>
    Task<IReadOnlyList<ContextDocument>> SearchAsync(
        ContextQuery query,
        CancellationToken ct = default);
}
