using System.Diagnostics;
using System.Text;
using LLMNetHelper.CodeChange;

namespace LLMNetHelper.GitLab;

/// <summary>
/// Результат операции с одним репозиторием.
/// </summary>
/// <param name="RepoName">Имя репозитория.</param>
/// <param name="LocalPath">Локальный путь.</param>
/// <param name="Action">Выполненное действие: "cloned", "updated", "skipped", "failed".</param>
/// <param name="Success">Успешно ли.</param>
/// <param name="Error">Сообщение об ошибке (если Success = false).</param>
/// <param name="DurationMs">Длительность в мс.</param>
public sealed record RepoOperationResult(
    string RepoName,
    string LocalPath,
    string Action,
    bool Success,
    string? Error = null,
    long DurationMs = 0);

/// <summary>
/// Сервис для клонирования и обновления репозиториев GitLab.
/// <para>
/// Использует git CLI через <see cref="RunProcessAsync"/> — тот же паттерн,
/// что и в <c>LLMNetHelper.CodeChange</c>.
/// </para>
/// </summary>
public sealed class GitLabService
{
    private readonly GitLabApiClient _apiClient;
    private readonly GitLabOptions _options;
    private readonly IProgress<RepoOperationResult>? _progress;

    public GitLabService(GitLabApiClient apiClient, GitLabOptions options, IProgress<RepoOperationResult>? progress = null)
    {
        _apiClient = apiClient;
        _options = options;
        _progress = progress;
    }

    /// <summary>
    /// Разрешает ID группы: берёт <see cref="GitLabOptions.GroupId"/>, а если он
    /// не задан (0) — ищет группу по <see cref="GitLabOptions.GroupName"/> через API.
    /// </summary>
    private async Task<int> ResolveGroupIdAsync(CancellationToken ct)
    {
        if (_options.GroupId > 0)
        {
            return _options.GroupId;
        }

        if (string.IsNullOrWhiteSpace(_options.GroupName))
        {
            throw new InvalidOperationException(
                "GitLab: ни GroupId, ни GroupName не заданы — неизвестно, какую группу клонировать.");
        }

        var group = await _apiClient
            .FindGroupByPathAsync(_options.GroupName, ct)
            .ConfigureAwait(false);

        if (group == null)
        {
            throw new InvalidOperationException(
                $"Группа '{_options.GroupName}' не найдена в GitLab (проверьте GitLab:GroupName и права токена).");
        }

        return group.Id;
    }

    /// <summary>
    /// Получает список всех репозиториев группы.
    /// </summary>
    public async Task<IReadOnlyList<GitLabProject>> ListReposAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("GitLab functionality is disabled. Set GitLab:Enabled = true.");
        }

        var groupId = await ResolveGroupIdAsync(ct).ConfigureAwait(false);

        return await _apiClient.GetGroupReposAsync(groupId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Клонировать или обновить все репозитории группы.
    /// </summary>
    /// <param name="forceClone">Если true — удалить существующие и клонировать заново.</param>
    /// <param name="ct">Токен отмены.</param>
    public async Task<GitLabBatchResult> CloneAllAsync(bool forceClone = false, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("GitLab functionality is disabled.");
        }

        var groupId = await ResolveGroupIdAsync(ct).ConfigureAwait(false);
        var repos = await _apiClient.GetGroupReposAsync(groupId, ct).ConfigureAwait(false);

        if (repos.Count == 0)
        {
            return new GitLabBatchResult(0, 0, 0, 0, []);
        }

        // Создаём каталог если нужно
        Directory.CreateDirectory(_options.CloneRoot);

        var results = new List<RepoOperationResult>();
        int cloned = 0;
        int updated = 0;
        int failed = 0;

        foreach (var repo in repos)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            var localPath = Path.Combine(_options.CloneRoot, repo.Name);
            var result = await ProcessRepoAsync(repo, localPath, forceClone, ct).ConfigureAwait(false);
            results.Add(result);
            _progress?.Report(result);

            if (result.Success)
            {
                if (result.Action == "cloned") cloned++;
                else if (result.Action == "updated") updated++;
            }
            else
            {
                failed++;
            }
        }

        return new GitLabBatchResult(repos.Count, cloned, updated, failed, results);
    }

    /// <summary>
    /// Обновить конкретный репозиторий.
    /// </summary>
    public async Task<RepoOperationResult> UpdateRepoAsync(string repoName, string? localPath = null, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("GitLab functionality is disabled.");
        }

        localPath ??= Path.Combine(_options.CloneRoot, repoName);

        if (!Directory.Exists(localPath))
        {
            // Если не существует — клонируем
            var groupId = await ResolveGroupIdAsync(ct).ConfigureAwait(false);
            var repos = await _apiClient.GetGroupReposAsync(groupId, ct).ConfigureAwait(false);
            var repo = repos.FirstOrDefault(r => r.Name == repoName);
            if (repo == null)
            {
                return new RepoOperationResult(repoName, localPath, "failed", false, $"Репозиторий '{repoName}' не найден в группе.");
            }

            return await ProcessRepoAsync(repo, localPath, false, ct).ConfigureAwait(false);
        }

        // Если существует — обновляем
        return await UpdateExistingRepoAsync(repoName, localPath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Получить статус конкретного репозитория.
    /// </summary>
    public async Task<RepoStatus> GetRepoStatusAsync(string repoName, string? localPath = null, CancellationToken ct = default)
    {
        localPath ??= Path.Combine(_options.CloneRoot, repoName);

        if (!Directory.Exists(Path.Combine(localPath, ".git")))
        {
            return new RepoStatus(repoName, localPath, false, false, [], null);
        }

        var dirtyResult = await RunProcessAsync("git", ["status", "--porcelain"], localPath, _options.UpdateTimeoutSeconds, ct).ConfigureAwait(false);
        var branchResult = await RunProcessAsync("git", ["rev-parse", "--abbrev-ref", "HEAD"], localPath, _options.UpdateTimeoutSeconds, ct).ConfigureAwait(false);
        var latestResult = await RunProcessAsync("git", ["log", "-1", "--format=%H|%ci"], localPath, _options.UpdateTimeoutSeconds, ct).ConfigureAwait(false);

        var dirtyFiles = dirtyResult.Succeeded
            ? dirtyResult.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList()
            : [];

        var currentBranch = branchResult.Succeeded ? branchResult.StdOut.Trim() : null;
        var latestCommit = latestResult.Succeeded ? latestResult.StdOut.Trim() : null;

        return new RepoStatus(repoName, localPath, true, dirtyFiles.Count > 0, dirtyFiles, currentBranch, latestCommit);
    }

    private async Task<RepoOperationResult> ProcessRepoAsync(
        GitLabProject repo,
        string localPath,
        bool forceClone,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            if (Directory.Exists(localPath))
            {
                if (forceClone)
                {
                    Directory.Delete(localPath, recursive: true);
                }
                else
                {
                    return await UpdateExistingRepoAsync(repo.Name, localPath, ct).ConfigureAwait(false);
                }
            }

            // Клонирование
            string[] depth = _options.CloneDepth > 1 ? new[] { "--depth", _options.CloneDepth.ToString() } : [];
            var args = new[] { "clone", "--branch", repo.DefaultBranch }.Concat(depth).Concat([repo.HttpUrlToRepo, localPath]).ToArray();

            var result = await RunProcessAsync(_options.GitExecutable, args, _options.CloneRoot, _options.CloneTimeoutSeconds, ct).ConfigureAwait(false);

            sw.Stop();

            if (result.Succeeded)
            {
                return new RepoOperationResult(repo.Name, localPath, "cloned", true, null, sw.ElapsedMilliseconds);
            }

            return new RepoOperationResult(
                repo.Name,
                localPath,
                "cloned",
                false,
                result.Describe(),
                sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new RepoOperationResult(repo.Name, localPath, "cloned", false, ex.Message, sw.ElapsedMilliseconds);
        }
    }

    private async Task<RepoOperationResult> UpdateExistingRepoAsync(string repoName, string localPath, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // Fetch
            var fetch = await RunProcessAsync(_options.GitExecutable, ["fetch", "--all", "--prune"], localPath, _options.UpdateTimeoutSeconds, ct).ConfigureAwait(false);
            if (!fetch.Succeeded)
            {
                sw.Stop();
                return new RepoOperationResult(repoName, localPath, "updated", false, fetch.Describe(), sw.ElapsedMilliseconds);
            }

            // Pull
            var pull = await RunProcessAsync(_options.GitExecutable, ["pull", "--ff-only"], localPath, _options.UpdateTimeoutSeconds, ct).ConfigureAwait(false);

            sw.Stop();

            if (pull.Succeeded)
            {
                return new RepoOperationResult(repoName, localPath, "updated", true, null, sw.ElapsedMilliseconds);
            }

            return new RepoOperationResult(
                repoName,
                localPath,
                "updated",
                false,
                pull.Describe(),
                sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new RepoOperationResult(repoName, localPath, "updated", false, ex.Message, sw.ElapsedMilliseconds);
        }
    }

    private async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // Конфигурация git через GIT_CONFIG_* — она действует только на этот процесс
        // и НЕ попадает в .git/config клона:
        //   * http.extraHeader — аутентификация приватных репозиториев токеном
        //     (иначе клон был бы анонимным и падал на 401 / запросе логина);
        //   * core.longpaths=true — в группе есть репозитории с путями длиннее
        //     260 символов, без этого флага checkout падает с
        //     "File name too long".
        var configIndex = 0;

        if (!string.IsNullOrWhiteSpace(_options.Token))
        {
            psi.Environment[$"GIT_CONFIG_KEY_{configIndex}"] = "http.extraHeader";
            psi.Environment[$"GIT_CONFIG_VALUE_{configIndex}"] = $"PRIVATE-TOKEN: {_options.Token}";
            configIndex++;
        }

        if (_options.AllowLongPaths)
        {
            psi.Environment[$"GIT_CONFIG_KEY_{configIndex}"] = "core.longpaths";
            psi.Environment[$"GIT_CONFIG_VALUE_{configIndex}"] = "true";
            configIndex++;
        }

        if (configIndex > 0)
        {
            psi.Environment["GIT_CONFIG_COUNT"] = configIndex.ToString();
        }

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };

        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, "", $"не удалось запустить {fileName}", false);
            }
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", $"сбой запуска {fileName}: {ex.Message}", false);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var timedOut = false;

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            TryKillTree(process);
        }

        var stdout = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        var stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

        var exitCode = timedOut ? -1 : process.ExitCode;
        return new ProcessResult(exitCode, stdout, stderr, timedOut);
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Уже завершился
        }
        catch (Exception)
        {
            // Нет прав или процесс уже мёртв
        }
    }
}

/// <summary>
/// Результат пакетной операции.
/// </summary>
/// <param name="Total">Всего репозиториев.</param>
/// <param name="Cloned">Новых клонирований.</param>
/// <param name="Updated">Обновлений.</param>
/// <param name="Failed">Ошибок.</param>
/// <param name="Details">Детали по каждому репозиторию.</param>
public sealed record GitLabBatchResult(
    int Total,
    int Cloned,
    int Updated,
    int Failed,
    IReadOnlyList<RepoOperationResult> Details)
{
    public bool AllSucceeded => Failed == 0;

    public override string ToString()
    {
        return $"GitLab: {Total} repos — cloned={Cloned}, updated={Updated}, failed={Failed}";
    }
}

/// <summary>
/// Статус одного репозитория.
/// </summary>
/// <param name="RepoName">Имя репозитория.</param>
/// <param name="LocalPath">Локальный путь.</param>
/// <param name="Exists">Существует ли локально.</param>
/// <param name="IsDirty">Есть ли незакоммиченные изменения.</param>
/// <param name="DirtyFiles">Список изменённых файлов.</param>
/// <param name="CurrentBranch">Текущая ветка.</param>
/// <param name="LatestCommit">SHA последнего коммита.</param>
public sealed record RepoStatus(
    string RepoName,
    string LocalPath,
    bool Exists,
    bool IsDirty,
    IReadOnlyList<string> DirtyFiles,
    string? CurrentBranch,
    string? LatestCommit = null);
