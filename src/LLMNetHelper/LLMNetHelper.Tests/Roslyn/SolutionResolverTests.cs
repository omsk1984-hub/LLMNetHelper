using LLMNetHelper.Roslyn;

namespace LLMNetHelper.Tests.Roslyn;

/// <summary>
/// Тесты резолвера файла решения (чистая ФС, офлайн).
/// </summary>
public class SolutionResolverTests : IDisposable
{
    private readonly string _root;

    public SolutionResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sol_resolver_{Guid.NewGuid():N}");
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

    private void Write(string relativePath)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "");
    }

    [Fact]
    public void Resolve_SlnxWins_OverSlnAndCsproj()
    {
        Write("Alpha.csproj");
        Write("Alpha.sln");
        Write("Alpha.slnx");

        var result = SolutionResolver.Resolve(_root);

        Assert.True(result.Found);
        Assert.EndsWith("Alpha.slnx", result.SolutionPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_SlnWins_OverCsproj()
    {
        Write("Alpha.csproj");
        Write("Alpha.sln");

        var result = SolutionResolver.Resolve(_root);

        Assert.True(result.Found);
        Assert.EndsWith("Alpha.sln", result.SolutionPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_OnlyCsproj_Found()
    {
        Write("Src/Alpha.csproj");

        var result = SolutionResolver.Resolve(_root);

        Assert.True(result.Found);
        Assert.EndsWith("Alpha.csproj", result.SolutionPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.Ambiguous);
    }

    [Fact]
    public void Resolve_MultipleCsproj_AmbiguousFirstAlphabetical()
    {
        Write("Zeta/Zeta.csproj");
        Write("Alpha/Alpha.csproj");

        var result = SolutionResolver.Resolve(_root);

        Assert.True(result.Found);
        Assert.True(result.Ambiguous);
        Assert.EndsWith("Alpha.csproj", result.SolutionPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_Nothing_FoundFalse()
    {
        Write("README.md");

        var result = SolutionResolver.Resolve(_root);

        Assert.False(result.Found);
        Assert.Equal("", result.SolutionPath);
    }

    [Fact]
    public void Resolve_BeyondMaxDepth_Ignored()
    {
        // Глубина 3: root/a/b/c/X.csproj — за пределами MaxDepth=2.
        Write("a/b/c/X.csproj");

        var result = SolutionResolver.Resolve(_root);

        Assert.False(result.Found);
    }

    [Fact]
    public void Resolve_WithinMaxDepth_Found()
    {
        // Глубина 2: root/src/X.csproj — в пределах.
        Write("src/X.csproj");

        var result = SolutionResolver.Resolve(_root);

        Assert.True(result.Found);
    }

    [Fact]
    public void Resolve_BinObjExcluded()
    {
        Write("bin/Release/X.csproj");
        Write("obj/X.csproj");
        Write("src/Real.csproj");

        var result = SolutionResolver.Resolve(_root);

        Assert.True(result.Found);
        Assert.EndsWith("Real.csproj", result.SolutionPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_MissingRoot_NotFound()
    {
        var result = SolutionResolver.Resolve(Path.Combine(_root, "ghost"));

        Assert.False(result.Found);
    }

    [Fact]
    public void Resolve_EmptyRoot_NotFound()
    {
        Assert.False(SolutionResolver.Resolve("").Found);
        Assert.False(SolutionResolver.Resolve(null!).Found);
    }
}
