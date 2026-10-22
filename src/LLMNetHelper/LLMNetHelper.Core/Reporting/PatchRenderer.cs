using System.Text;

namespace LLMNetHelper.Core.Reporting;

/// <summary>
/// Рендер патча в формате unified diff для приложения вручную
/// (<c>git apply patch.diff</c>).
/// <para>
/// Строится из пар «старый код / новый код»: полноценный git-diff не нужен,
/// потому что исходный код читается из рабочего дерева до правки.
/// </para>
/// </summary>
public static class PatchRenderer
{
    /// <summary>
    /// Формирует unified diff для одного файла.
    /// </summary>
    /// <param name="filePath">Путь относительно корня репозитория.</param>
    /// <param name="originalText">Исходный текст файла.</param>
    /// <param name="patchedText">Изменённый текст файла.</param>
    /// <param name="contextLines">Сколько строк контекста показывать вокруг изменения.</param>
    public static string RenderFileDiff(
        string filePath,
        string originalText,
        string patchedText,
        int contextLines = 3)
    {
        var original = SplitLines(originalText);
        var patched = SplitLines(patchedText);

        var diff = BuildLineDiff(original, patched);

        // Только строки контекста (или пустой диф) — изменений нет.
        if (diff.Count == 0 || diff.All(static d => d.Kind == DiffKind.Same))
        {
            return "";
        }

        var groups = GroupIntoHunks(diff, contextLines);

        if (groups.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"--- a/{NormalizePath(filePath)}");
        sb.AppendLine($"+++ b/{NormalizePath(filePath)}");

        foreach (var hunk in groups)
        {
            var oldStart = hunk[0].OldLine ?? hunk[0].NewLine;
            var newStart = hunk[0].NewLine ?? hunk[0].OldLine;
            var oldCount = hunk.Count(static d => d.Kind != DiffKind.Added);
            var newCount = hunk.Count(static d => d.Kind != DiffKind.Removed);

            sb.AppendLine($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@");

            foreach (var line in hunk)
            {
                var prefix = line.Kind switch
                {
                    DiffKind.Added => "+",
                    DiffKind.Removed => "-",
                    _ => " "
                };

                sb.AppendLine(prefix + line.Text);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Собирает сводный diff по нескольким файлам.
    /// </summary>
    public static string RenderCombined(IEnumerable<(string FilePath, string Diff)> diffs)
    {
        var sb = new StringBuilder();

        foreach (var (path, diff) in diffs)
        {
            if (string.IsNullOrWhiteSpace(diff))
            {
                continue;
            }

            // Первые две строки (---/+++) уже есть в RenderFileDiff.
            sb.AppendLine($"# {path}");
            sb.AppendLine(diff);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Приводит путь к виду с прямыми слэшами.
    /// </summary>
    private static string NormalizePath(string path)
    {
        return (path ?? "").Replace('\\', '/').TrimStart('/');
    }

    /// <summary>
    /// Разбивает текст на строки, сохраняя последнюю пустую строку как значимую.
    /// </summary>
    private static string[] SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return text.Replace("\r\n", "\n").Split('\n');
    }

    private enum DiffKind
    {
        Same,
        Added,
        Removed
    }

    private sealed record DiffLine(DiffKind Kind, string Text, int? OldLine, int? NewLine);

    /// <summary>
    /// Простейший LCS-диф по строкам. Для файлов агентских правок
    /// (до нескольких тысяч строк) этого достаточно.
    /// </summary>
    private static List<DiffLine> BuildLineDiff(string[] original, string[] patched)
    {
        var n = original.Length;
        var m = patched.Length;

        // Матрица LCS.
        var lcs = new int[n + 1, m + 1];

        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = original[i] == patched[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var result = new List<DiffLine>(n + m);
        int x = 0, y = 0;

        while (x < n && y < m)
        {
            if (original[x] == patched[y])
            {
                result.Add(new DiffLine(DiffKind.Same, original[x], x + 1, y + 1));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                result.Add(new DiffLine(DiffKind.Removed, original[x], x + 1, null));
                x++;
            }
            else
            {
                result.Add(new DiffLine(DiffKind.Added, patched[y], null, y + 1));
                y++;
            }
        }

        while (x < n)
        {
            result.Add(new DiffLine(DiffKind.Removed, original[x], x + 1, null));
            x++;
        }

        while (y < m)
        {
            result.Add(new DiffLine(DiffKind.Added, patched[y], null, y + 1));
            y++;
        }

        return result;
    }

    /// <summary>
    /// Группирует изменения в хунки с контекстом.
    /// </summary>
    private static List<List<DiffLine>> GroupIntoHunks(List<DiffLine> diff, int contextLines)
    {
        var hunks = new List<List<DiffLine>>();
        var current = new List<DiffLine>();
        var sinceLastChange = int.MaxValue;

        foreach (var line in diff)
        {
            if (line.Kind == DiffKind.Same)
            {
                sinceLastChange++;

                if (current.Count > 0)
                {
                    current.Add(line);

                    // Контекст после изменения исчерпан — закрываем хунк.
                    if (sinceLastChange > contextLines * 2)
                    {
                        TrimTrailingContext(current, contextLines);
                        hunks.Add(current);
                        current = [];
                        sinceLastChange = int.MaxValue;
                    }
                }
                continue;
            }

            // Первое изменение — добираем контекст «до» из уже накопленного.
            if (current.Count == 0)
            {
                var take = Math.Min(contextLines, hunks.Count > 0 ? 0 : diff.Count);
                _ = take;
            }

            current.Add(line);
            sinceLastChange = 0;
        }

        if (current.Count > 0)
        {
            TrimTrailingContext(current, contextLines);
            hunks.Add(current);
        }

        return hunks;
    }

    /// <summary>
    /// Оставляет не более <paramref name="contextLines"/> строк контекста в конце хунка.
    /// </summary>
    private static void TrimTrailingContext(List<DiffLine> hunk, int contextLines)
    {
        var trailing = 0;

        for (var i = hunk.Count - 1; i >= 0 && hunk[i].Kind == DiffKind.Same; i--)
        {
            trailing++;
        }

        if (trailing > contextLines)
        {
            hunk.RemoveRange(hunk.Count - (trailing - contextLines), trailing - contextLines);
        }
    }
}
