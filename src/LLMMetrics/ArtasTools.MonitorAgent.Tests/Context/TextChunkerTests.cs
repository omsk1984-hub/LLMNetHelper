using ArtasTools.MonitorAgent.Context;

namespace ArtasTools.MonitorAgent.Tests.Context;

public class TextChunkerTests
{
    [Fact]
    public void Split_EmptyText_ReturnsEmpty()
    {
        Assert.Empty(TextChunker.Split("", "title"));
        Assert.Empty(TextChunker.Split("   ", "title"));
    }

    [Fact]
    public void Split_ShortText_ReturnsSingleChunkWithFallbackTitle()
    {
        var chunks = TextChunker.Split("# Заголовок\n\nкраткий текст", "fallback");

        Assert.Single(chunks);
        Assert.Equal("Заголовок", chunks[0].Title);
        Assert.Contains("краткий текст", chunks[0].Text);
    }

    [Fact]
    public void Split_ByHeadings_ProducesSeparateChunks()
    {
        var text = """
            # Первый
            текст один

            # Второй
            текст два
            """;

        var chunks = TextChunker.Split(text, "fallback", chunkSize: 1000);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Первый", chunks[0].Title);
        Assert.Equal("Второй", chunks[1].Title);
    }

    [Fact]
    public void Split_LongText_RespectsChunkSize()
    {
        var paragraph = new string('a', 500);
        var text = string.Join("\n\n", Enumerable.Repeat(paragraph, 20));

        var chunks = TextChunker.Split(text, "big", chunkSize: 1000, overlap: 100);

        Assert.True(chunks.Count > 1);
        foreach (var chunk in chunks)
        {
            // Небольшой допуск: чанк собирается из целых абзацев.
            Assert.True(chunk.Text.Length <= 1200, $"чанк слишком велик: {chunk.Text.Length}");
        }
    }

    [Fact]
    public void Split_AssignsSequentialOrdinals()
    {
        var text = "# A\n" + new string('x', 2000) + "\n\n# B\n" + new string('y', 2000);
        var chunks = TextChunker.Split(text, "fallback", chunkSize: 500, overlap: 50);

        var ordinals = chunks.Select(static c => c.Ordinal).ToArray();
        Assert.Equal(Enumerable.Range(0, chunks.Count).ToArray(), ordinals);
    }

    [Fact]
    public void Split_HeadingOnlyLevels1To6_AreDetected()
    {
        var text = "###### Глубокий\nтело";
        var chunks = TextChunker.Split(text, "fallback");

        Assert.Single(chunks);
        Assert.Equal("Глубокий", chunks[0].Title);
    }

    [Fact]
    public void Split_SevenHashes_IsNotHeading()
    {
        var text = "####### не заголовок\nтело";
        var chunks = TextChunker.Split(text, "fallback");

        Assert.Single(chunks);
        Assert.Equal("fallback", chunks[0].Title);
    }
}
