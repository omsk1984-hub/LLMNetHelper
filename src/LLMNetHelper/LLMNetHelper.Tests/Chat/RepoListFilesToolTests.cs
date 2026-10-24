using LLMNetHelper.Chat;
using LLMNetHelper.Chat.Tools;
using LLMNetHelper.GitLab;

namespace LLMNetHelper.Tests.Chat;

public class RepoListFilesToolTests : IDisposable
{
    private readonly string _root;

    public RepoListFilesToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"chat_list_{Guid.NewGuid():N}");
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

    private static RepoListFilesTool CreateTool(ChatScope scope, ChatOptions? options = null) =>
        new(scope, options ?? new ChatOptions());

    [Fact]
    public async Task List_Subdirectory_ReturnsMatchingFiles()
    {
        Write("alpha/src/App.cs", "class App { }");
        Write("alpha/src/Notes.md", "# notes");
        Write("alpha/src/data.bin", "binary");

        var scope = new ChatScope(new GitLabOptions { CloneRoot = _root }, new ChatOptions());
        scope.TrySetRepo("alpha", out _);

        var result = await CreateTool(scope).InvokeAsync("""{"path":"src"}""");

        Assert.Contains("App.cs", result, StringComparison.Ordinal);
        Assert.Contains("Notes.md", result, StringComparison.Ordinal);
        Assert.DoesNotContain("data.bin", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_EmptyPath_ListsScopeRoot()
    {
        Write("alpha/App.cs", "class App { }");

        var scope = new ChatScope(new GitLabOptions { CloneRoot = _root }, new ChatOptions());
        scope.TrySetRepo("alpha", out _);

        var result = await CreateTool(scope).InvokeAsync("{}");

        Assert.Contains("App.cs", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_PathTraversal_Rejected()
    {
        var scope = new ChatScope(new GitLabOptions { CloneRoot = _root }, new ChatOptions());
        scope.TrySetRepo("alpha", out _);

        var result = await CreateTool(scope).InvokeAsync("""{"path":"../../.."}""");

        Assert.Contains("Отказ", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_LimitApplied()
    {
        Write("alpha/a.cs", "class A { }");
        Write("alpha/b.cs", "class B { }");
        Write("alpha/c.cs", "class C { }");

        var scope = new ChatScope(new GitLabOptions { CloneRoot = _root }, new ChatOptions());
        scope.TrySetRepo("alpha", out _);

        var result = await CreateTool(scope).InvokeAsync("""{"limit":2}""");

        Assert.Contains("лимит 2", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_NoMatches_Reports()
    {
        Write("alpha/readme.txt", "nothing");

        var scope = new ChatScope(new GitLabOptions { CloneRoot = _root }, new ChatOptions());
        scope.TrySetRepo("alpha", out _);

        var result = await CreateTool(scope).InvokeAsync("""{"path":"empty_dir"}""");

        Assert.Contains("не найдены", result, StringComparison.Ordinal);
    }
}
