namespace LLMNetHelper.CodeChange;

/// <summary>
/// Параметры применения правок.
/// <para>
/// По умолчанию <see cref="Enabled"/> = <c>false</c> — безопасный дефолт:
/// агент сначала работает в режиме «диагноз + отчёт», а запись в репозиторий
/// включается осознанно.
/// </para>
/// </summary>
public sealed class CodeChangeOptions
{
    /// <summary>Секция конфигурации.</summary>
    public const string SectionName = "CodeChange";

    /// <summary>Разрешено ли агенту менять код и коммитить.</summary>
    public bool Enabled { get; set; }

    /// <summary>Корень git-репозитория.</summary>
    public string RepositoryPath { get; set; } = "";

    /// <summary>Базовая ветка, от которой создаётся ветка исправления.</summary>
    public string BaseBranch { get; set; } = "main";

    /// <summary>Требовать чистое рабочее дерево перед созданием ветки.</summary>
    public bool RequireCleanWorktree { get; set; } = true;

    /// <summary>Таймаут сборки в секундах.</summary>
    public int BuildTimeoutSeconds { get; set; } = 600;

    /// <summary>Таймаут тестов в секундах.</summary>
    public int TestTimeoutSeconds { get; set; } = 900;

    /// <summary>Пропускать прогон тестов (для быстрой итерации).</summary>
    public bool SkipTests { get; set; }

    /// <summary>Путь к решению для сборки и тестов.</summary>
    public string SolutionPath { get; set; } = "";

    /// <summary>Имя git-исполняемого файла.</summary>
    public string GitExecutable { get; set; } = "git";

    /// <summary>Имя dotnet-исполняемого файла.</summary>
    public string DotnetExecutable { get; set; } = "dotnet";

    /// <summary>Префикс имени ветки исправления.</summary>
    public string BranchPrefix { get; set; } = "fix/ai-";
}
