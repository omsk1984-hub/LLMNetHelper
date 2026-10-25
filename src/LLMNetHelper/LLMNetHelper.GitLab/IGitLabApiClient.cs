namespace LLMNetHelper.GitLab;

/// <summary>
/// Контракт HTTP-клиента GitLab API (план 22, этап 2.3). Извлечён из
/// <see cref="GitLabApiClient"/>, чтобы <see cref="GitLabService"/> зависел
/// от абстракции, а не от конкретного класса — подмена в тестах без сети.
/// <para>
/// Размещён в проекте GitLab (не в Abstractions): сигнатура использует
/// доменные типы проекта (<see cref="GitLabUser"/>, <see cref="GitLabGroup"/>,
/// <see cref="GitLabProject"/>), и их утечка в общий слой абстракций
/// нарушила бы архитектурные границы.
/// </para>
/// <para>
/// <see cref="IDisposable"/> в контракт не включён: жизненным циклом клиента
/// владеет DI-контейнер (синглтон), а требовать disposal от любой реализации —
/// нарушение LSP (см. план 22, этап 2.7 про <c>IFactStore</c>).
/// </para>
/// </summary>
public interface IGitLabApiClient
{
    /// <summary>
    /// Получает профиль текущего пользователя (<c>GET /user</c>).
    /// Используется для проверки валидности токена.
    /// </summary>
    Task<GitLabUser> GetCurrentUserAsync(CancellationToken ct = default);

    /// <summary>
    /// Ищет группу по полному пути (<c>GET /groups?search=...</c>),
    /// с точным сравнением <c>full_path</c> без учёта регистра.
    /// </summary>
    /// <param name="fullPath">Полный путь группы, например "GPM/backend".</param>
    /// <returns>Найденная группа или <c>null</c>.</returns>
    Task<GitLabGroup?> FindGroupByPathAsync(string fullPath, CancellationToken ct = default);

    /// <summary>
    /// Получает репозитории группы с автопагинацией
    /// (архивные исключаются при <c>GitLabOptions.ExcludeArchived</c>).
    /// </summary>
    Task<IReadOnlyList<GitLabProject>> GetGroupReposAsync(int groupId, CancellationToken ct = default);
}
