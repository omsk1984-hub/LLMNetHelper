namespace LLMNetHelper.Context;

/// <summary>
/// Параметры слоя контекста.
/// </summary>
public sealed class ContextOptions
{
    /// <summary>Секция конфигурации.</summary>
    public const string SectionName = "Context";

    /// <summary>Включён ли сбор внешнего контекста.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Сколько документов запрашивать у каждого источника.</summary>
    public int TopK { get; set; } = 8;

    /// <summary>Максимальная длина одного документа в символах.</summary>
    public int MaxChars { get; set; } = 6000;

    /// <summary>Общий бюджет символов на весь контекст.</summary>
    public int TotalCharsBudget { get; set; } = 24000;
}

/// <summary>
/// Параметры файлового источника контекста.
/// </summary>
public sealed class LocalFolderOptions
{
    /// <summary>Секция конфигурации.</summary>
    public const string SectionName = "Context:Sources:local-folder";

    /// <summary>Корневой каталог. Относительный путь разрешается от текущего каталога процесса.</summary>
    public string Path { get; set; } = "docs";

    /// <summary>Glob-шаблоны включаемых файлов.</summary>
    public string[] Include { get; set; } = ["**/*.md"];

    /// <summary>
    /// Glob-шаблоны исключений. По умолчанию исключаются артефакты сборки,
    /// зависимости и файлы с секретами.
    /// </summary>
    public string[] Exclude { get; set; } =
    [
        "**/bin/**",
        "**/obj/**",
        "**/node_modules/**",
        "**/.git/**",
        "**/.env",
        "**/.env.*",
        "**/*.key",
        "**/*.pfx",
        "**/secrets*"
    ];

    /// <summary>Максимальное число файлов, просматриваемых за один поиск.</summary>
    public int MaxFiles { get; set; } = 2000;

    /// <summary>Максимальный размер файла в байтах; более крупные пропускаются.</summary>
    public long MaxFileBytes { get; set; } = 512 * 1024;

    /// <summary>Размер чанка в символах при разбиении длинных файлов.</summary>
    public int ChunkSize { get; set; } = 3000;

    /// <summary>Перекрытие соседних чанков в символах.</summary>
    public int ChunkOverlap { get; set; } = 300;

    /// <summary>
    /// Корневой каталог относительно рабочего каталога процесса.
    /// Возвращает абсолютный путь.
    /// </summary>
    public string ResolveRoot()
    {
        return System.IO.Path.GetFullPath(Path);
    }
}
