using LLMNetHelper.GitLab;
using Xunit;

namespace LLMNetHelper.Tests.GitLab;

/// <summary>
/// Тесты GitLab-инструментов (план 22, этап 6.7): покрывают риск-хотспоты
/// <c>GitLabListReposTool.InvokeAsync</c> (CC=11), <c>GitLabCloneAllTool.InvokeAsync</c>
/// (CC=12), <c>GitLabUpdateRepoTool.InvokeAsync</c> (CC=11) — ветки разбора
/// аргументов, валидации и обработки исключений.
/// <para>
/// Сеть и git CLI не используются: <see cref="GitLabService"/> построен на
/// фейковом <see cref="IGitLabApiClient"/> (абстракция этапа 2.3). Успешные
/// ветки клонирования/обновления здесь намеренно не проверяются — они запускают
/// git и покрыты интеграционным тестом <c>GitLabIntegrationTests</c> (трейт
/// Category=Integration, по умолчанию Skipped).
/// </para>
/// </summary>
public class GitLabToolTests
{
    private static GitLabProject Project(string path, string branch = "main", bool archived = false) =>
        new(1, path.Split('/')[^1], path, $"https://git.test/{path}.git", null, branch, "2026-01-01T00:00:00Z", 0, 0, archived);

    private static (GitLabService Service, GitLabOptions Options) CreateService(
        bool enabled = true,
        int groupId = 42,
        IReadOnlyList<GitLabProject>? repos = null)
    {
        var options = new GitLabOptions
        {
            Enabled = enabled,
            GroupId = groupId,
            CloneRoot = Path.Combine(Path.GetTempPath(), "gitlab-tool-tests-" + Guid.NewGuid().ToString("N"))
        };
        var api = new FakeApi(repos ?? []);
        return (new GitLabService(api, options), options);
    }

    // ---------- gitlab_list_repos ----------

    [Fact]
    public async Task ListRepos_ValidGroup_RendersEachProject()
    {
        var (service, options) = CreateService(repos:
        [
            Project("GPM/backend-core-api"),
            Project("GPM/frontend-web", branch: "develop")
        ]);
        var tool = new GitLabListReposTool(service, options);

        var text = await tool.InvokeAsync("""{"group_id":42}""");

        Assert.Contains("Найдено репозиториев: 2", text, StringComparison.Ordinal);
        Assert.Contains("- GPM/backend-core-api", text, StringComparison.Ordinal);
        Assert.Contains("Ветка: main", text, StringComparison.Ordinal);
        Assert.Contains("- GPM/frontend-web", text, StringComparison.Ordinal);
        Assert.Contains("Ветка: develop", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListRepos_EmptyGroup_ReportsNoneFound()
    {
        var (service, options) = CreateService(repos: []);
        var tool = new GitLabListReposTool(service, options);

        var text = await tool.InvokeAsync("{}");

        Assert.Contains("репозиториев не найдено", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListRepos_BrokenJson_ReturnsParseError()
    {
        var (service, options) = CreateService();
        var tool = new GitLabListReposTool(service, options);

        var text = await tool.InvokeAsync("{ not json");

        Assert.StartsWith("Ошибка разбора аргументов:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListRepos_NonPositiveGroupId_Rejected()
    {
        var (service, options) = CreateService();
        var tool = new GitLabListReposTool(service, options);

        var text = await tool.InvokeAsync("""{"group_id":-5}""");

        Assert.Contains("Неверный group_id: -5", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListRepos_Disabled_ReportsGitLabDisabled()
    {
        var (service, options) = CreateService(enabled: false);
        var tool = new GitLabListReposTool(service, options);

        var text = await tool.InvokeAsync("{}");

        Assert.StartsWith("GitLab отключён:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListRepos_GroupIdNotConfigured_RejectedBeforeServiceCall()
    {
        // Enabled=true, но GroupId=0 и GroupName пуст: инструмент валидирует
        // group_id ДО обращения к сервису, поэтому до ResolveGroupIdAsync
        // (с его сообщением «неизвестно, какую группу клонировать») дело не доходит.
        var (service, options) = CreateService(groupId: 0);
        var tool = new GitLabListReposTool(service, options);

        var text = await tool.InvokeAsync("{}");

        Assert.Contains("Неверный group_id: 0", text, StringComparison.Ordinal);
    }

    // ---------- gitlab_clone_all ----------

    [Fact]
    public async Task CloneAll_BrokenJson_ReturnsParseError()
    {
        var (service, _) = CreateService();
        var tool = new GitLabCloneAllTool(service);

        var text = await tool.InvokeAsync("{ broken");

        Assert.StartsWith("Ошибка разбора аргументов:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloneAll_Disabled_ReportsGitLabDisabled()
    {
        var (service, _) = CreateService(enabled: false);
        var tool = new GitLabCloneAllTool(service);

        var text = await tool.InvokeAsync("{}");

        Assert.StartsWith("GitLab отключён:", text, StringComparison.Ordinal);
    }

    // ---------- gitlab_update_repo ----------

    [Fact]
    public async Task UpdateRepo_BrokenJson_ReturnsParseError()
    {
        var (service, _) = CreateService();
        var tool = new GitLabUpdateRepoTool(service);

        var text = await tool.InvokeAsync("nope{");

        Assert.StartsWith("Ошибка разбора аргументов:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateRepo_MissingRepoName_AskedForIt()
    {
        var (service, _) = CreateService();
        var tool = new GitLabUpdateRepoTool(service);

        var text = await tool.InvokeAsync("{}");

        Assert.Equal("Укажите repo_name.", text.Trim());
    }

    [Fact]
    public async Task UpdateRepo_Disabled_ReportsGitLabDisabled()
    {
        var (service, _) = CreateService(enabled: false);
        var tool = new GitLabUpdateRepoTool(service);

        var text = await tool.InvokeAsync("""{"repo_name":"core-api"}""");

        Assert.StartsWith("GitLab отключён:", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Фейковый API-клиент: отдаёт заготовленный список проектов группы.
    /// Остальные методы контракта не используются в этих сценариях.
    /// </summary>
    private sealed class FakeApi(IReadOnlyList<GitLabProject> repos) : IGitLabApiClient
    {
        public Task<GitLabUser> GetCurrentUserAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<GitLabGroup?> FindGroupByPathAsync(string fullPath, CancellationToken ct = default) =>
            Task.FromResult<GitLabGroup?>(null);

        public Task<IReadOnlyList<GitLabProject>> GetGroupReposAsync(int groupId, CancellationToken ct = default) =>
            Task.FromResult(repos);
    }
}
