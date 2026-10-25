using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMNetHelper.GitLab;

/// <summary>
/// HTTP-клиент для GitLab API.
/// <para>
/// Использует токен из заголовка <c>PRIVATE-TOKEN</c> (или <c>Authorization: Bearer</c>).
/// Поддерживает пагинацию через <c>per_page</c> + <c>page</c>.
/// </para>
/// </summary>
// План 22, этап 2.3: реализация контракта IGitLabApiClient.
public sealed class GitLabApiClient : IGitLabApiClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly GitLabOptions _options;
    private readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public GitLabApiClient(GitLabOptions options, HttpClient http)
    {
        _options = options;
        _http = http;

        // Завершающий слэш обязателен: относительные запросы вида "user"
        // иначе отбрасывают "/api/v4" из базового адреса (Uri.Combine
        // с ведущим слэшем заменяет путь целиком) — GitLab возвращал HTML
        // страницы логина вместо JSON.
        var baseUrl = options.ApiUrl.EndsWith('/')
            ? options.ApiUrl
            : options.ApiUrl + "/";

        _http.BaseAddress = new Uri(baseUrl);
        _http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", options.Token);

        // Без явного Accept GitLab трактует запрос как браузерную навигацию
        // и отдаёт HTML-страницу логина (200, но не JSON).
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>
    /// Получает профиль текущего пользователя (<c>GET /user</c>).
    /// <para>
    /// Используется для проверки валидности токена: невалидный токен даёт
    /// 401 и <see cref="HttpRequestException"/>.
    /// </para>
    /// </summary>
    public async Task<GitLabUser> GetCurrentUserAsync(CancellationToken ct = default)
    {
        var response = await _http.GetStringAsync("user", ct).ConfigureAwait(false);

        var user = JsonSerializer.Deserialize<GitLabUser>(response, _jsonOpts)
            ?? throw new InvalidOperationException("GitLab API вернул пустой ответ /user.");

        return user;
    }

    /// <summary>
    /// Ищет группу по полному пути (<c>GET /groups?search=...</c>).
    /// <para>
    /// GitLab-параметр <c>search</c> ищет по подстроке имени, поэтому после
    /// запроса выполняется точное сравнение <c>full_path</c> (без учёта регистра).
    /// </para>
    /// </summary>
    /// <param name="fullPath">Полный путь группы, например "GPM/backend".</param>
    /// <returns>Найденная группа или <c>null</c>, если группы с таким путём нет.</returns>
    public async Task<GitLabGroup?> FindGroupByPathAsync(string fullPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        // Ищем по последнему сегменту пути — так результат заметно уже,
        // а точное совпадение full_path ниже отсекает ложные попадания.
        var lastSegment = fullPath[(fullPath.LastIndexOf('/') + 1)..];
        var encoded = Uri.EscapeDataString(lastSegment);

        int page = 1;
        const int perPage = 100;

        while (true)
        {
            var url = $"groups?search={encoded}&per_page={perPage}&page={page}";
            var response = await _http.GetStringAsync(url, ct).ConfigureAwait(false);

            var groups = JsonSerializer.Deserialize<GitLabGroup[]>(response, _jsonOpts) ?? [];

            var match = groups.FirstOrDefault(g =>
                string.Equals(g.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                return match;
            }

            if (groups.Length < perPage)
            {
                return null;
            }

            page++;
        }
    }

    /// <summary>
    /// Получает репозитории группы с автопагинацией.
    /// <para>
    /// Архивные проекты исключаются, если <see cref="GitLabOptions.ExcludeArchived"/>
    /// (= по умолчанию <c>true</c>): GitLab отдаёт их наравне с боевыми, и в группе
    /// <c>GPM/backend</c> это 43 из 103 репозиториев. Фильтрация — серверным
    /// параметром <c>archived=false</c> плюс клиентская страховка, потому что
    /// документированный для GitLab <c>exclude_archived=true</c> на проверенной
    /// версии 19.2.9 игнорируется.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<GitLabProject>> GetGroupReposAsync(int groupId, CancellationToken ct = default)
    {
        var repos = new List<GitLabProject>();
        int page = 1;
        const int perPage = 100;

        var archivedQuery = _options.ExcludeArchived ? "&archived=false" : "";

        while (true)
        {
            // simple=true НЕ используется: в «простом» представлении GitLab не отдаёт
            // поле archived (проверено на 19.2.9), и клиентская страховка
            // фильтрации становилась бы пустой. Модель десериализует только
            // объявленные поля, поэтому раздутый ответ не проблема.
            var url = $"groups/{groupId}/projects?per_page={perPage}&page={page}&include_subgroups=true{archivedQuery}";
            var response = await _http.GetStringAsync(url, ct).ConfigureAwait(false);

            var projects = JsonSerializer.Deserialize<GitLabProject[]>(response, _jsonOpts) ?? [];

            if (projects.Length == 0)
            {
                break;
            }

            // Страховка на случай, если сервер параметр проигнорирует.
            if (_options.ExcludeArchived)
            {
                projects = projects.Where(static p => !p.Archived).ToArray();
            }

            repos.AddRange(projects);

            // Если получили меньше, чем per_page — это последняя страница
            if (projects.Length < perPage)
            {
                break;
            }

            page++;
        }

        return repos;
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
