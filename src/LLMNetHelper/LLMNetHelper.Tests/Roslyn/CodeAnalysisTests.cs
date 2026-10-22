using LLMNetHelper.Roslyn;

namespace LLMNetHelper.Tests.Roslyn;

/// <summary>
/// Тесты поиска символов, чтения исходников, графа вызовов и анализа структуры.
/// </summary>
public class CodeAnalysisTests
{
    private static string FixtureProjectPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LLMNetHelper.Tests.csproj")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Не найден каталог тестового проекта");
        }

        return Path.Combine(dir.FullName, "Fixtures", "Roslyn", "SampleProject", "SampleProject.csproj");
    }

    private static RoslynOptions CreateOptions() => new()
    {
        Enabled = true,
        SolutionPath = FixtureProjectPath(),
        CompilationCacheSeconds = 60,
        MaxSearchResults = 50
    };

    [Fact]
    public async Task CodeSearch_FindsClass()
    {
        var options = CreateOptions();
        using var loader = new SolutionLoader(options);
        var search = new CodeSearchService(loader, options);

        var results = await search.SearchAsync("OrderProcessor");

        Assert.Contains(results, static r => r.Kind == "class" && r.FullName.Contains("OrderProcessor"));
    }

    [Fact]
    public async Task CodeSearch_FindsMethod()
    {
        var options = CreateOptions();
        using var loader = new SolutionLoader(options);
        var search = new CodeSearchService(loader, options);

        var results = await search.SearchAsync("ProcessOrder");

        Assert.Contains(results, static r => r.Kind == "method");
    }

    [Fact]
    public async Task CodeSearch_IsCaseInsensitive()
    {
        var options = CreateOptions();
        using var loader = new SolutionLoader(options);
        var search = new CodeSearchService(loader, options);

        var results = await search.SearchAsync("orderprocessor");

        Assert.NotEmpty(results);
    }

    [Fact]
    public async Task CodeSearch_NoMatch_ReturnsEmpty()
    {
        var options = CreateOptions();
        using var loader = new SolutionLoader(options);
        var search = new CodeSearchService(loader, options);

        Assert.Empty(await search.SearchAsync("СовершенноНесуществующийСимвол"));
    }

    [Fact]
    public async Task CodeSearch_EmptyQuery_ReturnsEmpty()
    {
        var options = CreateOptions();
        using var loader = new SolutionLoader(options);
        var search = new CodeSearchService(loader, options);

        Assert.Empty(await search.SearchAsync(""));
    }

    [Fact]
    public async Task CodeReader_ReadsMemberBody()
    {
        var options = CreateOptions();
        using var loader = new SolutionLoader(options);
        var search = new CodeSearchService(loader, options);
        var reader = new CodeReader(options);

        var symbols = await search.SearchAsync("SaveOrder");
        var method = symbols.First(static s => s.Kind == "method");

        var snippet = await reader.ReadAsync(method.FileName, method.Line);

        Assert.NotNull(snippet);
        Assert.Contains("SaveOrder", snippet.Text);
        Assert.Contains("catch", snippet.Text);
    }

    [Fact]
    public async Task CodeReader_MissingFile_ReturnsNull()
    {
        var options = CreateOptions();
        var reader = new CodeReader(options);

        Assert.Null(await reader.ReadAsync(Path.Combine(Path.GetTempPath(), "nope.cs"), 1));
    }

    [Fact]
    public async Task CodeReader_InvalidLine_ReturnsNull()
    {
        var options = CreateOptions();
        var reader = new CodeReader(options);

        Assert.Null(await reader.ReadAsync(FixtureProjectPath(), 99999));
    }

    [Fact]
    public async Task CallGraph_FindsCallers()
    {
        var options = CreateOptions();
        using var loader = new SolutionLoader(options);
        var graph = new CallGraphService(loader, options);

        var callers = await graph.FindCallersAsync("ProcessOrderAsync");

        Assert.Contains(callers, static c => c.FullName == "RunAsync");
    }

    [Fact]
    public async Task CallGraph_FindsCallees()
    {
        var options = CreateOptions();
        using var loader = new SolutionLoader(options);
        var graph = new CallGraphService(loader, options);

        var callees = await graph.FindCalleesAsync("RunAsync");

        Assert.Contains("ProcessOrderAsync", callees);
        Assert.Contains("SaveOrder", callees);
    }

    [Fact]
    public async Task CallGraph_UnknownMethod_ReturnsEmpty()
    {
        var options = CreateOptions();
        using var loader = new SolutionLoader(options);
        var graph = new CallGraphService(loader, options);

        Assert.Empty(await graph.FindCallersAsync("НетТакогоМетода"));
    }

    [Fact]
    public async Task Structure_SummarizesProjectsAndTypes()
    {
        var options = CreateOptions();
        using var loader = new SolutionLoader(options);
        var analyzer = new StructureAnalyzer(loader);

        var summary = await analyzer.SummarizeAsync();

        Assert.NotNull(summary);
        Assert.Equal(1, summary.ProjectCount);
        Assert.True(summary.TypeCount >= 4, $"типов: {summary.TypeCount}");
        Assert.True(summary.MethodCount >= 5, $"методов: {summary.MethodCount}");
        Assert.Contains("SampleProject", summary.Projects);
    }

    [Fact]
    public async Task Structure_FindsDiRegistration()
    {
        var options = CreateOptions();
        using var loader = new SolutionLoader(options);
        var analyzer = new StructureAnalyzer(loader);

        var registrations = await analyzer.FindRegistrationsAsync();

        Assert.Contains(registrations, static r =>
            r.ServiceType.Contains("IOrderRepository") && r.Lifetime == "singleton");
    }

    [Fact]
    public async Task Loader_Disabled_ReturnsNull()
    {
        var options = new RoslynOptions { Enabled = false, SolutionPath = FixtureProjectPath() };
        using var loader = new SolutionLoader(options);

        Assert.Null(await loader.LoadAsync());
    }

    [Fact]
    public async Task Loader_CachesSolutionBetweenCalls()
    {
        var options = CreateOptions();
        using var loader = new SolutionLoader(options);

        var first = await loader.LoadAsync();
        var second = await loader.LoadAsync();

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task Loader_InvalidateCache_ReloadsSolution()
    {
        var options = CreateOptions();
        using var loader = new SolutionLoader(options);

        var first = await loader.LoadAsync();
        loader.InvalidateCache();
        var second = await loader.LoadAsync();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }
}
