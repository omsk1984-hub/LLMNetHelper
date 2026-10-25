using System.Text;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace LLMNetHelper.CodeChange;

/// <summary>
/// Причина отказа при применении правок.
/// </summary>
public enum ChangeFailure
{
    /// <summary>Не было отказа.</summary>
    None,

    /// <summary>Применение правок отключено конфигурацией.</summary>
    Disabled,

    /// <summary>Репозиторий не найден или не является git-репозиторием.</summary>
    NoRepository,

    /// <summary>Рабочее дерево грязное, а требуется чистое.</summary>
    DirtyWorktree,

    /// <summary>Не удалось создать ветку.</summary>
    BranchFailed,

    /// <summary>Правка не применилась к AST.</summary>
    ApplyFailed,

    /// <summary>Сборка после правки не прошла.</summary>
    BuildFailed,

    /// <summary>Тесты после правки не прошли.</summary>
    TestsFailed,

    /// <summary>Не удалось сделать коммит.</summary>
    CommitFailed,

    /// <summary>Агент не предложил изменений.</summary>
    NoChanges
}

/// <summary>
/// Результат транзакции применения правок.
/// </summary>
/// <param name="Failure">Причина отказа.</param>
/// <param name="BranchName">Имя ветки (если создана).</param>
/// <param name="CommitSha">SHA коммита (если сделан).</param>
/// <param name="Message">Пояснение для отчёта.</param>
/// <param name="Diff">Накопленный diff.</param>
/// <param name="AppliedChanges">Сколько правок применено.</param>
public sealed record ChangeOutcome(
    ChangeFailure Failure,
    string? BranchName,
    string? CommitSha,
    string Message,
    string Diff,
    int AppliedChanges)
{
    /// <summary>Успешно ли завершилась транзакция.</summary>
    public bool Succeeded => Failure == ChangeFailure.None;

    /// <summary>Пустой результат «ничего не делали».</summary>
    public static ChangeOutcome Skipped(ChangeFailure reason, string message) =>
        new(reason, null, null, message, "", 0);
}

/// <summary>
/// Транзакция применения правок: ветка → правки → сборка → тесты → коммит,
/// с полным откатом на любом провале.
/// <para>
/// Инварианты:
/// <list type="bullet">
///   <item>правки идут только в отдельную ветку <c>fix/ai-{runId}</c>;</item>
///   <item>провал на любом шаге возвращает репозиторий в исходное состояние;</item>
///   <item>push не выполняется — только локальный коммит;</item>
///   <item>грязное рабочее дерево — отказ, а не «закоммитим всё подряд».</item>
/// </list>
/// </para>
/// </summary>
public sealed class ChangeTransaction
{
    private readonly CodeChangeOptions _options;
    // План 22, этап 2.1: зависимость от абстракции IGitService, а не от GitService.
    private readonly IGitService _git;
    private readonly IBuildRunner _build;
    // План 22, этап 2.2: зависимости от абстракций IAstPatcher/ICompilationValidator.
    private readonly IAstPatcher _patcher;
    private readonly ICompilationValidator _validator;
    private readonly SolutionLoader _loader;
    private readonly ILogger<ChangeTransaction>? _logger;

    public ChangeTransaction(
        CodeChangeOptions options,
        IGitService git,
        IBuildRunner build,
        IAstPatcher patcher,
        ICompilationValidator validator,
        SolutionLoader loader,
        ILogger<ChangeTransaction>? logger = null)
    {
        _options = options;
        _git = git;
        _build = build;
        _patcher = patcher;
        _validator = validator;
        _loader = loader;
        _logger = logger;
    }

    /// <summary>
    /// Применяет план исправления.
    /// </summary>
    public async Task<ChangeOutcome> ApplyAsync(
        long runId,
        FixPlan plan,
        CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return ChangeOutcome.Skipped(ChangeFailure.Disabled, "CodeChange:Enabled=false — правки не применяются");
        }

        if (plan is null || !plan.HasChanges)
        {
            return ChangeOutcome.Skipped(ChangeFailure.NoChanges, "агент не предложил изменений");
        }

        if (!await _git.IsRepositoryAsync(ct).ConfigureAwait(false))
        {
            return ChangeOutcome.Skipped(ChangeFailure.NoRepository,
                $"не git-репозиторий: {_options.RepositoryPath}");
        }

        if (_options.RequireCleanWorktree && !await _git.IsWorktreeCleanAsync(ct).ConfigureAwait(false))
        {
            var dirtyFiles = await _git.GetDirtyFilesAsync(ct).ConfigureAwait(false);
            var detail = dirtyFiles.Count > 0
                ? $"изменённые файлы ({dirtyFiles.Count}):\n" + string.Join("\n", dirtyFiles.Take(20))
                : "(porcelain-вывод пуст)";
            return ChangeOutcome.Skipped(ChangeFailure.DirtyWorktree,
                $"рабочее дерево содержит незакоммиченные изменения:\n{detail}");
        }

        var baseBranch = await _git.GetCurrentBranchAsync(ct).ConfigureAwait(false) ?? _options.BaseBranch;
        var branchName = _git.BuildBranchName(runId);

        var branchResult = await _git.CreateBranchAsync(branchName, ct).ConfigureAwait(false);
        if (!branchResult.Succeeded)
        {
            return ChangeOutcome.Skipped(ChangeFailure.BranchFailed,
                $"не удалось создать ветку {branchName}: {branchResult.Describe()}");
        }

        // Дальше любой провал откатывает ветку.
        try
        {
            var solution = await _loader.LoadAsync(ct).ConfigureAwait(false);
            if (solution is null)
            {
                return await RollbackAsync(branchName, baseBranch, ChangeFailure.ApplyFailed,
                    "не удалось загрузить решение для правок", ct).ConfigureAwait(false);
            }

            var applied = 0;

            foreach (var change in plan.Changes)
            {
                ct.ThrowIfCancellationRequested();

                var patch = await _patcher.ApplyAsync(
                    solution, change.FilePath, change.SymbolName, change.ChangeKind, change.NewCode, ct)
                    .ConfigureAwait(false);

                if (!patch.Succeeded || patch.UpdatedSolution is null)
                {
                    return await RollbackAsync(branchName, baseBranch, ChangeFailure.ApplyFailed,
                        $"правка {change.SymbolName} не применилась: {patch.Reason}", ct).ConfigureAwait(false);
                }

                solution = patch.UpdatedSolution;
                applied++;
            }

            // Проверка компиляции до записи на диск — дешёвый барьер.
            var validation = await _validator.ValidateAsync(solution, null, ct).ConfigureAwait(false);
            if (!validation.Succeeded)
            {
                var details = string.Join("; ", validation.Errors.Take(3));
                return await RollbackAsync(branchName, baseBranch, ChangeFailure.BuildFailed,
                    $"правки не компилируются: {details}", ct).ConfigureAwait(false);
            }

            // Записываем изменённые документы на диск.
            var writeResult = await WriteChangesAsync(solution, plan, ct).ConfigureAwait(false);
            if (writeResult is not null)
            {
                return await RollbackAsync(branchName, baseBranch, ChangeFailure.ApplyFailed,
                    writeResult, ct).ConfigureAwait(false);
            }

            // Сборка.
            var build = await _build.BuildAsync(ct).ConfigureAwait(false);
            if (!build.Succeeded)
            {
                return await RollbackAsync(branchName, baseBranch, ChangeFailure.BuildFailed,
                    $"сборка не прошла: {build.Describe()}", ct).ConfigureAwait(false);
            }

            // Тесты.
            var tests = await _build.TestAsync(ct).ConfigureAwait(false);
            if (!tests.Succeeded)
            {
                return await RollbackAsync(branchName, baseBranch, ChangeFailure.TestsFailed,
                    $"тесты не прошли: {tests.Describe()}", ct).ConfigureAwait(false);
            }

            var diff = await _git.GetDiffAsync(ct).ConfigureAwait(false);

            var message = BuildCommitMessage(runId, plan);
            var commit = await _git.CommitAllAsync(message, ct).ConfigureAwait(false);
            if (!commit.Succeeded)
            {
                return await RollbackAsync(branchName, baseBranch, ChangeFailure.CommitFailed,
                    $"коммит не создан: {commit.Describe()}", ct).ConfigureAwait(false);
            }

            var sha = await _git.GetHeadShaAsync(ct).ConfigureAwait(false);

            // Проверка: origin не ушёл вперёд — информационное предупреждение.
            try
            {
                var aheadCount = await _git.CountAheadAsync("origin/main", ct).ConfigureAwait(false);
                if (aheadCount > 0)
                {
                    _logger?.LogWarning(
                        "origin/main имеет {AheadCount} новых коммитов после нашего коммита {Sha}",
                        aheadCount, sha);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Не удалось проверить diff с origin (не критично)");
            }

            _loader.InvalidateCache();

            return new ChangeOutcome(
                ChangeFailure.None,
                branchName,
                sha,
                $"применено правок: {applied}; ветка {branchName}; push не выполнялся",
                diff,
                applied);
        }
        catch (OperationCanceledException)
        {
            await RollbackAsync(branchName, baseBranch, ChangeFailure.ApplyFailed,
                "операция отменена", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            return await RollbackAsync(branchName, baseBranch, ChangeFailure.ApplyFailed,
                $"непредвиденный сбой: {ex.Message}", ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Записывает изменённые документы на диск.
    /// Возвращает текст ошибки либо null при успехе.
    /// </summary>
    private static async Task<string?> WriteChangesAsync(
        Solution solution,
        FixPlan plan,
        CancellationToken ct)
    {
        var targets = plan.Changes
            .Select(static c => c.FilePath.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                ct.ThrowIfCancellationRequested();

                if (document.FilePath is null)
                {
                    continue;
                }

                var docPath = document.FilePath.Replace('\\', '/');
                var matches = targets.Any(t =>
                    docPath.Equals(t, StringComparison.OrdinalIgnoreCase) ||
                    docPath.EndsWith(t, StringComparison.OrdinalIgnoreCase));

                if (!matches)
                {
                    continue;
                }

                var text = await document.GetTextAsync(ct).ConfigureAwait(false);

                try
                {
                    await File.WriteAllTextAsync(document.FilePath, text.ToString(), ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return $"не удалось записать {document.FilePath}: {ex.Message}";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Откат: возврат на базовую ветку, удаление ветки исправления.
    /// </summary>
    private async Task<ChangeOutcome> RollbackAsync(
        string branchName,
        string baseBranch,
        ChangeFailure failure,
        string message,
        CancellationToken ct)
    {
        try
        {
            await _git.ResetHardAsync(ct).ConfigureAwait(false);
            await _git.CheckoutAsync(baseBranch, ct).ConfigureAwait(false);
            await _git.DeleteBranchAsync(branchName, ct).ConfigureAwait(false);
            _loader.InvalidateCache();
        }
        catch (Exception ex)
        {
            message += $" (откат неполный: {ex.Message})";
        }

        return new ChangeOutcome(failure, null, null, message, "", 0);
    }

    /// <summary>
    /// Сообщение коммита: без секретов, с указанием прогона.
    /// </summary>
    private static string BuildCommitMessage(long runId, FixPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"fix(ai): автоматическая правка по инциденту #{runId}");
        sb.AppendLine();
        sb.AppendLine(plan.Rationale);
        sb.AppendLine();
        sb.AppendLine("Изменения:");

        foreach (var change in plan.Changes)
        {
            // План 22, этап 1.3: enum → прежняя wire-строка в сообщении коммита.
            sb.AppendLine($"- {change.FilePath}: {change.SymbolName} ({ChangeKindFormat.ToWire(change.ChangeKind)})");
        }

        sb.AppendLine();
        sb.AppendLine("Сгенерировано ночным агентом. Требует ручной проверки перед мержем.");

        return sb.ToString();
    }
}
