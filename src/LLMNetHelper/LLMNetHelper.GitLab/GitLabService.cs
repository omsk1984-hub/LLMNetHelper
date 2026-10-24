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
/// <param name="Note">Служебная пометка об автовосстановлении (например,
/// «клон неполон — переклонирован»). Не является ошибкой: операция могла
/// завершиться успешно, но с предупреждением.</param>
public sealed record RepoOperationResult(
    string RepoName,
    string LocalPath,
    string Action,
    bool Success,
    string? Error = null,
    long DurationMs = 0,
    string? Note = null);

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

        var state = await InspectLocalRepoAsync(localPath, ct).ConfigureAwait(false);

        if (state != LocalRepoState.Healthy)
        {
            // Отсутствует, повреждён или неполон (прерванный клон) — клонируем
            // заново; ProcessRepoAsync сам удалит битый огрызок перед клоном.
            var groupId = await ResolveGroupIdAsync(ct).ConfigureAwait(false);
            var repos = await _apiClient.GetGroupReposAsync(groupId, ct).ConfigureAwait(false);
            var repo = repos.FirstOrDefault(r => r.Name == repoName);
            if (repo == null)
            {
                return new RepoOperationResult(repoName, localPath, "failed", false, $"Репозиторий '{repoName}' не найден в группе.");
            }

            return await ProcessRepoAsync(repo, localPath, false, ct).ConfigureAwait(false);
        }

        // Здоровый репозиторий — обновляем
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

    /// <summary>
    /// Состояние локального каталога репозитория, определяемое
    /// <see cref="InspectLocalRepoAsync"/>.
    /// </summary>
    internal enum LocalRepoState
    {
        /// <summary>Каталога нет.</summary>
        Missing,

        /// <summary>Каталог есть, но это не git-репозиторий (нет валидного <c>.git</c>).</summary>
        NotARepo,

        /// <summary>
        /// <c>.git</c> есть, но нет ни одного коммита (HEAD не резолвится) —
        /// типичный след клона, прерванного по таймауту: остаётся осиротевший
        /// <c>objects/pack/tmp_pack_*</c> и пустой <c>refs/heads</c>.
        /// </summary>
        Incomplete,

        /// <summary>Полноценный репозиторий — можно обновлять (fetch + pull).</summary>
        Healthy
    }

    /// <summary>
    /// Определяет состояние локального каталога.
    /// <para>
    /// Мотивация: прежняя проверка <c>Directory.Exists</c> принимала огрызок
    /// прерванного клона за здоровый репозиторий и тащила его в
    /// <c>git fetch --all</c>, где тот гарантированно падал по
    /// <see cref="GitLabOptions.UpdateTimeoutSeconds"/> (в пустом ref'ах
    /// репозитории fetch качает полную историю). Теперь такой каталог
    /// распознаётся как <see cref="LocalRepoState.Incomplete"/> и
    /// переклонировается автоматически.
    /// </para>
    /// </summary>
    internal async Task<LocalRepoState> InspectLocalRepoAsync(string localPath, CancellationToken ct)
    {
        if (!Directory.Exists(localPath))
        {
            return LocalRepoState.Missing;
        }

        // .git должен быть каталогом: файл .git (worktree/submodule) этот код
        // не создаёт — такой каталог трактуется как повреждённый.
        if (!Directory.Exists(Path.Combine(localPath, ".git")))
        {
            return LocalRepoState.NotARepo;
        }

        var probeTimeout = Math.Clamp(_options.UpdateTimeoutSeconds, 10, 30);

        var gitDir = await RunProcessAsync(
            _options.GitExecutable, ["rev-parse", "--git-dir"], localPath, probeTimeout, ct)
            .ConfigureAwait(false);

        if (!gitDir.Succeeded)
        {
            return LocalRepoState.NotARepo;
        }

        var head = await RunProcessAsync(
            _options.GitExecutable, ["rev-parse", "--verify", "-q", "HEAD"], localPath, probeTimeout, ct)
            .ConfigureAwait(false);

        return head.Succeeded ? LocalRepoState.Healthy : LocalRepoState.Incomplete;
    }

    internal async Task<RepoOperationResult> ProcessRepoAsync(
        GitLabProject repo,
        string localPath,
        bool forceClone,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            string? note = null;

            if (forceClone)
            {
                if (Directory.Exists(localPath))
                {
                    if (!await TryDeleteRepoDirectoryAsync(localPath).ConfigureAwait(false))
                    {
                        return new RepoOperationResult(
                            repo.Name,
                            localPath,
                            "cloned",
                            false,
                            $"не удалось удалить каталог для переклонирования: {localPath}",
                            sw.ElapsedMilliseconds);
                    }

                    note = "force-clone: каталог удалён, клонируем заново";
                }
            }
            else
            {
                var state = await InspectLocalRepoAsync(localPath, ct).ConfigureAwait(false);

                if (state == LocalRepoState.Healthy)
                {
                    return await UpdateExistingRepoAsync(repo.Name, localPath, ct).ConfigureAwait(false);
                }

                if (state != LocalRepoState.Missing)
                {
                    var reason = state == LocalRepoState.Incomplete
                        ? "клон неполон (нет ни одного коммита)"
                        : "каталог не является git-репозиторием";

                    if (!await TryDeleteRepoDirectoryAsync(localPath).ConfigureAwait(false))
                    {
                        return new RepoOperationResult(
                            repo.Name,
                            localPath,
                            "cloned",
                            false,
                            $"{reason}; каталог не удаляется — удалите вручную: {localPath}",
                            sw.ElapsedMilliseconds);
                    }

                    note = reason + ", переклонируем заново";
                }
            }

            return await CloneWithRetryAsync(repo, localPath, note, sw, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new RepoOperationResult(repo.Name, localPath, "cloned", false, ex.Message, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Клонирование с одной повторной попыткой при ошибке, не связанной с
    /// таймаутом (транзиентные сетевые сбои, обрыв соединения). Перед повтором
    /// и после финальной неудачи остатки каталога удаляются, чтобы локальное
    /// хранилище после прогона содержало либо здоровые клоны, либо ничего.
    /// </summary>
    private async Task<RepoOperationResult> CloneWithRetryAsync(
        GitLabProject repo,
        string localPath,
        string? note,
        Stopwatch sw,
        CancellationToken ct)
    {
        const int MaxAttempts = 2;
        string? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            string[] depth = _options.CloneDepth > 1 ? new[] { "--depth", _options.CloneDepth.ToString() } : [];
            var args = new[] { "clone", "--branch", repo.DefaultBranch }.Concat(depth).Concat([repo.HttpUrlToRepo, localPath]).ToArray();

            var result = await RunProcessAsync(_options.GitExecutable, args, _options.CloneRoot, _options.CloneTimeoutSeconds, ct).ConfigureAwait(false);

            if (result.Succeeded)
            {
                return new RepoOperationResult(repo.Name, localPath, "cloned", true, null, sw.ElapsedMilliseconds, note);
            }

            lastError = result.Describe();

            // При таймауте повтор бессмысленен: с тем же лимитом он истечёт снова.
            if (result.TimedOut || attempt == MaxAttempts)
            {
                break;
            }

            await TryDeleteRepoDirectoryAsync(localPath).ConfigureAwait(false);
        }

        // Финальная неудача: убираем огрызок каталога, чтобы следующий запуск
        // начал с чистого состояния. Если удалить не удалось —
        // InspectLocalRepoAsync на следующем прогоне распознает его как
        // Incomplete/NotARepo и снова переклонит.
        await TryDeleteRepoDirectoryAsync(localPath).ConfigureAwait(false);

        sw.Stop();
        return new RepoOperationResult(repo.Name, localPath, "cloned", false, lastError, sw.ElapsedMilliseconds, note);
    }

    /// <summary>
    /// Рекурсивное удаление каталога репозитория с повторами: убитый по
    /// таймауту git-процесс (<c>Kill(entireProcessTree)</c>) может
    /// кратковременно удерживать файлы. Возвращает <c>true</c>, если каталога
    /// больше нет.
    /// </summary>
    private static async Task<bool> TryDeleteRepoDirectoryAsync(string localPath, int attempts = 3, int pauseMs = 500)
    {
        for (var i = 1; i <= attempts; i++)
        {
            try
            {
                if (!Directory.Exists(localPath))
                {
                    return true;
                }

                Directory.Delete(localPath, recursive: true);
                return !Directory.Exists(localPath);
            }
            catch (IOException) when (i < attempts)
            {
                await Task.Delay(pauseMs).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (i < attempts)
            {
                await Task.Delay(pauseMs).ConfigureAwait(false);
            }
        }

        return false;
    }

    private async Task<RepoOperationResult> UpdateExistingRepoAsync(string repoName, string localPath, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string? note = null;

        try
        {
            // Fetch
            var fetch = await RunProcessAsync(_options.GitExecutable, ["fetch", "--all", "--prune"], localPath, _options.UpdateTimeoutSeconds, ct).ConfigureAwait(false);

            // Осиротевшие lock-файлы: прошлый прогон мог быть убит по таймауту
            // посреди операции, и git оставил *.lock (index.lock, shallow.lock,
            // packed-refs.lock, refs/**/*.lock), который блокирует каждую
            // последующую команду. Чужих git-процессов в этом каталоге быть не
            // может (раннер убивает всё дерево; правило проекта запрещает
            // трогать чужие процессы), поэтому такие файлы безопасно удаляем
            // и повторяем fetch один раз.
            if (!fetch.Succeeded && LooksLikeLockError(fetch))
            {
                var removed = CleanStaleLockFiles(localPath);

                if (removed > 0)
                {
                    note = $"удалено осиротевших lock-файлов: {removed}";
                    fetch = await RunProcessAsync(_options.GitExecutable, ["fetch", "--all", "--prune"], localPath, _options.UpdateTimeoutSeconds, ct).ConfigureAwait(false);
                }
            }

            if (!fetch.Succeeded)
            {
                sw.Stop();
                return new RepoOperationResult(repoName, localPath, "updated", false, fetch.Describe(), sw.ElapsedMilliseconds, note);
            }

            // Pull
            var pull = await RunProcessAsync(_options.GitExecutable, ["pull", "--ff-only"], localPath, _options.UpdateTimeoutSeconds, ct).ConfigureAwait(false);

            sw.Stop();

            if (pull.Succeeded)
            {
                return new RepoOperationResult(repoName, localPath, "updated", true, null, sw.ElapsedMilliseconds, note);
            }

            var error = pull.Describe() +
                " | подсказка: ветки разошлись или есть локальные изменения — " +
                "используйте run_LLMNetHelper.ps1 -CloneAll -ForceClone либо разрешите конфликт вручную";

            return new RepoOperationResult(repoName, localPath, "updated", false, error, sw.ElapsedMilliseconds, note);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new RepoOperationResult(repoName, localPath, "updated", false, ex.Message, sw.ElapsedMilliseconds, note);
        }
    }

    /// <summary>
    /// Признак того, что команда git упала из-за lock-файла
    /// («Unable to create '.../xxx.lock': File exists»).
    /// </summary>
    private static bool LooksLikeLockError(ProcessResult result)
    {
        var text = (result.StdErr + "\n" + result.StdOut).ToLowerInvariant();
        return text.Contains(".lock") && (text.Contains("unable to create") || text.Contains("file exists"));
    }

    /// <summary>
    /// Удаляет осиротевшие <c>*.lock</c> внутри <c>.git</c> репозитория.
    /// Возвращает число реально удалённых файлов.
    /// </summary>
    internal static int CleanStaleLockFiles(string localPath)
    {
        var gitDir = Path.Combine(localPath, ".git");

        if (!Directory.Exists(gitDir))
        {
            return 0;
        }

        var removed = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(gitDir, "*.lock", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return removed;
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
