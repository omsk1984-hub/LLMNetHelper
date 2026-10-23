using System.Text.Json.Serialization;

namespace LLMNetHelper.GitLab;

/// <summary>
/// Информация о репозитории из GitLab API.
/// <para>
/// GitLab API отдаёт поля в snake_case, поэтому каждое свойство помечено
/// <see cref="JsonPropertyNameAttribute"/>: без этого десериализация с
/// <c>JsonSerializerDefaults.Web</c> их не сопоставит.
/// </para>
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
/// <param name="Archived">Архивный ли проект (GitLab возвращает такие наравне с боевыми).</param>
public sealed record GitLabProject(
    int Id,
    string Name,
    [property: JsonPropertyName("path_with_namespace")] string PathWithNamespace,
    [property: JsonPropertyName("http_url_to_repo")] string HttpUrlToRepo,
    [property: JsonPropertyName("ssh_url_to_repo")] string? SshUrlToRepo,
    [property: JsonPropertyName("default_branch")] string DefaultBranch,
    [property: JsonPropertyName("last_activity_at")] string? LastActivityAt,
    [property: JsonPropertyName("open_issues_count")] int OpenIssuesCount,
    [property: JsonPropertyName("stars_count")] int StarsCount,
    bool Archived = false);
