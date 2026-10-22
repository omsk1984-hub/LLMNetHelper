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
public sealed class GitService
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
    /// Diff текущих изменений (для отчёта).
    /// </summary>
    public async Task<string> GetDiffAsync(CancellationToken ct = default)
    {
        var result = await RunGitAsync(["diff", "HEAD"], ct).ConfigureAwait(false);
        return result.Succeeded ? result.StdOut : "";
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
