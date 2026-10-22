namespace ArtasTools.MonitorAgent.Roslyn;

/// <summary>
/// Чтение исходного текста члена по координате (файл + строка).
/// </summary>
public sealed class CodeReader
{
    private readonly RoslynOptions _options;

    public CodeReader(RoslynOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Читает текст с указанной строки (1-based) до конца члена.
    /// </summary>
    /// <param name="filePath">Абсолютный или относительный путь к файлу.</param>
    /// <param name="startLine">Строка начала (1-based).</param>
    public async Task<CodeSnippet?> ReadAsync(
        string filePath,
        int startLine,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || startLine <= 0)
        {
            return null;
        }

        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(filePath, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (startLine > lines.Length)
        {
            return null;
        }

        var startIndex = startLine - 1;
        var endIndex = FindMemberEnd(lines, startIndex);

        var slice = string.Join('\n', lines[startIndex..(endIndex + 1)]);
        var truncated = slice.Length > _options.MaxSourceChars;

        if (truncated)
        {
            slice = slice[.._options.MaxSourceChars] + "\n… (обрезано)";
        }

        var symbol = new CodeSymbol(
            Kind: "member",
            FullName: Path.GetFileName(filePath),
            FileName: filePath,
            Line: startLine);

        return new CodeSnippet(symbol, slice, truncated);
    }

    /// <summary>
    /// Определяет последнюю строку члена по балансу фигурных скобок,
    /// начиная со строки <paramref name="startIndex"/>.
    /// </summary>
    private static int FindMemberEnd(string[] lines, int startIndex)
    {
        var depth = 0;
        var seenBrace = false;
        var limit = Math.Min(lines.Length, startIndex + 500);

        for (var i = startIndex; i < limit; i++)
        {
            foreach (var c in lines[i])
            {
                if (c == '{')
                {
                    depth++;
                    seenBrace = true;
                }
                else if (c == '}')
                {
                    depth--;
                    if (seenBrace && depth == 0)
                    {
                        return i;
                    }
                }
            }

            // Член без тела (например, авто-свойство с одним выражением) —
            // останавливаемся на точке с запятой при нулевой глубине.
            if (!seenBrace && lines[i].TrimEnd().EndsWith(';'))
            {
                return i;
            }
        }

        return Math.Min(lines.Length - 1, limit - 1);
    }
}
