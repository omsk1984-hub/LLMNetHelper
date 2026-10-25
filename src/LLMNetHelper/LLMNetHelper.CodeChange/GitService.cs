namespace LLMNetHelper.CodeChange;

/// <summary>
/// Работа с git через CLI-вызовы.
/// <para>
/// Сознательно не используется LibGit2Sharp: пакета нет в офлайн-кэше,
/// а CLI ведёт себя ровно так же, как у разработчика, и не требует
/// нативной библиотеки.
/// </para>
/// <para>
/// <b>Push не выполняется.</b> Агент создаёт только локальный коммит в отдельной
/// ветке; мерж и отправка — вручную.
/// </para>
/// </summary>
public sealed class GitService : IGitService
{
    private const int GitTimeoutSeconds = 120;

    private readonly CodeChangeOptions _options;

    public GitService(CodeChangeOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Проверяет, что каталог существует и является git-репозиторием.
    /// </summary>
    public async Task<bool> IsRepositoryAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.RepositoryPath) ||
            !Directory.Exists(_options.RepositoryPath))
        {
            return false;
        }

        var result = await RunGitAsync(["rev-parse", "--is-inside-work-tree"], ct).ConfigureAwait(false);
        return result.Succeeded &&
               result.StdOut.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Рабочее дерево чистое (нет незакоммиченных изменений).
    /// </summary>
    public async Task<bool> IsWorktreeCleanAsync(CancellationToken ct = default)
    {
        var result = await RunGitAsync(["status", "--porcelain"], ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(result.StdOut);
    }

    /// <summary>
    /// Текущая ветка.
    /// </summary>
    public async Task<string?> GetCurrentBranchAsync(CancellationToken ct = default)
    {
        var result = await RunGitAsync(["rev-parse", "--abbrev-ref", "HEAD"], ct).ConfigureAwait(false);
        return result.Succeeded ? result.StdOut.Trim() : null;
    }

    /// <summary>
    /// Создаёт и переключается на новую ветку.
    /// </summary>
    public async Task<ProcessResult> CreateBranchAsync(
        string branchName,
        CancellationToken ct = default)
    {
        return await RunGitAsync(["checkout", "-b", branchName], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Переключается на существующую ветку.
    /// </summary>
    public async Task<ProcessResult> CheckoutAsync(
        string branchName,
        CancellationToken ct = default)
    {
        return await RunGitAsync(["checkout", branchName], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Удаляет ветку (используется при откате).
    /// </summary>
    public async Task<ProcessResult> DeleteBranchAsync(
        string branchName,
        CancellationToken ct = default)
    {
        return await RunGitAsync(["branch", "-D", branchName], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Откатывает все изменения рабочего дерева.
    /// </summary>
    public async Task<ProcessResult> ResetHardAsync(CancellationToken ct = default)
    {
        return await RunGitAsync(["reset", "--hard"], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Добавляет все изменения в индекс и создаёт коммит.
    /// </summary>
    public async Task<ProcessResult> CommitAllAsync(
        string message,
        CancellationToken ct = default)
    {
        var add = await RunGitAsync(["add", "-A"], ct).ConfigureAwait(false);
        if (!add.Succeeded)
        {
            return add;
        }

        return await RunGitAsync(["commit", "-m", message], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// SHA текущего коммита.
    /// </summary>
    public async Task<string?> GetHeadShaAsync(CancellationToken ct = default)
    {
        var result = await RunGitAsync(["rev-parse", "HEAD"], ct).ConfigureAwait(false);
        return result.Succeeded ? result.StdOut.Trim() : null;
    }

    /// <summary>
    /// Список изменённых/untracked файлов (porcelain формат).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetDirtyFilesAsync(CancellationToken ct = default)
    {
        var result = await RunGitAsync(["status", "--porcelain"], ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return [];
        }

        return result.StdOut
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Length > 3 ? line.Substring(3).Trim() : line)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Количество коммитов, которые HEAD имеет выше origin/&lt;branch&gt;.
    /// Возвращает 0, если origin не настроен или ветки нет.
    /// </summary>
    public async Task<int> CountAheadAsync(string branch, CancellationToken ct = default)
    {
        var result = await RunGitAsync(
            ["log", "--oneline", $"HEAD..{branch}"], ct).ConfigureAwait(false);

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return 0;
        }

        return result.StdOut.Split(
            new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>
    /// Diff текущих изменений (для отчёта).
    /// </summary>
    public async Task<string> GetDiffAsync(CancellationToken ct = default)
    {
        var result = await RunGitAsync(["diff", "HEAD"], ct).ConfigureAwait(false);
        return result.Succeeded ? result.StdOut : "";
    }

    /// <summary>
    /// Получает обновления из всех удалённых репозиториев.
    /// <para>
    /// Возвращает <c>false</c>, если remote не настроен — это не ошибка,
    /// а нормальное состояние для локального репозитория.
    /// </para>
    /// </summary>
    public async Task<bool> FetchAsync(CancellationToken ct = default)
    {
        // 1. Проверяем, есть ли remotes — если нет, сразу false.
        var remotes = await RunGitAsync(["remote"], ct).ConfigureAwait(false);
        if (remotes.Succeeded && string.IsNullOrWhiteSpace(remotes.StdOut.Trim()))
        {
            return false;
        }

        var result = await RunGitAsync(
            ["fetch", "--all", "--tags", "--prune"], ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // Unable to access / could not resolve host — remote есть, но недоступен.
            var err = result.StdErr.ToLowerInvariant();
            if (err.Contains("unable to access") ||
                err.Contains("could not resolve host"))
            {
                return false;
            }

            throw new InvalidOperationException(
                $"git fetch failed: {result.Describe()}");
        }

        return true;
    }

    /// <summary>
    /// Жёстко переключает рабочую директорию на remote-ветку.
    /// <para>
    /// 1. Выполняет <c>git reset --hard origin/&lt;текущая ветка&gt;</c>.
    /// 2. Выполняет <c>git clean -fd</c> для удаления untracked-файлов.
    /// </para>
    /// <para>
    /// Требует, чтобы fetch уже был выполнен. Если remote-ветка с тем же
    /// именем не найдена — фоллбэк на <c>origin/main</c>.
    /// </para>
    /// </summary>
    public async Task<ProcessResult> PullHardAsync(CancellationToken ct = default)
    {
        var currentBranch = await GetCurrentBranchAsync(ct).ConfigureAwait(false);
        var remoteBranch = currentBranch is not null
            ? $"origin/{currentBranch}"
            : "origin/main";

        // 1. Сбросить всё к origin/<текущая ветка>
        var reset = await RunGitAsync(
            ["reset", "--hard", remoteBranch], ct).ConfigureAwait(false);

        if (!reset.Succeeded)
        {
            // remote-ветка может не совпадать по имени — пробуем main
            if (!string.Equals(remoteBranch, "origin/main", StringComparison.Ordinal))
            {
                reset = await RunGitAsync(
                    ["reset", "--hard", "origin/main"], ct).ConfigureAwait(false);
            }
        }

        if (!reset.Succeeded)
        {
            return reset;
        }

        // 2. Clean -fd (удалить untracked + ignored)
        var clean = await RunGitAsync(["clean", "-fd"], ct).ConfigureAwait(false);
        if (!clean.Succeeded)
        {
            return clean;
        }

        return reset;
    }

    /// <summary>
    /// Нормализует имя ветки: только допустимые символы.
    /// </summary>
    public string BuildBranchName(long runId)
    {
        var suffix = $"{runId}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        return _options.BranchPrefix + suffix;
    }

    private Task<ProcessResult> RunGitAsync(string[] args, CancellationToken ct)
    {
        return ProcessRunner.RunAsync(
            _options.GitExecutable,
            args,
            _options.RepositoryPath,
            GitTimeoutSeconds,
            ct);
    }
}
