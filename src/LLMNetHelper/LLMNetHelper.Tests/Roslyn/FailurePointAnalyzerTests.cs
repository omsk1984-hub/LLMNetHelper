using LLMNetHelper.Abstractions;
using LLMNetHelper.Roslyn;

namespace LLMNetHelper.Tests.Roslyn;

/// <summary>
/// Тесты анализатора проблемных мест на фикстуре-проекте
/// <c>Fixtures/Roslyn/SampleProject</c>.
/// </summary>
public class FailurePointAnalyzerTests
{
    private static string FixtureProjectPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);

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

    [Fact]
    public async Task Analyze_EmptyDocuments_ReturnsEmpty()
    {
        var analyzer = new FailurePointAnalyzer();
        var documents = Array.Empty<SourceDocument>();

        var points = await analyzer.AnalyzeAsync(documents);

        Assert.Empty(points);
    }

    [Fact]
    public async Task Analyze_FindsEmptyCatch()
    {
        var code = @"
using System;
public class OrderProcessor
{
    public void ProcessOrderAsync()
    {
        try
        {
            DoWork();
        }
        catch { }
    }
    private void DoWork() { }
}";
        var analyzer = new FailurePointAnalyzer();
        var documents = new[] { new SourceDocument("OrderProcessor.cs", code) };

        var points = await analyzer.AnalyzeAsync(documents);

        Assert.Contains(points, static p => p.Type == "empty-catch");
    }

    [Fact]
    public async Task Analyze_FindsCatchExceptionWithoutLogging()
    {
        var code = @"
using System;
public class OrderProcessor
{
    public void SaveOrder()
    {
        try
        {
            DoWork();
        }
        catch (Exception)
        {
            Console.WriteLine(""error"");
        }
    }
    private void DoWork() { }
}";
        var analyzer = new FailurePointAnalyzer();
        var documents = new[] { new SourceDocument("OrderProcessor.cs", code) };

        var points = await analyzer.AnalyzeAsync(documents);

        Assert.Contains(points, static p => p.Type == "catch-without-log");
    }

    [Fact]
    public async Task Analyze_FindsThrowNull()
    {
        var code = @"
using System;
public class OrderProcessor
{
    public void BadThrow()
    {
        throw null!;
    }
}";
        var analyzer = new FailurePointAnalyzer();
        var documents = new[] { new SourceDocument("OrderProcessor.cs", code) };

        var points = await analyzer.AnalyzeAsync(documents);

        Assert.Contains(points, static p => p.Type == "throw-null");
    }

    [Fact]
    public async Task Analyze_ReportsContainingMember()
    {
        var code = @"
using System;
public class OrderProcessor
{
    public void ProcessOrderAsync()
    {
        try { }
        catch { }
    }
}";
        var analyzer = new FailurePointAnalyzer();
        var documents = new[] { new SourceDocument("OrderProcessor.cs", code) };

        var points = await analyzer.AnalyzeAsync(documents);

        Assert.Contains(points, static p => p.Member == "ProcessOrderAsync");
    }

    [Fact]
    public async Task Analyze_FileFilter_WorksWithFilter()
    {
        var code = @"
using System;
public class DependencyInjection
{
    public void Register() { }
}";
        var analyzer = new FailurePointAnalyzer();
        var documents = new[] { new SourceDocument("DependencyInjection.cs", code) };

        var points = await analyzer.AnalyzeAsync(documents);

        Assert.All(points, static p => p.FileName.Contains("DependencyInjection.cs"));
    }
}
