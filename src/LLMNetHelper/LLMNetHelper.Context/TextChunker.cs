namespace LLMNetHelper.Context;

/// <summary>
/// Чанк текста с заголовком секции, к которой он относится.
/// </summary>
/// <param name="Title">Заголовок секции (или имя файла, если заголовков нет).</param>
/// <param name="Text">Текст чанка.</param>
/// <param name="Ordinal">Порядковый номер чанка в файле.</param>
public sealed record TextChunk(string Title, string Text, int Ordinal);

/// <summary>
/// Разбиение Markdown/текста на чанки.
/// Сначала режет по заголовкам, затем — по абзацам, затем — принудительно по размеру.
/// Перекрытие помогает не терять контекст на границах.
/// </summary>
public static class TextChunker
{
    /// <summary>
    /// Разбивает текст на чанки.
    /// </summary>
    /// <param name="text">Исходный текст.</param>
    /// <param name="fallbackTitle">Заголовок, если в тексте нет разметки.</param>
    /// <param name="chunkSize">Целевой размер чанка в символах.</param>
    /// <param name="overlap">Перекрытие соседних чанков при принудительном делении.</param>
    public static IReadOnlyList<TextChunk> Split(
        string text,
        string fallbackTitle,
        int chunkSize = 3000,
        int overlap = 300)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        if (chunkSize < 200)
        {
            chunkSize = 200;
        }

        if (overlap < 0 || overlap >= chunkSize / 2)
        {
            overlap = Math.Max(0, chunkSize / 10);
        }

        var sections = SplitByHeadings(text, fallbackTitle);
        var chunks = new List<TextChunk>();
        var ordinal = 0;

        foreach (var (title, body) in sections)
        {
            if (body.Length <= chunkSize)
            {
                if (!string.IsNullOrWhiteSpace(body))
                {
                    chunks.Add(new TextChunk(title, body.Trim(), ordinal++));
                }
                continue;
            }

            foreach (var part in SplitLarge(body, chunkSize, overlap))
            {
                chunks.Add(new TextChunk(title, part, ordinal++));
            }
        }

        return chunks;
    }

    /// <summary>
    /// Делит текст по Markdown-заголовкам (#, ##, ###).
    /// Заголовок становится Title секции.
    /// </summary>
    private static List<(string Title, string Body)> SplitByHeadings(string text, string fallbackTitle)
    {
        var sections = new List<(string, string)>();
        var lines = text.Split('\n');

        string currentTitle = fallbackTitle;
        var buffer = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r');

            if (IsHeading(trimmed, out var headingText))
            {
                if (buffer.Length > 0)
                {
                    sections.Add((currentTitle, buffer.ToString()));
                    buffer.Clear();
                }
                currentTitle = headingText;
                continue;
            }

            buffer.AppendLine(trimmed);
        }

        if (buffer.Length > 0)
        {
            sections.Add((currentTitle, buffer.ToString()));
        }

        if (sections.Count == 0)
        {
            sections.Add((fallbackTitle, text));
        }

        return sections;
    }

    private static bool IsHeading(string line, out string headingText)
    {
        headingText = "";
        if (line.Length < 2 || line[0] != '#')
        {
            return false;
        }

        var level = 0;
        while (level < line.Length && line[level] == '#')
        {
            level++;
        }

        if (level is < 1 or > 6 || level >= line.Length || line[level] != ' ')
        {
            return false;
        }

        headingText = line[(level + 1)..].Trim();
        return headingText.Length > 0;
    }

    /// <summary>
    /// Принудительное деление длинного текста по абзацам, с перекрытием.
    /// </summary>
    private static IEnumerable<string> SplitLarge(string body, int chunkSize, int overlap)
    {
        var paragraphs = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var current = new System.Text.StringBuilder();

        foreach (var paragraph in paragraphs)
        {
            var p = paragraph.Trim();
            if (p.Length == 0)
            {
                continue;
            }

            // Сам абзац длиннее чанка — режем жёстко.
            if (p.Length > chunkSize)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString().Trim();
                    current.Clear();
                }

                for (var i = 0; i < p.Length; i += chunkSize - overlap)
                {
                    var len = Math.Min(chunkSize, p.Length - i);
                    yield return p.Substring(i, len).Trim();
                    if (i + len >= p.Length)
                    {
                        break;
                    }
                }
                continue;
            }

            if (current.Length + p.Length + 2 > chunkSize && current.Length > 0)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.AppendLine();
                current.AppendLine();
            }
            current.Append(p);
        }

        if (current.Length > 0)
        {
            yield return current.ToString().Trim();
        }
    }
}
