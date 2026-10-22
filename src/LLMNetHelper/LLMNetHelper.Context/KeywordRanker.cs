using System.Text.RegularExpressions;

namespace LLMNetHelper.Context;

/// <summary>
/// Ранжировщик чанков по релевантности запросу.
/// Реализует упрощённый BM25 без внешних зависимостей — это важно, потому что
/// векторных БД и эмбеддингов в окружении нет (см. план, §2).
/// </summary>
public sealed partial class KeywordRanker
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    /// <summary>
    /// Минимальная длина токена.
    /// </summary>
    private const int MinTokenLength = 2;

    /// <summary>
    /// Ранжирует документы против запроса и возвращает их в порядке убывания оценки.
    /// Документы с нулевой оценкой отбрасываются.
    /// </summary>
    public IReadOnlyList<ScoredDocument> Rank<T>(
        string query,
        IReadOnlyList<T> documents,
        Func<T, string> textSelector,
        Func<T, string> idSelector)
    {
        // Термы запроса усекаются до основы (первые N символов), чтобы
        // "фрактал" находил "фракталов", а "ошибка" — "ошибки"/"ошибок".
        // Это дешёвая замена стеммингу: без внешних зависимостей.
        var queryTokens = TokenizeWords(query)
            .Select(Stem)
            .Where(static t => t.Length >= MinTokenLength)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (queryTokens.Length == 0 || documents.Count == 0)
        {
            return [];
        }

        // В документах тоже считаем по основам — иначе словоформы не совпадут.
        var docs = documents
            .Select(d => ToStemCounts(textSelector(d)))
            .ToList();

        // Средняя длина документа — знаменатель BM25.
        var avgLength = docs.Average(static t => t.Values.Sum() == 0 ? 1.0 : (double)t.Values.Sum());
        if (avgLength <= 0)
        {
            return [];
        }

        // document frequency: в скольких документах встречается термин.
        var df = new Dictionary<string, int>(queryTokens.Length, StringComparer.Ordinal);
        foreach (var token in queryTokens)
        {
            var count = 0;
            foreach (var doc in docs)
            {
                if (doc.ContainsKey(token))
                {
                    count++;
                }
            }
            df[token] = count;
        }

        var results = new List<ScoredDocument>(documents.Count);
        var n = docs.Count;

        for (var i = 0; i < documents.Count; i++)
        {
            var tf = docs[i];
            var docLength = tf.Values.Sum();
            double score = 0;

            foreach (var token in queryTokens)
            {
                if (!tf.TryGetValue(token, out var freq) || freq == 0)
                {
                    continue;
                }

                var docFreq = df[token];
                var idf = Math.Log(1 + (n - docFreq + 0.5) / (docFreq + 0.5));
                var numerator = freq * (K1 + 1);
                var denominator = freq + K1 * (1 - B + B * docLength / avgLength);

                score += idf * numerator / denominator;
            }

            if (score > 0)
            {
                results.Add(new ScoredDocument(idSelector(documents[i]), score));
            }
        }

        results.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        return results;
    }

    /// <summary>
    /// Минимальная длина основы. Для русского языка 4–5 символов достаточно,
    /// чтобы отсечь окончания, не склеивая разные слова.
    /// </summary>
    private const int StemLength = 5;

    /// <summary>
    /// Усечение токена до основы.
    /// </summary>
    public static string Stem(string token)
    {
        if (token.Length <= StemLength)
        {
            return token;
        }

        return token[..StemLength];
    }

    /// <summary>
    /// Считает частоты основ токенов документа.
    /// </summary>
    private static Dictionary<string, int> ToStemCounts(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var token in TokenizeWords(text))
        {
            var stem = Stem(token);
            if (stem.Length < MinTokenLength)
            {
                continue;
            }

            counts[stem] = counts.TryGetValue(stem, out var existing) ? existing + 1 : 1;
        }

        return counts;
    }

    /// <summary>
    /// Токенизация: нижний регистр, разбиение по не-буквам/цифрам,
    /// отбрасывание слишком коротких токенов.
    /// </summary>
    public static Dictionary<string, int> Tokenize(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return counts;
        }

        foreach (Match match in WordPattern().Matches(text))
        {
            var token = match.Value.ToLowerInvariant();
            if (token.Length < MinTokenLength)
            {
                continue;
            }

            counts[token] = counts.TryGetValue(token, out var existing) ? existing + 1 : 1;
        }

        return counts;
    }

    /// <summary>
    /// Разбивает текст на токены без подсчёта частот.
    /// </summary>
    public static IEnumerable<string> TokenizeWords(string text)
    {
        return WordPattern().Matches(text)
            .Select(static m => m.Value.ToLowerInvariant())
            .Where(static t => t.Length >= MinTokenLength);
    }

    /// <summary>
    /// Буквы (включая кириллицу), цифры и подчёркивание.
    /// </summary>
    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();
}

/// <summary>
/// Документ с оценкой релевантности.
/// </summary>
/// <param name="Id">Идентификатор документа в источнике.</param>
/// <param name="Score">Оценка BM25.</param>
public sealed record ScoredDocument(string Id, double Score);
