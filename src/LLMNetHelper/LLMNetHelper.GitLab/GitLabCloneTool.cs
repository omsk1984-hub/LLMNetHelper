using System.Text.Json;
using LLMNetHelper.Abstractions;

namespace LLMNetHelper.GitLab;

/// <summary>
/// Инструмент для просмотра списка репозиториев группы GitLab.
/// </summary>
public sealed class GitLabListReposTool : IAgentTool
{
    private readonly GitLabService _service;
    private readonly GitLabOptions _options;

    public GitLabListReposTool(GitLabService service, GitLabOptions options)
    {
        _service = service;
        _options = options;
    }

    public string Name => "gitlab_list_repos";

    public string Description =>
        "Получает список всех репозиториев в группе GitLab. " +
        "Возвращает имя, URL, основную ветку и дату последней активности. " +
        "Используйте перед gitlab_clone_all или gitlab_update_repo.";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "group_id": {
              "type": "integer",
              "description": "ID группы GitLab (если не указано — используется из конфигурации)"
            }
          }
        }
        """;

    public async Task<string> InvokeAsync(string parametersJson, CancellationToken ct = default)
    {
        int groupId;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson);
            groupId = doc.RootElement.TryGetProperty("group_id", out var g) && g.TryGetInt32(out var v)
                ? v
                : _options.GroupId;
        }
        catch (JsonException ex)
        {
            return $"Ошибка разбора аргументов: {ex.Message}";
        }

        if (groupId <= 0)
        {
            return $"Неверный group_id: {groupId}. Укажите положительное число.";
        }

        try
        {
            var repos = await _service.ListReposAsync(ct).ConfigureAwait(false);

            if (repos.Count == 0)
            {
                return $"В группе {groupId} репозиториев не найдено.";
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Найдено репозиториев: {repos.Count}\n");

            foreach (var repo in repos)
            {
                sb.AppendLine($"- {repo.PathWithNamespace}");
                sb.AppendLine($"  Ветка: {repo.DefaultBranch}");
                sb.AppendLine($"  URL: {repo.HttpUrlToRepo}");
                if (repo.LastActivityAt != null)
                {
                    sb.AppendLine($"  Последняя активность: {repo.LastActivityAt}");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (InvalidOperationException ex)
        {
            return $"GitLab отключён: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Ошибка получения списка: {ex.Message}";
        }
    }
}

/// <summary>
/// Инструмент для клонирования/обновления всех репозиториев группы.
/// </summary>
public sealed class GitLabCloneAllTool : IAgentTool
{
    private readonly GitLabService _service;

    public GitLabCloneAllTool(GitLabService service)
    {
        _service = service;
    }

    public string Name => "gitlab_clone_all";

    public string Description =>
        "Клонирует или обновляет все репозитории группы GitLab. " +
        "Если репозиторий уже существует — выполняет git pull. " +
        "Используйте force_clone=true для полного переклонирования.";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "force_clone": {
              "type": "boolean",
              "description": "Удалить существующие репозитории и клонировать заново (по умолчанию false)"
            }
          }
        }
        """;

    public async Task<string> InvokeAsync(string parametersJson, CancellationToken ct = default)
    {
        bool forceClone = false;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson);
            if (doc.RootElement.TryGetProperty("force_clone", out var f) && f.GetBoolean() == true)
            {
                forceClone = true;
            }
        }
        catch (JsonException ex)
        {
            return $"Ошибка разбора аргументов: {ex.Message}";
        }

        try
        {
            var result = await _service.CloneAllAsync(forceClone, ct).ConfigureAwait(false);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(result.ToString());

            if (result.Details.Count > 0)
            {
                sb.AppendLine("\nДетали:");
                foreach (var detail in result.Details)
                {
                    var status = detail.Success ? "ok" : "FAIL" + (detail.Error != null ? $" — {detail.Error}" : "");
                    sb.AppendLine($"  - {detail.RepoName}: {detail.Action} ({status})");
                }
            }

            return sb.ToString();
        }
        catch (InvalidOperationException ex)
        {
            return $"GitLab отключён: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Ошибка пакетной операции: {ex.Message}";
        }
    }
}

/// <summary>
/// Инструмент для обновления конкретного репозитория.
/// </summary>
public sealed class GitLabUpdateRepoTool : IAgentTool
{
    private readonly GitLabService _service;

    public GitLabUpdateRepoTool(GitLabService service)
    {
        _service = service;
    }

    public string Name => "gitlab_update_repo";

    public string Description =>
        "Обновляет конкретный репозиторий группы GitLab. " +
        "Если репозиторий не существует локально — клонирует его. " +
        "Если существует — выполняет git pull.";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "repo_name": {
              "type": "string",
              "description": "Имя репозитория (например, 'core-api' или 'GPM/backend-core-api')"
            }
          },
          "required": ["repo_name"]
        }
        """;

    public async Task<string> InvokeAsync(string parametersJson, CancellationToken ct = default)
    {
        string repoName;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson);
            repoName = doc.RootElement.TryGetProperty("repo_name", out var n) ? n.GetString() ?? "" : "";
        }
        catch (JsonException ex)
        {
            return $"Ошибка разбора аргументов: {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(repoName))
        {
            return "Укажите repo_name.";
        }

        try
        {
            var result = await _service.UpdateRepoAsync(repoName, ct: ct).ConfigureAwait(false);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Репозиторий: {result.RepoName}");
            sb.AppendLine($"Действие: {result.Action}");
            sb.AppendLine($"Локальный путь: {result.LocalPath}");
            sb.AppendLine($"Статус: {(result.Success ? "ok" : "FAIL")}");

            if (!result.Success && result.Error != null)
            {
                sb.AppendLine($"Ошибка: {result.Error}");
            }

            sb.AppendLine($"Время: {result.DurationMs} мс");

            return sb.ToString();
        }
        catch (InvalidOperationException ex)
        {
            return $"GitLab отключён: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Ошибка обновления: {ex.Message}";
        }
    }
}
