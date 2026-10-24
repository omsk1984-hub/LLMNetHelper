using LLMNetHelper.Chat;
using LLMNetHelper.GitLab;

namespace LLMNetHelper.Tests.Chat;

public class ChatScopeTests : IDisposable
{
    private readonly string _root;

    public ChatScopeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"chat_scope_{Guid.NewGuid():N}");
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

    private ChatScope CreateScope() =>
        new(new GitLabOptions { CloneRoot = _root }, new ChatOptions());

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void ListRepos_ReturnsSortedSubdirectories()
    {
        Write("beta/Program.cs", "// beta");
        Write("alpha/README.md", "# alpha");
        Write(".hidden/x.md", "секрет");
        Write("top-level.md", "не репозиторий");

        var repos = CreateScope().ListRepos();

        Assert.Equal(["alpha", "beta"], repos);
    }

    [Fact]
    public void ListRepos_MissingRoot_ReturnsEmpty()
    {
        var scope = new ChatScope(
            new GitLabOptions { CloneRoot = Path.Combine(_root, "nope") },
            new ChatOptions());

        Assert.Empty(scope.ListRepos());
    }

    [Fact]
    public void Default_RootIsCloneRoot()
    {
        var scope = CreateScope();

        Assert.Null(scope.CurrentRepo);
        Assert.Equal(_root, scope.RootPath);
    }

    [Fact]
    public void TrySetRepo_Existing_NarrowsRoot()
    {
        Write("alpha/README.md", "# alpha");
        var scope = CreateScope();

        var ok = scope.TrySetRepo("alpha", out var message);

        Assert.True(ok);
        Assert.Equal("alpha", scope.CurrentRepo);
        Assert.Equal(Path.Combine(_root, "alpha"), scope.RootPath);
        Assert.Contains("alpha", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TrySetRepo_Missing_FailsAndKeepsScope()
    {
        Write("alpha/README.md", "# alpha");
        var scope = CreateScope();

        var ok = scope.TrySetRepo("ghost", out var message);

        Assert.False(ok);
        Assert.Null(scope.CurrentRepo);
        Assert.Contains("ghost", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TrySetRepo_EmptyName_ResetsToAll()
    {
        Write("alpha/README.md", "# alpha");
        var scope = CreateScope();
        scope.TrySetRepo("alpha", out _);

        var ok = scope.TrySetRepo("", out _);

        Assert.True(ok);
        Assert.Null(scope.CurrentRepo);
    }

    [Fact]
    public void Reset_ReturnsToCloneRoot()
    {
        Write("alpha/README.md", "# alpha");
        var scope = CreateScope();
        scope.TrySetRepo("alpha", out _);

        scope.Reset();

        Assert.Null(scope.CurrentRepo);
        Assert.Equal(_root, scope.RootPath);
    }

    [Fact]
    public void TryResolveInside_RelativePath_WithinScope()
    {
        Write("alpha/src/App.cs", "class App {}");
        var scope = CreateScope();

        var ok = scope.TryResolveInside("alpha/src/App.cs", out var full);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "alpha/src/App.cs")), full);
    }

    [Fact]
    public void TryResolveInside_PathTraversal_Rejected()
    {
        Write("alpha/README.md", "# alpha");
        var scope = CreateScope();
        scope.TrySetRepo("alpha", out _);

        Assert.False(scope.TryResolveInside("../../outside.txt", out _));
        Assert.False(scope.TryResolveInside(Path.Combine(_root, "..", "evil.txt"), out _));
        Assert.False(scope.TryResolveInside("", out _));
        Assert.False(scope.TryResolveInside("   ", out _));
    }

    [Fact]
    public void TryResolveInside_RootItself_Allowed()
    {
        var scope = CreateScope();

        Assert.True(scope.TryResolveInside(".", out var full));
        Assert.Equal(_root, full);
    }

    [Fact]
    public async Task Search_FindsFileInNarrowedRepoOnly()
    {
        Write("alpha/guide.md", "# Alpha\nуникальноеКлючевоеСловоZebra в альфе");
        Write("beta/guide.md", "# Beta\nобычный текст без слова");
        var scope = CreateScope();
        scope.TrySetRepo("beta", out _);

        var result = await scope.SearchAsync("уникальноеКлючевоеСловоZebra");

        Assert.All(result.Documents, d => Assert.StartsWith("beta/", d.Id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_AllRepos_SpansSubdirectories()
    {
        Write("alpha/guide.md", "# Alpha\nуникальноеКлючевоеСловоZebra");
        Write("beta/notes.md", "# Beta\nуникальноеКлючевоеСловоZebra тоже");
        var scope = CreateScope();

        var result = await scope.SearchAsync("уникальноеКлючевоеСловоZebra");

        Assert.True(result.Documents.Count >= 2);
    }

    [Fact]
    public void ListFiles_RespectsIncludePatternsAndLimit()
    {
        Write("alpha/a.cs", "class A {}");
        Write("alpha/b.bin", "binary");
        Write("alpha/c.md", "# c");
        var scope = CreateScope();
        scope.TrySetRepo("alpha", out _);

        var files = scope.ListFiles(null, limit: 10);

        Assert.Equal(["a.cs", "c.md"], files.Select(f => f.RelativePath).ToArray());
    }

    [Fact]
    public void ListFiles_LimitTruncates()
    {
        Write("alpha/a.cs", "class A {}");
        Write("alpha/b.cs", "class B {}");
        Write("alpha/c.cs", "class C {}");
        var scope = CreateScope();
        scope.TrySetRepo("alpha", out _);

        var files = scope.ListFiles(null, limit: 2);

        Assert.Equal(2, files.Count);
    }
}
