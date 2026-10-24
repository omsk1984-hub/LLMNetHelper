namespace LLMNetHelper.Roslyn;

/// <summary>
/// Резолвер файла решения в каталоге репозитория: <c>*.slnx</c> → <c>*.sln</c> →
/// единственный <c>*.csproj</c>. Используется чатом в режиме <c>/repo</c>, чтобы
/// открыть решение конкретного репозитория, а не глобальный <c>Roslyn:SolutionPath</c>.
/// <para>
/// Поиск — на глубине ≤ <see cref="MaxDepth"/> от корня репозитория,
/// исключая <c>bin/obj/.git</c>. Несколько кандидатов одного приоритета —
/// берётся первый по алфавиту (детерминированно); факт неоднозначности
/// виден вызывающему через <see cref="ResolutionResult.Ambiguous"/>.
/// </para>
/// </summary>
public static class SolutionResolver
{
    /// <summary>Максимальная глубина поиска каталогов решений.</summary>
    public const int MaxDepth = 2;

    private static readonly string[] ExcludedDirs = ["bin", "obj", ".git", ".vs", "node_modules", "packages"];

    /// <summary>
    /// Ищет файл решения в <paramref name="repoRoot"/>.
    /// Возвращает <see cref="ResolutionResult.NotFound"/>, если ничего не найдено.
    /// </summary>
    public static ResolutionResult Resolve(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
        {
            return ResolutionResult.NotFound;
        }

        var candidates = new List<string>();
        Collect(repoRoot, depth: 0, candidates);
        candidates.Sort(StringComparer.OrdinalIgnoreCase);

        if (candidates.Count == 0)
        {
            return ResolutionResult.NotFound;
        }

        // Приоритет расширений: .slnx > .sln > .csproj.
        foreach (var ext in new[] { ".slnx", ".sln" })
        {
            var matched = candidates.Where(c =>
                Path.GetExtension(c).Equals(ext, StringComparison.OrdinalIgnoreCase)).ToArray();

            if (matched.Length > 0)
            {
                return new ResolutionResult(matched[0], matched.Length > 1);
            }
        }

        // Единственный .csproj — допустим; несколько — неоднозначность,
        // но всё же лучше, чем ничего (берём первый по алфавиту).
        var projects = candidates.Where(c =>
            Path.GetExtension(c).Equals(".csproj", StringComparison.OrdinalIgnoreCase)).ToArray();

        return projects.Length > 0
            ? new ResolutionResult(projects[0], projects.Length > 1)
            : ResolutionResult.NotFound;
    }

    private static void Collect(string dir, int depth, List<string> acc)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file);
            if (ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                acc.Add(Path.GetFullPath(file));
            }
        }

        IEnumerable<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(dir);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        foreach (var sub in subdirs)
        {
            var name = Path.GetFileName(sub);
            if (ExcludedDirs.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith('.'))
            {
                continue;
            }

            Collect(sub, depth + 1, acc);
        }
    }
}

/// <summary>
/// Результат резолва: путь к файлу решения/проекта и признак неоднозначности.
/// </summary>
/// <param name="SolutionPath">Абсолютный путь к <c>.slnx</c>/<c>.sln</c>/<c>.csproj</c>.</param>
/// <param name="Ambiguous">true — подходящих файлов несколько, взят первый по алфавиту.</param>
public sealed record ResolutionResult(string SolutionPath, bool Ambiguous)
{
    /// <summary>Ничего не найдено.</summary>
    public static readonly ResolutionResult NotFound = new("", false);

    /// <summary>Решение найдено (путь непустой).</summary>
    public bool Found => !string.IsNullOrEmpty(SolutionPath);
}
