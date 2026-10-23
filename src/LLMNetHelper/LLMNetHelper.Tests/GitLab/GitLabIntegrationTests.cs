using LLMNetHelper.GitLab;

namespace LLMNetHelper.Tests.GitLab;

/// <summary>
/// Интеграционные тесты GitLab-интеграции.
/// <para>
/// Проверяют <b>реально</b>: валидность токена (<c>GET /user</c>), доступность
/// группы по пути (<c>GET /groups?search=</c>) и получение списка проектов
/// группы (<c>GET /groups/{id}/projects</c>).
/// </para>
/// <para>
/// Токен берётся из переменной окружения <c>GITLAB__TOKEN</c>, а при её
/// отсутствии — из файла <c>src/LLMNetHelper/.env</c> (тот же источник, что у
/// раннера <c>run_LLMNetHelper.ps1</c>).
/// </para>
/// <para>
/// Сетевые проверки выполняются <b>только</b> при <c>GITLAB__INTEGRATION=1</c>;
/// иначе каждый тест имеет статус <c>Skipped</c> (а не Failed) — это сохраняет
/// требование «все тесты офлайн» (.roo/rules-code/01-test.md).
/// </para>
/// </summary>
public sealed class GitLabIntegrationTests
{
    /// <summary>
    /// Токен читается из окружения / .env и попадает в <see cref="GitLabOptions"/>.
    /// </summary>
    [GitLabIntegrationFact]
    [Trait("Category", "Integration")]
    public void Token_LoadedFromEnv()
    {
        var options = GitLabIntegrationEnv.RequireOptions();

        Assert.False(string.IsNullOrWhiteSpace(options.Token));
        Assert.True(options.Enabled);
        Assert.StartsWith("http", options.ApiUrl, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(options.GroupName));
    }

    /// <summary>
    /// Реальная проверка токена: <c>GET /user</c> отдаёт 200 и профиль
    /// пользователя. Невалидный/просроченный токен даёт 401 и
    /// <see cref="HttpRequestException"/> — тест упадёт с понятным текстом.
    /// </summary>
    [GitLabIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task GetCurrentUser_TokenIsValid()
    {
        using var fixture = new GitLabFixture();

        var user = await fixture.Client.GetCurrentUserAsync();

        Assert.True(user.Id > 0, $"GitLab вернул пользователя с Id={user.Id}.");
        Assert.False(string.IsNullOrWhiteSpace(user.Username), "GitLab вернул пустой username.");
    }

    /// <summary>
    /// Резолв группы по полному пути (по умолчанию <c>GPM/backend</c>):
    /// токен имеет право читать группу, и группа существует.
    /// </summary>
    [GitLabIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task FindGroupByPath_Resolved()
    {
        using var fixture = new GitLabFixture();

        var group = await fixture.Client.FindGroupByPathAsync(fixture.Options.GroupName);

        Assert.NotNull(group);
        Assert.True(group!.Id > 0, $"Группа '{fixture.Options.GroupName}' вернулась с Id={group.Id}.");
        Assert.Equal(fixture.Options.GroupName, group.FullPath);
    }

    /// <summary>
    /// Список проектов группы возвращается и относится к искомой группе
    /// (включая подгруппы — запрос идёт с <c>include_subgroups=true</c>).
    /// </summary>
    [GitLabIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task GetGroupRepos_ReturnsProjectsOfGroup()
    {
        using var fixture = new GitLabFixture();

        var group = await fixture.Client.FindGroupByPathAsync(fixture.Options.GroupName);
        Assert.NotNull(group);

        var repos = await fixture.Client.GetGroupReposAsync(group!.Id);

        Assert.NotNull(repos);

        foreach (var repo in repos)
        {
            Assert.StartsWith(group.FullPath, repo.PathWithNamespace, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(repo.HttpUrlToRepo));
        }
    }

    /// <summary>
    /// Архивные проекты группы исключаются (<see cref="GitLabOptions.ExcludeArchived"/>
    /// по умолчанию <c>true</c>): GitLab отдаёт их наравне с боевыми, и без фильтрации
    /// они попадают в локальные клоны.
    /// </summary>
    [GitLabIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task GetGroupRepos_ExcludesArchivedByDefault()
    {
        using var fixture = new GitLabFixture();

        var group = await fixture.Client.FindGroupByPathAsync(fixture.Options.GroupName);
        Assert.NotNull(group);

        Assert.True(fixture.Options.ExcludeArchived);

        var repos = await fixture.Client.GetGroupReposAsync(group!.Id);

        Assert.NotEmpty(repos);
        Assert.DoesNotContain(repos, static p => p.Archived);
    }

    /// <summary>
    /// При <see cref="GitLabOptions.ExcludeArchived"/>=<c>false</c> архивные проекты
    /// возвращаются — фильтр отключается осознанно, а не навсегда.
    /// </summary>
    [GitLabIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task GetGroupRepos_IncludesArchivedWhenFilterDisabled()
    {
        using var fixture = new GitLabFixture(excludeArchived: false);

        var group = await fixture.Client.FindGroupByPathAsync(fixture.Options.GroupName);
        Assert.NotNull(group);

        var repos = await fixture.Client.GetGroupReposAsync(group!.Id);

        Assert.Contains(repos, static p => p.Archived);
    }
}

/// <summary>
/// Разрешение конфигурации интеграционных тестов GitLab: гейт по переменной
/// окружения, токен из окружения или <c>.env</c>, параметры API/группы.
/// </summary>
internal static class GitLabIntegrationEnv
{
    /// <summary>Переменная-гейт: только при <c>1</c>/<c>true</c> тесты выполняются.</summary>
    public const string GateVariable = "GITLAB__INTEGRATION";

    /// <summary>Токен GitLab (секрет, берётся из <c>.env</c>).</summary>
    public const string TokenVariable = "GITLAB__TOKEN";

    /// <summary>Необязательное переопределение базового URL API.</summary>
    public const string ApiUrlVariable = "GITLAB__APIURL";

    /// <summary>Необязательное переопределение полного пути группы.</summary>
    public const string GroupNameVariable = "GITLAB__GROUPNAME";

    /// <summary>Необязательный явный путь к <c>.env</c>.</summary>
    public const string EnvFileVariable = "GITLAB__ENVFILE";

    public const string DefaultApiUrl = "https://gitlab.intellt.ru/api/v4";
    public const string DefaultGroupName = "GPM/backend";

    private static readonly Lazy<State> _state = new(Resolve);

    /// <summary>Причина пропуска теста либо <c>null</c>, если тест можно выполнять.</summary>
    public static string? SkipReason => _state.Value.SkipReason;

    /// <summary>Разрешённые параметры (доступны только при отсутствии причины пропуска).</summary>
    public static GitLabOptions RequireOptions()
    {
        var state = _state.Value;

        if (state.SkipReason != null || state.Options == null)
        {
            throw new InvalidOperationException(
                $"Конфигурация GitLab недоступна: {state.SkipReason ?? "неизвестная причина"}.");
        }

        return state.Options;
    }

    private static State Resolve()
    {
        if (!IsGateEnabled())
        {
            return new State(
                $"Интеграционные тесты GitLab выключены: установите ${GateVariable}='1' " +
                "(например: $env:GITLAB__INTEGRATION='1'; dotnet test ... --filter Category=Integration).",
                null);
        }

        var token = ReadToken();

        if (string.IsNullOrWhiteSpace(token))
        {
            return new State(
                $"Токен не найден: задайте {TokenVariable} в окружении или в файле " +
                "src/LLMNetHelper/.env.",
                null);
        }

        var options = new GitLabOptions
        {
            Enabled = true,
            ApiUrl = ReadSetting(ApiUrlVariable, DefaultApiUrl),
            GroupName = ReadSetting(GroupNameVariable, DefaultGroupName),
            // ID группы намеренно не задан: он резолвится через API по GroupName.
            GroupId = 0,
            Token = token,
            CloneRoot = "repos/GPM",
            CloneTimeoutSeconds = 300,
            UpdateTimeoutSeconds = 120,
            CloneDepth = 1,
            GitExecutable = "git"
        };

        return new State(null, options);
    }

    private static bool IsGateEnabled()
    {
        var raw = Environment.GetEnvironmentVariable(GateVariable);

        return string.Equals(raw?.Trim(), "1", StringComparison.Ordinal)
            || string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadSetting(string variable, string fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variable);

        return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();
    }

    /// <summary>
    /// Токен: сначала из окружения (его экспортирует <c>run_LLMNetHelper.ps1</c>),
    /// иначе — из первого найденного <c>.env</c>.
    /// </summary>
    private static string? ReadToken()
    {
        var fromEnv = Environment.GetEnvironmentVariable(TokenVariable);

        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }

        foreach (var candidate in EnumerateEnvFiles())
        {
            var token = ReadValueFromFile(candidate, TokenVariable);

            if (!string.IsNullOrWhiteSpace(token))
            {
                return token!.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Каталог <c>.env</c> определяется подъёмом вверх от каталога сборки —
    /// без жёстко зашитого диска, чтобы тест оставался переносимым.
    /// </summary>
    private static IEnumerable<string> EnumerateEnvFiles()
    {
        var explicitPath = Environment.GetEnvironmentVariable(EnvFileVariable);

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return explicitPath;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        for (int level = 0; dir != null && level < 12; level++, dir = dir.Parent)
        {
            // Корень репозитория: src/LLMNetHelper/.env
            yield return Path.Combine(dir.FullName, "src", "LLMNetHelper", ".env");
            // Каталог самого проекта (вдруг тесты запускаются из него)
            yield return Path.Combine(dir.FullName, ".env");
        }
    }

    /// <summary>
    /// Примитивный парсер <c>.env</c> — тот же, что в <c>run_LLMNetHelper.ps1</c>:
    /// <c>KEY=VALUE</c>, разделение по первому <c>=</c>, кавычки обрезаются,
    /// инлайн-комментарии не поддерживаются.
    /// </summary>
    private static string? ReadValueFromFile(string path, string key)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var idx = line.IndexOf('=');

            if (idx <= 0)
            {
                continue;
            }

            var name = line[..idx].Trim();

            if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line[(idx + 1)..].Trim().Trim('"').Trim('\'');
        }

        return null;
    }

    private sealed record State(string? SkipReason, GitLabOptions? Options);
}

/// <summary>
/// Атрибут интеграционного факта GitLab: помечает тест как <c>Skipped</c>,
/// если гейт <c>GITLAB__INTEGRATION</c> не включён или токен не найден.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class GitLabIntegrationFactAttribute : FactAttribute
{
    public GitLabIntegrationFactAttribute()
    {
        var reason = GitLabIntegrationEnv.SkipReason;

        if (reason != null)
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// Экземпляр <see cref="GitLabApiClient"/> с настроенным <see cref="HttpClient"/>.
/// <para>
/// Протокол фиксируется на TLS 1.2: TLS 1.3 в локальном SChannel при рукопожатии
/// с <c>gitlab.intellt.ru</c> падает с <c>Win32Exception 0x80004005</c> («Не удается
/// установить связь с локальной системой безопасности») — это особенность хоста,
/// а не токена или API. Валидация сертификата — стандартная (системное доверие).
/// </para>
/// </summary>
internal sealed class GitLabFixture : IDisposable
{
    private readonly HttpClient _http;

    public GitLabFixture(bool excludeArchived = true)
    {
        Options = GitLabIntegrationEnv.RequireOptions();
        Options.ExcludeArchived = excludeArchived;

        var handler = new HttpClientHandler
        {
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12
        };

        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        Client = new GitLabApiClient(Options, _http);
    }

    public GitLabOptions Options { get; }

    public GitLabApiClient Client { get; }

    public void Dispose()
    {
        Client.Dispose();
        _http.Dispose();
    }
}
