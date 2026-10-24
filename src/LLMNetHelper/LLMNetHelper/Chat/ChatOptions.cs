namespace LLMNetHelper.Chat;

/// <summary>
/// Параметры режима чата <c>--chat</c> (секция конфигурации <c>Chat</c>).
/// <para>
/// Чат отвечает на вопросы по скачанным репозиториям (<c>GitLab:CloneRoot</c>).
/// Поиск контекста — BM25 через <c>LocalFolderContextSource</c> с собственными
/// Include-паттернами (код + документы), поэтому ночной агент и его
/// <c>Context:Sources:local-folder</c> (docs) не затрагиваются.
/// </para>
/// </summary>
public sealed class ChatOptions
{
    /// <summary>Секция конфигурации.</summary>
    public const string SectionName = "Chat";

    /// <summary>
    /// Glob-шаблоны включаемых файлов: исходники, проекты, конфиги, документы.
    /// </summary>
    public string[] Include { get; set; } =
    [
        "**/*.md", "**/*.cs", "**/*.csproj", "**/*.sln", "**/*.slnx",
        "**/*.json", "**/*.sql", "**/*.yml", "**/*.yaml", "**/*.xml",
        "**/*.txt", "**/*.py", "**/*.js", "**/*.ts"
    ];

    /// <summary>Сколько фрагментов контекста отбирать на один вопрос.</summary>
    public int TopK { get; set; } = 8;

    /// <summary>Максимальная длина одного фрагмента в символах.</summary>
    public int MaxChars { get; set; } = 6000;

    /// <summary>Общий бюджет символов контекста на один вопрос.</summary>
    public int TotalCharsBudget { get; set; } = 24000;

    /// <summary>Максимальное число файлов, просматриваемых за один поиск.</summary>
    public int MaxFiles { get; set; } = 20000;

    /// <summary>Максимальный размер файла в байтах; более крупные пропускаются.</summary>
    public long MaxFileBytes { get; set; } = 512 * 1024;

    /// <summary>Размер чанка в символах при разбиении длинных файлов.</summary>
    public int ChunkSize { get; set; } = 3000;

    /// <summary>Перекрытие соседних чанков в символах.</summary>
    public int ChunkOverlap { get; set; } = 300;

    /// <summary>Сколько сообщений истории держать в контексте диалога.</summary>
    public int HistoryMessages { get; set; } = 20;

    /// <summary>Максимум итераций ReAct-цикла в режиме <c>/tools</c>.</summary>
    public int MaxSteps { get; set; } = 8;

    /// <summary>Максимум вызовов инструментов за один вопрос в режиме <c>/tools</c>.</summary>
    public int ToolCallLimit { get; set; } = 30;

    /// <summary>Максимум файлов, возвращаемых инструментом <c>repo_list_files</c>.</summary>
    public int ListLimit { get; set; } = 200;

    /// <summary>Markdown-транскрипт диалога (подсекция <c>Chat:DialogLog</c>).</summary>
    public DialogLogOptions DialogLog { get; set; } = new();
}

/// <summary>
/// Параметры транскрипта диалога: один Markdown-файл на весь сеанс чата
/// (от запуска до <c>/exit</c>/EOF) со всеми вопросами, командами, шагами
/// ReAct-цикла (ответ модели → вызов инструмента → полный результат) и
/// финальными ответами. См. <see cref="ChatDialogLogger"/>.
/// </summary>
public sealed class DialogLogOptions
{
    /// <summary>Писать ли транскрипт (по умолчанию — да).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Каталог транскриптов. Относительный путь разрешается от корня
    /// репозитория раннером (<c>CHAT__DIALOGLOG__DIRECTORY</c>) либо от
    /// рабочего каталога процесса.
    /// </summary>
    public string Directory { get; set; } = "logs/dialogs";

    /// <summary>
    /// Писать ли в транскрипт ПОЛНЫЙ текст BM25-фрагментов контекста
    /// (режим без <c>/tools</c>) — ровно то, что видела модель.
    /// По умолчанию — да; <c>false</c> оставляет только аннотации
    /// (заголовок, score, длина, путь). Фрагменты ограничены сверху
    /// параметрами <c>Chat:TopK</c>/<c>Chat:MaxChars</c>/<c>Chat:TotalCharsBudget</c>,
    /// поэтому размер файла предсказуем.
    /// </summary>
    public bool IncludeContextFragments { get; set; } = true;
}
