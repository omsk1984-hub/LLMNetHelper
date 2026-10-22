using ArtasTools.MonitorAgent.Roslyn;

namespace ArtasTools.MonitorAgent.Tests.Roslyn;

/// <summary>
/// Тесты анализатора проблемных мест на фикстуре-проекте
/// <c>Fixtures/Roslyn/SampleProject</c>.
/// </summary>
public class FailurePointAnalyzerTests
{
    private static string FixtureProjectPath()
    {
        // Тесты запускаются из bin/Debug/net10.0 — поднимаемся до каталога тестового проекта.
        var baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ArtasTools.MonitorAgent.Tests.csproj")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Не найден каталог тестового проекта");
        }

        return Path.Combine(dir.FullName, "Fixtures", "Roslyn", "SampleProject", "SampleProject.csproj");
    }

    private static (SolutionLoader Loader, FailurePointAnalyzer Analyzer) Create()
    {
        var options = new RoslynOptions
        {
            Enabled = true,
            SolutionPath = FixtureProjectPath(),
            CompilationCacheSeconds = 60
        };

        var loader = new SolutionLoader(options);
        return (loader, new FailurePointAnalyzer(loader, options));
    }

    [Fact]
    public async Task Analyze_Disabled_ReturnsEmpty()
    {
        var options = new RoslynOptions { Enabled = false, SolutionPath = FixtureProjectPath() };
        using var loader = new SolutionLoader(options);
        var analyzer = new FailurePointAnalyzer(loader, options);

        Assert.Empty(await analyzer.AnalyzeAsync());
    }

    [Fact]
    public async Task Analyze_MissingSolution_ReturnsEmpty()
    {
        var options = new RoslynOptions
        {
            Enabled = true,
            SolutionPath = Path.Combine(Path.GetTempPath(), "no-such-solution.slnx")
        };

        using var loader = new SolutionLoader(options);
        var analyzer = new FailurePointAnalyzer(loader, options);

        Assert.Empty(await analyzer.AnalyzeAsync());
    }

    [Fact]
    public async Task Analyze_FindsEmptyCatch()
    {
        var (loader, analyzer) = Create();
        using (loader)
        {
            var points = await analyzer.AnalyzeAsync("OrderProcessor.cs");

            Assert.Contains(points, static p => p.Kind == "empty-catch");
        }
    }

    [Fact]
    public async Task Analyze_FindsCatchExceptionWithoutLogging()
    {
        var (loader, analyzer) = Create();
        using (loader)
        {
            var points = await analyzer.AnalyzeAsync("OrderProcessor.cs");

            Assert.Contains(points, static p => p.Kind == "catch-exception");
        }
    }

    [Fact]
    public async Task Analyze_FindsThrowNull()
    {
        var (loader, analyzer) = Create();
        using (loader)
        {
            var points = await analyzer.AnalyzeAsync("OrderProcessor.cs");

            Assert.Contains(points, static p => p.Kind == "throw-null");
        }
    }

    [Fact]
    public async Task Analyze_ReportsContainingMember()
    {
        var (loader, analyzer) = Create();
        using (loader)
        {
            var points = await analyzer.AnalyzeAsync("OrderProcessor.cs");

            Assert.Contains(points, static p => p.ContainingMember == "ProcessOrderAsync");
            Assert.Contains(points, static p => p.ContainingMember == "SaveOrder");
        }
    }

    [Fact]
    public async Task Analyze_FileFilter_ExcludesOtherFiles()
    {
        var (loader, analyzer) = Create();
        using (loader)
        {
            var points = await analyzer.AnalyzeAsync("DependencyInjection.cs");

            Assert.All(points, static p => Assert.Contains("DependencyInjection.cs", p.FileName));
        }
    }
}
