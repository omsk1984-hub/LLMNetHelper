using System.Text.RegularExpressions;

namespace LLMNetHelper.Context;

/// <summary>
/// Обход каталога с фильтрацией по glob-шаблонам.
/// Не бросает исключений при недоступных файлах — пропускает их.
/// </summary>
public sealed class FileWalker
{
    private readonly LocalFolderOptions _options;
    private readonly Regex[] _include;
    private readonly Regex[] _exclude;

    public FileWalker(LocalFolderOptions options)
    {
        _options = options;
        _include = GlobCompiler.Compile(options.Include);
        _exclude = GlobCompiler.Compile(options.Exclude);
    }

    /// <summary>
    /// Перечисляет файлы, подходящие под Include и не подходящие под Exclude.
    /// </summary>
    public IReadOnlyList<WalkedFile> Walk(CancellationToken ct = default)
    {
        var root = _options.ResolveRoot();
        if (!Directory.Exists(root))
        {
            return [];
        }

        var result = new List<WalkedFile>();
        var enumeration = SafeEnumerate(root);

        foreach (var fullPath in enumeration)
        {
            ct.ThrowIfCancellationRequested();

            if (result.Count >= _options.MaxFiles)
            {
                break;
            }

            var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');

            if (!GlobCompiler.MatchesAny(_include, relative))
            {
                continue;
            }

            if (GlobCompiler.MatchesAny(_exclude, relative))
            {
                continue;
            }

            long size;
            try
            {
                size = new FileInfo(fullPath).Length;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (size > _options.MaxFileBytes)
            {
                continue;
            }

            result.Add(new WalkedFile(relative, fullPath, size));
        }

        // Детерминированный порядок — важно для воспроизводимости прогона.
        result.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return result;
    }

    /// <summary>
    /// Перечисление без падения на недоступных подкаталогах.
    /// </summary>
    private static IEnumerable<string> SafeEnumerate(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] files;
            string[] dirs;

            try
            {
                files = Directory.GetFiles(dir);
                dirs = Directory.GetDirectories(dir);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var f in files)
            {
                yield return f;
            }

            foreach (var d in dirs)
            {
                pending.Push(d);
            }
        }
    }
}

/// <summary>
/// Файл, найденный при обходе.
/// </summary>
/// <param name="RelativePath">Путь относительно корня, с прямыми слэшами.</param>
/// <param name="FullPath">Абсолютный путь.</param>
/// <param name="SizeBytes">Размер файла.</param>
public sealed record WalkedFile(string RelativePath, string FullPath, long SizeBytes);

/// <summary>
/// Компиляция glob-шаблонов в регулярные выражения.
/// Поддерживает <c>**</c>, <c>*</c> и <c>?</c>, регистронезависимо.
/// </summary>
public static class GlobCompiler
{
    private static readonly TimeSpan _MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Компилирует набор glob-шаблонов.
    /// </summary>
    public static Regex[] Compile(IEnumerable<string> patterns)
    {
        return patterns
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(CompileOne)
            .ToArray();
    }

    /// <summary>
    /// Компилирует один glob-шаблон.
    /// </summary>
    public static Regex CompileOne(string glob)
    {
        var normalized = glob.Replace('\\', '/').Trim();
        var pattern = ToRegex(normalized);
        return new Regex(
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
            _MatchTimeout);
    }

    /// <summary>
    /// Проверяет, совпадает ли путь хотя бы с одним шаблоном.
    /// </summary>
    public static bool MatchesAny(IReadOnlyList<Regex> patterns, string relativePath)
    {
        for (var i = 0; i < patterns.Count; i++)
        {
            if (patterns[i].IsMatch(relativePath))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Преобразование glob в regex. <c>**/</c> означает «любая глубина, включая ноль».
    /// </summary>
    private static string ToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");

        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];

            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                // "**/" -> ноль или более сегментов; "**" в конце -> что угодно
                var hasSlashAfter = i + 2 < glob.Length && glob[i + 2] == '/';
                if (hasSlashAfter)
                {
                    sb.Append("(?:.*/)?");
                    i += 2;
                }
                else
                {
                    sb.Append(".*");
                    i += 1;
                }
                continue;
            }

            switch (c)
            {
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        sb.Append('$');
        return sb.ToString();
    }
}
