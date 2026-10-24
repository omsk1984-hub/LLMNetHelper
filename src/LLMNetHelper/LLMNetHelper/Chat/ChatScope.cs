using LLMNetHelper.Abstractions;
using LLMNetHelper.Context;
using LLMNetHelper.GitLab;
using LLMNetHelper.Infrastructure.Context;
using LLMNetHelper.Roslyn;

namespace LLMNetHelper.Chat;

/// <summary>
/// Область чата: текущий корень поиска внутри <c>GitLab:CloneRoot</c>.
/// <para>
/// По умолчанию — все репозитории (<c>CloneRoot</c>); команда <c>/repo <имя></c>
/// сужает область до <c>CloneRoot/<имя></c>. Источник контекста строится
/// заново под каждую область, поэтому глобальный DI-состав хоста не меняется.
/// </para>
/// <para>
/// <see cref="TryResolveInside"/> — guard путей: инструменты чтения/листинга
/// физически не могут выйти за пределы текущей области.
/// </para>
/// </summary>
public sealed class ChatScope
{
    private readonly ChatOptions _options;
    private string? _currentRepo;
    private ResolutionResult? _solutionCache;

    public ChatScope(GitLabOptions gitLab, ChatOptions options)
    {
        ArgumentNullException.ThrowIfNull(gitLab);
        ArgumentNullException.ThrowIfNull(options);

        CloneRoot = Path.GetFullPath(gitLab.CloneRoot);
        _options = options;
    }

    /// <summary>Абсолютный корень клонирования.</summary>
    public string CloneRoot { get; }

    /// <summary>Имя выбранного репозитория или <c>null</c> — все репозитории.</summary>
    public string? CurrentRepo => _currentRepo;

    /// <summary>Абсолютный корень текущей области поиска.</summary>
    public string RootPath =>
        _currentRepo is null ? CloneRoot : Path.Combine(CloneRoot, _currentRepo);

    /// <summary>Список репозиториев — непосредственные подкаталоги CloneRoot.</summary>
    public IReadOnlyList<string> ListRepos()
    {
        if (!Directory.Exists(CloneRoot))
        {
            return [];
        }

        var names = new List<string>();
        foreach (var dir in Directory.GetDirectories(CloneRoot))
        {
            var name = Path.GetFileName(dir);
            if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith('.'))
            {
                names.Add(name);
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// Сужает область до репозитория <paramref name="name"/>.
    /// Пустое имя — сброс на все репозитории.
    /// </summary>
    /// <returns>true — область установлена; false — репозиторий не найден.</returns>
    public bool TrySetRepo(string? name, out string message)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            _currentRepo = null;
            _solutionCache = null;
            message = $"Область: все репозитории ({CloneRoot}).";
            return true;
        }

        var candidate = Path.Combine(CloneRoot, name.Trim());
        if (!Directory.Exists(candidate))
        {
            message = $"Репозиторий не найден: {name} (проверьте список: /repos).";
            return false;
        }

        _currentRepo = name.Trim();
        _solutionCache = null;
        message = $"Область: {_currentRepo} ({candidate}).";
        return true;
    }

    /// <summary>Сброс области на все репозитории CloneRoot.</summary>
    public void Reset()
    {
        _currentRepo = null;
        _solutionCache = null;
    }

    /// <summary>
    /// Файл решения текущего репозитория (<c>*.slnx</c> → <c>*.sln</c> → единственный
    /// <c>*.csproj</c>, глубина ≤ 2). <c>false</c> — область не сужена до репозитория
    /// (Roslyn в чате работает только с <c>/repo</c>) или решение не найдено.
    /// Результат кэшируется до смены области.
    /// </summary>
    public bool TryGetSolutionPath(out string solutionPath)
    {
        solutionPath = "";
        if (_currentRepo is null)
        {
            return false;
        }

        _solutionCache ??= SolutionResolver.Resolve(RootPath);
        if (!_solutionCache.Found)
        {
            return false;
        }

        solutionPath = _solutionCache.SolutionPath;
        return true;
    }

    /// <summary>
    /// BM25-поиск по файлам текущей области (переиспользует
    /// <see cref="LocalFolderContextSource"/> + <see cref="ContextAggregator"/>).
    /// </summary>
    public async Task<ContextSearchResult> SearchAsync(string text, CancellationToken ct = default)
    {
        var source = new LocalFolderContextSource(BuildFolderOptions(), BuildContextOptions());
        var aggregator = new ContextAggregator([source], BuildContextOptions());
        return await aggregator
            .SearchAsync(new ContextQuery(text, _options.TopK, _options.MaxChars), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Guard путей: разрешает относительный (или абсолютный) путь и проверяет,
    /// что он лежит внутри текущей области. Наружу — никогда.
    /// </summary>
    public bool TryResolveInside(string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var root = Path.GetFullPath(RootPath);
        string candidate;
        try
        {
            candidate = Path.IsPathRooted(relativePath)
                ? Path.GetFullPath(relativePath)
                : Path.GetFullPath(Path.Combine(root, relativePath));
        }
        catch (ArgumentException)
        {
            // Недопустимые символы пути — отказ, а не исключение наружу.
            return false;
        }

        var prefix = root[^1] == Path.DirectorySeparatorChar
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    /// <summary>
    /// Список файлов области (или её подкаталога) через <see cref="FileWalker"/>:
    /// Include/Exclude наследуются конфигурации чата.
    /// </summary>
    public IReadOnlyList<WalkedFile> ListFiles(string? subPath, int limit, CancellationToken ct = default)
    {
        var folder = BuildFolderOptions(subPath);
        var walker = new FileWalker(folder);
        var files = walker.Walk(ct);
        return limit > 0 && files.Count > limit ? files.Take(limit).ToArray() : files;
    }

    private LocalFolderOptions BuildFolderOptions(string? subPath = null)
    {
        var path = subPath is null ? RootPath : Path.Combine(RootPath, subPath);
        return new LocalFolderOptions
        {
            Path = Path.GetFullPath(path),
            Include = _options.Include,
            MaxFiles = _options.MaxFiles,
            MaxFileBytes = _options.MaxFileBytes,
            ChunkSize = _options.ChunkSize,
            ChunkOverlap = _options.ChunkOverlap
        };
    }

    private ContextOptions BuildContextOptions() => new()
    {
        Enabled = true,
        TopK = _options.TopK,
        MaxChars = _options.MaxChars,
        TotalCharsBudget = _options.TotalCharsBudget
    };
}
