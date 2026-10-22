namespace ArtasTools.MonitorAgent.Abstractions;

/// <summary>
/// Запрос к источникам контекста.
/// </summary>
/// <param name="Text">Поисковый текст: сообщение об ошибке, имена символов, названия метрик.</param>
/// <param name="TopK">Сколько документов вернуть максимум.</param>
/// <param name="MaxChars">Максимальная длина одного документа в символах.</param>
public sealed record ContextQuery(
    string Text,
    int TopK = 8,
    int MaxChars = 6000)
{
    /// <summary>
    /// Пустой запрос — используется для проверки доступности источника.
    /// </summary>
    public static ContextQuery Empty { get; } = new("", 0, 0);
}

/// <summary>
/// Документ, найденный в источнике контекста.
/// </summary>
/// <param name="Source">Имя источника ("local-folder").</param>
/// <param name="Id">Идентификатор внутри источника: относительный путь или ключ страницы.</param>
/// <param name="Title">Человекочитаемый заголовок.</param>
/// <param name="Content">Содержимое, обрезанное и очищенное от секретов.</param>
/// <param name="Score">Оценка релевантности (больше — лучше).</param>
public sealed record ContextDocument(
    string Source,
    string Id,
    string Title,
    string Content,
    double Score)
{
    /// <summary>Сколько символов реально уйдёт в промпт.</summary>
    public int Length => Content.Length;
}

/// <summary>
/// Результат поиска по всем источникам: документы плюс сведения о том,
/// какие источники были опрошены и какие недоступны (для отчёта).
/// </summary>
/// <param name="Documents">Найденные и отранжированные документы.</param>
/// <param name="QueriedSources">Имена опрошенных источников.</param>
/// <param name="UnavailableSources">Имена недоступных источников.</param>
public sealed record ContextSearchResult(
    IReadOnlyList<ContextDocument> Documents,
    IReadOnlyList<string> QueriedSources,
    IReadOnlyList<string> UnavailableSources);
