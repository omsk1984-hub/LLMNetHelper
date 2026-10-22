using ArtasTools.MonitorAgent.Context;

namespace ArtasTools.MonitorAgent.Tests.Context;

public class KeywordRankerTests
{
    private sealed record Doc(string Id, string Text);

    private static IReadOnlyList<ScoredDocument> Rank(string query, params Doc[] docs)
    {
        var ranker = new KeywordRanker();
        return ranker.Rank(
            query,
            docs,
            static d => d.Text,
            static d => d.Id);
    }

    [Fact]
    public void Rank_EmptyQuery_ReturnsEmpty()
    {
        Assert.Empty(Rank("", new Doc("1", "текст")));
    }

    [Fact]
    public void Rank_NoDocuments_ReturnsEmpty()
    {
        Assert.Empty(Rank("ошибка", []));
    }

    [Fact]
    public void Rank_PrefersDocumentWithQueryTerm()
    {
        var result = Rank(
            "timeout подключения",
            new Doc("relevant", "ошибка timeout при подключении к базе"),
            new Doc("irrelevant", "совсем другой текст про фракталы"));

        Assert.NotEmpty(result);
        Assert.Equal("relevant", result[0].Id);
    }

    [Fact]
    public void Rank_TermFrequencyMatters()
    {
        var result = Rank(
            "null",
            new Doc("many", "null null null null ссылка"),
            new Doc("few", "null ссылка упомянута один раз"));

        Assert.Equal("many", result[0].Id);
    }

    [Fact]
    public void Rank_DropsZeroScoreDocuments()
    {
        var result = Rank(
            "уникальноеслово",
            new Doc("a", "совершенно другой текст"),
            new Doc("b", "ещё один текст"));

        Assert.Empty(result);
    }

    [Fact]
    public void Rank_IsDeterministic()
    {
        var docs = new[]
        {
            new Doc("1", "ошибка при записи во vault"),
            new Doc("2", "ошибка при чтении из vault"),
            new Doc("3", "ошибка при записи во vault")
        };

        var first = Rank("ошибка vault", docs).Select(static r => r.Id).ToArray();
        var second = Rank("ошибка vault", docs).Select(static r => r.Id).ToArray();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Rank_CyrillicTokens_AreSupported()
    {
        var result = Rank("фрактал", new Doc("doc", "расчёт фракталов на рынке"));

        Assert.Single(result);
        Assert.Equal("doc", result[0].Id);
    }

    [Fact]
    public void Tokenize_IgnoresSingleCharacterTokens()
    {
        var tokens = KeywordRanker.Tokenize("a bb ccc");

        Assert.False(tokens.ContainsKey("a"));
        Assert.True(tokens.ContainsKey("bb"));
        Assert.True(tokens.ContainsKey("ccc"));
    }

    [Fact]
    public void Tokenize_IsCaseInsensitive()
    {
        var tokens = KeywordRanker.Tokenize("Error ERROR error");

        Assert.Equal(3, tokens["error"]);
    }

    [Fact]
    public void TokenizeWords_ReturnsLowercaseTokens()
    {
        var words = KeywordRanker.TokenizeWords("Привет Мир").ToArray();

        Assert.Contains("привет", words);
        Assert.Contains("мир", words);
    }
}
