namespace LLMNetHelper.GitLab;

/// <summary>
/// Информация о репозитории из GitLab API.
/// </summary>
/// <param name="Id">Уникальный ID проекта.</param>
/// <param name="Name">Имя проекта.</param>
/// <param name="PathWithNamespace">Путь вида "group/project".</param>
/// <param name="HttpUrlToRepo">URL для клонирования (HTTPS).</param>
/// <param name="SshUrlToRepo">URL для клонирования (SSH).</param>
/// <param name="DefaultBranch">Основная ветка.</param>
/// <param name="LastActivityAt">Последняя активность (ISO 8601).</param>
/// <param name="OpenIssuesCount">Количество открытых issue.</param>
/// <param name="StarsCount">Количество звёзд.</param>
public sealed record GitLabProject(
    int Id,
    string Name,
    string PathWithNamespace,
    string HttpUrlToRepo,
    string? SshUrlToRepo,
    string DefaultBranch,
    string? LastActivityAt,
    int OpenIssuesCount,
    int StarsCount);
