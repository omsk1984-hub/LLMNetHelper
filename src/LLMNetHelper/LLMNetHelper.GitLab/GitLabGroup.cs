using System.Text.Json.Serialization;

namespace LLMNetHelper.GitLab;

/// <summary>
/// Информация о группе GitLab (ответ <c>GET /groups</c>).
/// <para>
/// GitLab API отдаёт поля в snake_case — сопоставление через
/// <see cref="JsonPropertyNameAttribute"/>.
/// </para>
/// </summary>
/// <param name="Id">Уникальный ID группы.</param>
/// <param name="Name">Имя группы.</param>
/// <param name="FullPath">Полный путь вида "GPM/backend".</param>
/// <param name="ParentId">ID родительской группы (null для корневой).</param>
public sealed record GitLabGroup(
    int Id,
    string Name,
    [property: JsonPropertyName("full_path")] string FullPath,
    [property: JsonPropertyName("parent_id")] int? ParentId);

/// <summary>
/// Текущий пользователь GitLab (ответ <c>GET /user</c>).
/// <para>
/// Используется для проверки валидности токена: запрос с невалидным
/// токеном возвращает 401, с валидным — 200 и профиль пользователя.
/// </para>
/// </summary>
/// <param name="Id">Уникальный ID пользователя.</param>
/// <param name="Username">Логин (username).</param>
/// <param name="Name">Отображаемое имя.</param>
public sealed record GitLabUser(
    int Id,
    string Username,
    string Name);
