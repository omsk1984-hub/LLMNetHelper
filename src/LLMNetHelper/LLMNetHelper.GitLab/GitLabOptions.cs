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

    /// <summary>
    /// Иключать ли архивные проекты группы (по умолчанию <c>true</c>).
    /// <para>
    /// GitLab по умолчанию возвращает архивные проекты наравне с боевыми —
    /// в группе <c>GPM/backend</c> их 43 из 103. Фильтрация делается серверным
    /// параметром <c>archived=false</c>: документированный для GitLab
    /// <c>exclude_archived=true</c> на проверенной версии (19.2.9)
    /// <b>игнорируется</b> и ничего не исключает.
    /// </para>
    /// </summary>
    public bool ExcludeArchived { get; set; } = true;

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

    /// <summary>
    /// Разрешить длинные пути (> 260 символов) при checkout
    /// (<c>core.longpaths=true</c> для процессов git). По умолчанию <c>true</c>:
    /// в реальных группах встречаются репозитории с глубоко вложенными файлами,
    /// без флага клон завершается с «File name too long».
    /// </summary>
    public bool AllowLongPaths { get; set; } = true;

    /// <summary>
    /// Допустимые протоколы TLS для GitLab API (например <c>"Tls12"</c> или
    /// <c>"Tls12, Tls13"</c>). Пусто — системный выбор по умолчанию.
    /// <para>
    /// Назначение: на рабочих станциях с Windows-прокси/SChannel рукопожатие
    /// TLS 1.3 может падать с <c>Win32Exception 0x80004005</c> («Не удается
    /// установить связь с локальной системой безопасности») — тогда помогают
    /// только более низкие протоколы.
    /// </para>
    /// </summary>
    public string TlsProtocols { get; set; } = "";
}
