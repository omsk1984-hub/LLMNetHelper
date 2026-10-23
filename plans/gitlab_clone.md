# План: GitLab Repository Manager

## Задача
Добавить возможность сервису LLMNetHelper автоматически клонировать и обновлять все репозитории из группы GitLab `GPM/backend` (https://gitlab.intellt.ru/GPM/backend).

---

## Архитектура

Новый модуль `LLMNetHelper.GitLab` добавляется в решение. Он включает:
- **GitLabOptions** — конфигурация (URL, токен, ID группы, каталог)
- **GitLabApiClient** — HTTP-клиент для GitLab API (список репозиториев группы)
- **GitLabService** — клонирование/обновление репозиториев через `git` CLI
- **GitLabCloneTool** — инструмент агента (`gitlab_list_repos`, `gitlab_clone_all`, `gitlab_update_repo`)

---

## Этап 1. GitLabOptions (конфигурация)

**Файл:** `LLMNetHelper.GitLab/GitLabOptions.cs`

Параметры:
- `Enabled` (bool, по умолчанию `false`) — безопасный дефолт
- `ApiUrl` (string) — `https://gitlab.intellt.ru/api/v4`
- `GroupId` (int) — ID группы `GPM`
- `GroupName` (string) — `GPM/backend` (для логов)
- `Token` (string) — токен для API (из env)
- `CloneRoot` (string) — локальный каталог для клонирования
- `CloneTimeoutSeconds` (int) — таймаут клонирования (300)
- `UpdateTimeoutSeconds` (int) — таймаут обновления (120)

---

## Этап 2. GitLabApiClient (GitLab API)

**Файл:** `LLMNetHelper.GitLab/GitLabApiClient.cs`

Использует `HttpClient` + `System.Text.Json`.

Методы:
- `GetGroupReposAsync(int groupId, ct)` — GET `/groups/{id}/projects?simple=true&per_page=100&include_subgroups=true`
- Возвращает `List<GitLabProject>` с полями: `id`, `name`, `path_with_namespace`, `http_url_to_repo`, `default_branch`, `last_activity_at`

---

## Этап 3. GitLabService (клонирование/обновление)

**Файл:** `LLMNetHelper.GitLab/GitLabService.cs`

Использует `ProcessRunner.RunAsync` (git CLI) — тот же паттерн, что в `GitService`.

Методы:
- `ListReposAsync(ct)` — вызывает `GitLabApiClient.GetGroupReposAsync`
- `CloneAllAsync(progress, ct)` — для каждого репозитория:
  - Если папка существует → `git -C <dir> pull`
  - Иначе → `git clone --depth=1 <url> <dir>`
- `UpdateRepoAsync(repoPath, ct)` — `git -C <path> fetch --all && git -C <path> pull`
- `GetRepoStatusAsync(repoPath, ct)` — `git status --porcelain`

---

## Этап 4. GitLabCloneTool (инструмент агента)

**Файл:** `LLMNetHelper.GitLab/GitLabCloneTool.cs`

Три инструмента:

1. **`gitlab_list_repos`** — список репозиториев группы
   - Параметры: `group_id` (int, optional, defaults to config)
   - Возвращает: JSON со списком {name, url, branch, lastActivity}

2. **`gitlab_clone_all`** — клонировать/обновить все репозитории
   - Параметры: `force_clone` (bool, false — если true, удалить и клонировать заново)
   - Возвращает: итог {total, cloned, updated, failed, details[]}

3. **`gitlab_update_repo`** — обновить конкретный репозиторий
   - Параметры: `repo_name` (string), `repo_path` (string, optional)
   - Возвращает: статус обновления

---

## Этап 5. Регистрация в DI

**Файл:** `LLMNetHelper.GitLab/GitLabRegistration.cs`

- `services.AddSingleton<GitLabApiClient>()`
- `services.AddSingleton<GitLabService>()`
- `services.AddSingleton<IAgentTool, GitLabCloneTool>()`

**Файл:** `LLMNetHelper.DependencyInjection.cs`

- Добавить `services.AddGitLab(options)` после `AddCodeChange`

---

## Этап 6. Конфигурация

**Файл:** `appsettings.json`

```json
{
  "GitLab": {
    "Enabled": false,
    "ApiUrl": "https://gitlab.intellt.ru/api/v4",
    "GroupId": 42,
    "GroupName": "GPM/backend",
    "Token": "",
    "CloneRoot": "repos/GPM",
    "CloneTimeoutSeconds": 300,
    "UpdateTimeoutSeconds": 120
  }
}
```

---

## Этап 7. Сборка и тесты

1. Добавить `LLMNetHelper.GitLab.csproj` в решение `.slnx`
2. Добавить юнит-тесты для `GitLabApiClient` (моки HTTP)
3. Проверить `dotnet build` и `dotnet test`

---

## Итого

| Файл | Описание |
|------|----------|
| `GitLabOptions.cs` | Конфигурация |
| `GitLabApiClient.cs` | HTTP-клиент к GitLab API |
| `GitLabService.cs` | Клонирование/обновление через git CLI |
| `GitLabCloneTool.cs` | 3 инструмента для агента |
| `GitLabRegistration.cs` | DI-регистрация |
| `LLMNetHelper.GitLab.csproj` | Проект |
| `DependencyInjection.cs` | Подключение в главный DI |
| `appsettings.json` | Конфигурация |
