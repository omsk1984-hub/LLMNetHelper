using LLMNetHelper.Abstractions;
using LLMNetHelper.Context;
using LLMNetHelper.Infrastructure.Context;

namespace LLMNetHelper.Tests.Context;

public class LocalFolderContextSourceTests : IDisposable
{
    private readonly string _root;

    public LocalFolderContextSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ctx_src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        GC.SuppressFinalize(this);
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private LocalFolderContextSource CreateSource(string path = "")
    {
        var options = new LocalFolderOptions
        {
            Path = string.IsNullOrEmpty(path) ? _root : path,
            Include = ["**/*.md"],
            ChunkSize = 1000,
            ChunkOverlap = 100
        };

        return new LocalFolderContextSource(options, new ContextOptions());
    }

    [Fact]
    public async Task IsAvailable_ExistingFolderWithFiles_ReturnsTrue()
    {
        Write("guide.md", "# Guide\nтекст");
        var source = CreateSource();

        Assert.True(await source.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailable_MissingFolder_ReturnsFalseWithoutThrow()
    {
        var source = CreateSource(Path.Combine(_root, "nope"));

        Assert.False(await source.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailable_EmptyFolder_ReturnsFalse()
    {
        var source = CreateSource();

        Assert.False(await source.IsAvailableAsync());
    }

    [Fact]
    public async Task Search_FindsRelevantDocument()
    {
        Write("writer.md", "# Writer\nошибка подключения к finam api при таймауте");
        Write("reader.md", "# Reader\nотрисовка графиков во flox");

        var source = CreateSource();
        var result = await source.SearchAsync(new ContextQuery("таймаут подключения finam", TopK: 3));

        Assert.NotEmpty(result);
        Assert.Contains("writer.md", result[0].Id);
        Assert.Equal("local-folder", result[0].Source);
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsEmpty()
    {
        Write("a.md", "текст");
        var source = CreateSource();

        Assert.Empty(await source.SearchAsync(new ContextQuery("")));
    }

    [Fact]
    public async Task Search_RespectsTopK()
    {
        for (var i = 0; i < 10; i++)
        {
            Write($"doc{i}.md", $"# Doc{i}\nобщая ошибка номер {i}");
        }

        var source = CreateSource();
        var result = await source.SearchAsync(new ContextQuery("общая ошибка", TopK: 3));

        Assert.True(result.Count <= 3);
    }

    [Fact]
    public async Task Search_TruncatesToMaxChars()
    {
        Write("long.md", "# Long\n" + string.Join(' ', Enumerable.Repeat("слово", 5000)));

        var source = CreateSource();
        var result = await source.SearchAsync(new ContextQuery("слово", TopK: 1, MaxChars: 200));

        Assert.NotEmpty(result);
        Assert.True(result[0].Content.Length <= 250, $"длина {result[0].Content.Length}");
    }

    [Fact]
    public async Task Search_RedactsSecrets()
    {
        Write("config.md", "# Config\npassword=SuperSecret123\ntoken tapi_sk_abcdef123456");

        var source = CreateSource();
        var result = await source.SearchAsync(new ContextQuery("config password", TopK: 1));

        Assert.NotEmpty(result);
        Assert.DoesNotContain("SuperSecret123", result[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_SourceNameIsLocalFolder()
    {
        Write("a.md", "# A\nошибка");
        var source = CreateSource();

        Assert.Equal("local-folder", source.Name);
        var result = await source.SearchAsync(new ContextQuery("ошибка"));
        Assert.All(result, static d => Assert.Equal("local-folder", d.Source));
    }

    [Fact]
    public async Task Search_IsDeterministic()
    {
        Write("a.md", "# A\nодинаковая ошибка");
        Write("b.md", "# B\nодинаковая ошибка");

        var source = CreateSource();
        var first = (await source.SearchAsync(new ContextQuery("одинаковая ошибка"))).Select(static d => d.Id).ToArray();
        var second = (await source.SearchAsync(new ContextQuery("одинаковая ошибка"))).Select(static d => d.Id).ToArray();

        Assert.Equal(first, second);
    }
}
