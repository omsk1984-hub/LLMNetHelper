namespace LLMNetHelper.CodeChange;

/// <summary>
/// Контракт работы с git (план 22, этап 2.1). Извлечён из <see cref="GitService"/>,
/// чтобы потребители (<see cref="ChangeTransaction"/>, планировщик) зависели от
/// абстракции, а не от конкретного класса — подмена в тестах без правки потребителей.
/// <para>
/// Инварианты (наследуем от <see cref="GitService"/>): push не выполняется —
/// только локальные операции; ветки создаются в отдельной ветке <c>fix/ai-{runId}</c>.
/// </para>
/// </summary>
public interface IGitService
{
    /// <summary>Проверяет, что каталог существует и является git-репозиторием.</summary>
    Task<bool> IsRepositoryAsync(CancellationToken ct = default);

    /// <summary>Проверяет, что рабочее дерево чистое (нет незакоммиченных изменений).</summary>
    Task<bool> IsWorktreeCleanAsync(CancellationToken ct = default);

    /// <summary>Текущая ветка либо null (detached HEAD / ошибка).</summary>
    Task<string?> GetCurrentBranchAsync(CancellationToken ct = default);

    /// <summary>Создаёт ветку из текущего HEAD.</summary>
    Task<ProcessResult> CreateBranchAsync(string branchName, CancellationToken ct = default);

    /// <summary>Переключается на ветку.</summary>
    Task<ProcessResult> CheckoutAsync(string branchName, CancellationToken ct = default);

    /// <summary>Удаляет ветку (с принудительным удалением незамерджённых коммитов).</summary>
    Task<ProcessResult> DeleteBranchAsync(string branchName, CancellationToken ct = default);

    /// <summary>Сбрасывает рабочее дерево к HEAD (<c>git reset --hard</c>).</summary>
    Task<ProcessResult> ResetHardAsync(CancellationToken ct = default);

    /// <summary>Коммитит все изменения (<c>git add -A</c> + <c>git commit</c>).</summary>
    Task<ProcessResult> CommitAllAsync(string message, CancellationToken ct = default);

    /// <summary>SHA текущего коммита либо null.</summary>
    Task<string?> GetHeadShaAsync(CancellationToken ct = default);

    /// <summary>Список изменённых файлов porcelain-вывода.</summary>
    Task<IReadOnlyList<string>> GetDirtyFilesAsync(CancellationToken ct = default);

    /// <summary>Сколько коммитов ветка опережает указанную базу.</summary>
    Task<int> CountAheadAsync(string branch, CancellationToken ct = default);

    /// <summary>Накопленный diff рабочего дерева относительно базы.</summary>
    Task<string> GetDiffAsync(CancellationToken ct = default);

    /// <summary>Обновляет удалённые ссылки (<c>git fetch</c>). true — успех.</summary>
    Task<bool> FetchAsync(CancellationToken ct = default);

    /// <summary>Жёсткая синхронизация с <c>origin/main</c> (<c>git pull --hard</c>).</summary>
    Task<ProcessResult> PullHardAsync(CancellationToken ct = default);

    /// <summary>Имя ветки для прогона: <c>fix/ai-{runId}</c>.</summary>
    string BuildBranchName(long runId);
}
