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
public sealed class GitLabApiClient : IDisposable
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
        _http.BaseAddress = new Uri(options.ApiUrl);
        _http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", options.Token);
    }

    /// <summary>
    /// Получает все репозитории группы с автопагинацией.
    /// </summary>
    public async Task<IReadOnlyList<GitLabProject>> GetGroupReposAsync(int groupId, CancellationToken ct = default)
    {
        var repos = new List<GitLabProject>();
        int page = 1;
        const int perPage = 100;

        while (true)
        {
            var url = $"/groups/{groupId}/projects?per_page={perPage}&page={page}&include_subgroups=true&simple=true";
            var response = await _http.GetStringAsync(url, ct).ConfigureAwait(false);

            var projects = JsonSerializer.Deserialize<GitLabProject[]>(response, _jsonOpts) ?? [];

            if (projects.Length == 0)
            {
                break;
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
