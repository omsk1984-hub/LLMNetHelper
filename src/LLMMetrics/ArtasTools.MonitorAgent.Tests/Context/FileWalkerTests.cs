using ArtasTools.MonitorAgent.Context;

namespace ArtasTools.MonitorAgent.Tests.Context;

public class FileWalkerTests : IDisposable
{
    private readonly string _root;

    public FileWalkerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ctx_walk_{Guid.NewGuid():N}");
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
            // Тестовый каталог во временной папке — не критично.
        }
        GC.SuppressFinalize(this);
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private LocalFolderOptions Options(params string[] includes)
    {
        return new LocalFolderOptions
        {
            Path = _root,
            Include = includes.Length > 0 ? includes : ["**/*.md"]
        };
    }

    [Fact]
    public void Walk_FindsMarkdownRecursively()
    {
        Write("README.md", "# root");
        Write("docs/a.md", "# a");
        Write("docs/nested/b.md", "# b");

        var walker = new FileWalker(Options("**/*.md"));
        var files = walker.Walk();

        Assert.Equal(3, files.Count);
    }

    [Fact]
    public void Walk_RespectsIncludePattern()
    {
        Write("README.md", "# root");
        Write("Program.cs", "class P {}");

        var walker = new FileWalker(Options("**/*.cs"));
        var files = walker.Walk();

        Assert.Single(files);
        Assert.EndsWith("Program.cs", files[0].RelativePath);
    }

    [Fact]
    public void Walk_ExcludesBinAndObj()
    {
        Write("docs/a.md", "# a");
        Write("bin/Debug/b.md", "# b");
        Write("obj/c.md", "# c");

        var walker = new FileWalker(Options("**/*.md"));
        var files = walker.Walk();

        Assert.Single(files);
        Assert.Equal("docs/a.md", files[0].RelativePath);
    }

    [Fact]
    public void Walk_ExcludesSecretFiles()
    {
        Write("docs/guide.md", "# guide");
        Write(".env", "SECRET=1");
        Write("docs/secrets.md", "token");

        var walker = new FileWalker(Options("**/*.md", "**/.env"));
        var files = walker.Walk();

        Assert.DoesNotContain(files, f => f.RelativePath.Contains("secrets"));
    }

    [Fact]
    public void Walk_EmptyDirectory_ReturnsEmpty()
    {
        var walker = new FileWalker(Options("**/*.md"));
        Assert.Empty(walker.Walk());
    }

    [Fact]
    public void Walk_MissingDirectory_ReturnsEmptyWithoutThrow()
    {
        var options = new LocalFolderOptions
        {
            Path = Path.Combine(_root, "does-not-exist"),
            Include = ["**/*.md"]
        };

        var walker = new FileWalker(options);
        Assert.Empty(walker.Walk());
    }

    [Fact]
    public void Walk_SkipsOversizedFiles()
    {
        Write("docs/small.md", new string('x', 100));
        Write("docs/big.md", new string('x', 2000));

        var options = Options("**/*.md");
        options.MaxFileBytes = 500;

        var walker = new FileWalker(options);
        var files = walker.Walk();

        Assert.Single(files);
        Assert.Equal("docs/small.md", files[0].RelativePath);
    }

    [Fact]
    public void Walk_DeterministicOrder()
    {
        Write("b.md", "b");
        Write("a.md", "a");
        Write("c.md", "c");

        var walker = new FileWalker(Options("**/*.md"));
        var first = walker.Walk().Select(static f => f.RelativePath).ToArray();
        var second = walker.Walk().Select(static f => f.RelativePath).ToArray();

        Assert.Equal(first, second);
        Assert.Equal(["a.md", "b.md", "c.md"], first);
    }
}

public class GlobCompilerTests
{
    [Theory]
    [InlineData("**/*.md", "README.md", true)]
    [InlineData("**/*.md", "docs/a.md", true)]
    [InlineData("**/*.md", "docs/nested/deep/a.md", true)]
    [InlineData("**/*.md", "a.txt", false)]
    [InlineData("**/bin/**", "bin/x.md", true)]
    [InlineData("**/bin/**", "src/bin/Debug/x.md", true)]
    [InlineData("**/bin/**", "docs/x.md", false)]
    [InlineData("*.md", "a.md", true)]
    [InlineData("*.md", "docs/a.md", false)]
    public void CompileOne_MatchesExpected(string glob, string path, bool expected)
    {
        var regex = GlobCompiler.CompileOne(glob);
        Assert.Equal(expected, regex.IsMatch(path));
    }

    [Fact]
    public void MatchesAny_EmptyList_ReturnsFalse()
    {
        Assert.False(GlobCompiler.MatchesAny([], "a.md"));
    }

    [Fact]
    public void Compile_IsCaseInsensitive()
    {
        var patterns = GlobCompiler.Compile(["**/*.MD"]);
        Assert.True(GlobCompiler.MatchesAny(patterns, "docs/readme.md"));
    }
}
