namespace LLMNetHelper.GitLab;

/// <summary>
/// Параметры работы с GitLab.
/// <para>
/// По умолчанию <see cref="Enabled"/> = <c>false</c> — функциональность
/// включается осознанно через конфигурацию.
/// </para>
/// </summary>
public sealed class GitLabOptions
{
    /// <summary>Секция конфигурации.</summary>
    public const string SectionName = "GitLab";

    /// <summary>Разрешено ли использование GitLab-функциональности.</summary>
    public bool Enabled { get; set; }

    /// <summary>Базовый URL GitLab API (по умолчанию "https://gitlab.intellt.ru/api/v4").</summary>
    public string ApiUrl { get; set; } = "https://gitlab.intellt.ru/api/v4";

    /// <summary>ID группы в GitLab.</summary>
    public int GroupId { get; set; }

    /// <summary>Название группы (для логов, например "GPM/backend").</summary>
    public string GroupName { get; set; } = "";

    /// <summary>Токен для доступа к GitLab API.</summary>
    public string Token { get; set; } = "";

    /// <summary>Локальный каталог для клонирования репозиториев.</summary>
    public string CloneRoot { get; set; } = "repos/GPM";

    /// <summary>Таймаут клонирования в секундах (по умолчанию 300).</summary>
    public int CloneTimeoutSeconds { get; set; } = 300;

    /// <summary>Таймаут обновления (pull) в секундах (по умолчанию 120).</summary>
    public int UpdateTimeoutSeconds { get; set; } = 120;

    /// <summary>Глубина клонирования (по умолчанию 1 — shallow clone).</summary>
    public int CloneDepth { get; set; } = 1;

    /// <summary>Имя git-исполняемого файла.</summary>
    public string GitExecutable { get; set; } = "git";
}
