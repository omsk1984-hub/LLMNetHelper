using LLMNetHelper.Chat;
using LLMNetHelper.Chat.Tools;
using LLMNetHelper.GitLab;

namespace LLMNetHelper.Tests.Chat;

public class RepoReadFileToolTests : IDisposable
{
    private readonly string _root;

    public RepoReadFileToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"chat_read_{Guid.NewGuid():N}");
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

    private static RepoReadFileTool CreateTool(ChatScope scope, ChatOptions? options = null) =>
        new(scope, options ?? new ChatOptions());

    [Fact]
    public async Task Read_InsideScope_ReturnsContent()
    {
        Write("alpha/src/App.cs", "class App { }");
        var scope = new ChatScope(new GitLabOptions { CloneRoot = _root }, new ChatOptions());

        var result = await CreateTool(scope).InvokeAsync("""{"path":"alpha/src/App.cs"}""");

        Assert.Contains("class App", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_NarrowedScope_RelativeToRepo()
    {
        Write("alpha/App.cs", "class App { }");
        Write("beta/Other.cs", "class Other { }");
        var scope = new ChatScope(new GitLabOptions { CloneRoot = _root }, new ChatOptions());
        scope.TrySetRepo("alpha", out _);

        var result = await CreateTool(scope).InvokeAsync("""{"path":"App.cs"}""");

        Assert.Contains("class App", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_PathTraversal_Rejected()
    {
        Write("alpha/App.cs", "class App { }");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), $"chat_outside_{Guid.NewGuid():N}.txt"), "outside");

        var scope = new ChatScope(new GitLabOptions { CloneRoot = _root }, new ChatOptions());
        scope.TrySetRepo("alpha", out _);

        var result = await CreateTool(scope).InvokeAsync("""{"path":"../../../outside.txt"}""");

        Assert.Contains("Отказ", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_AbsoluteOutsidePath_Rejected()
    {
        var scope = new ChatScope(new GitLabOptions { CloneRoot = _root }, new ChatOptions());
        var outside = Path.GetTempPath();

        var result = await CreateTool(scope).InvokeAsync($$"""{"path":"{{outside.Replace("\\", "\\\\")}}"}""");

        Assert.Contains("Отказ", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_MissingFile_ReportsNotFound()
    {
        var scope = new ChatScope(new GitLabOptions { CloneRoot = _root }, new ChatOptions());

        var result = await CreateTool(scope).InvokeAsync("""{"path":"ghost.cs"}""");

        Assert.Contains("не найден", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_RedactsSecrets()
    {
        Write("alpha/config.json", "{ \"api_key\": \"super-secret-value-123\" }");
        var scope = new ChatScope(new GitLabOptions { CloneRoot = _root }, new ChatOptions());

        var result = await CreateTool(scope).InvokeAsync("""{"path":"alpha/config.json"}""");

        Assert.DoesNotContain("super-secret-value-123", result, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_TruncatesByMaxChars()
    {
        Write("alpha/big.txt", new string('x', 5000));
        var scope = new ChatScope(new GitLabOptions { CloneRoot = _root }, new ChatOptions());
        var options = new ChatOptions { MaxChars = 100 };

        var result = await CreateTool(scope).InvokeAsync("""{"path":"alpha/big.txt","maxChars":50}""");

        Assert.Contains("обрезано", result, StringComparison.Ordinal);
        Assert.True(options.MaxChars > 0);
    }

    [Fact]
    public async Task Read_EmptyPath_AsksForPath()
    {
        var scope = new ChatScope(new GitLabOptions { CloneRoot = _root }, new ChatOptions());

        var result = await CreateTool(scope).InvokeAsync("{}");

        Assert.Contains("path", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_BrokenJson_ReturnsErrorMessage()
    {
        var scope = new ChatScope(new GitLabOptions { CloneRoot = _root }, new ChatOptions());

        var result = await CreateTool(scope).InvokeAsync("{not json");

        Assert.Contains("Ошибка разбора", result, StringComparison.Ordinal);
    }
}
