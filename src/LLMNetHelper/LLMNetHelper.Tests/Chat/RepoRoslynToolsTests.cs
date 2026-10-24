using LLMNetHelper.Chat;
using LLMNetHelper.Chat.Tools;
using LLMNetHelper.GitLab;
using LLMNetHelper.Roslyn;
using LLMCore.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
// Алиас: LLMNetHelper.Chat.ChatOptions конфликтует с Microsoft.Extensions.AI.ChatOptions,
// который нужен стабам IChatClient (паттерн — ChatAgentLoopTests).
using LlmChatOptions = LLMNetHelper.Chat.ChatOptions;
using MeaiChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace LLMNetHelper.Tests.Chat;

/// <summary>
/// Тесты Roslyn-инструментов чата (план 21) на временном репозитории с
/// самодостаточным проектом. Синтаксический анализ не требует restore и сети —
/// тесты офлайн. Требование «только /repo» проверяется отдельно.
/// </summary>
public class RepoRoslynToolsTests : IDisposable
{
    private readonly string _root;

    public RepoRoslynToolsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"repo_roslyn_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        // Репозиторий "sample" с проектом и проблемным кодом.
        Write("sample/Sample.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);

        Write("sample/Problem.cs", """
            namespace Sample;

            public sealed class OrderProcessor
            {
                public int Process()
                {
                    try
                    {
                        Run();
                        return 1;
                    }
                    catch (Exception)
                    {
                        // catch с оператором, но без логирования -> catch-without-log
                        return 0;
                    }
                }

                public void Run()
                {
                }

                public void Save()
                {
                    try
                    {
                    }
                    catch
                    {
                        // пустой catch -> empty-catch
                    }
                }
            }
            """);
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

    /// <summary>Рослин-сервисы с общим загрузчиком; освобождает workspace.</summary>
    private sealed class RoslynFixture : IDisposable
    {
        public SolutionLoader Loader { get; }
        public CodeSearchService Search { get; }
        public CallGraphService Graph { get; }
        public FailurePointAnalyzer Analyzer { get; }

        public RoslynFixture()
        {
            var options = new RoslynOptions { Enabled = true, CompilationCacheSeconds = 60 };
            Loader = new SolutionLoader(options);
            Search = new CodeSearchService(Loader, options);
            Graph = new CallGraphService(Loader, options);
            Analyzer = new FailurePointAnalyzer();
        }

        public void Dispose() => Loader.Dispose();
    }

    private static ChatScope CreateScope(string root, string? repo)
    {
        var scope = new ChatScope(new GitLabOptions { CloneRoot = root }, new LlmChatOptions());
        if (repo is not null)
        {
            scope.TrySetRepo(repo, out _);
        }
        return scope;
    }

    // --- Инструменты: требование «только /repo» -----------------------------

    [Fact]
    public async Task FailurePoints_WithoutRepo_ReturnsRepoHint()
    {
        using var fx = new RoslynFixture();
        var scope = CreateScope(_root, repo: null);
        var tool = new RepoFailurePointsTool(scope, fx.Loader, fx.Analyzer);

        var result = await tool.InvokeAsync("{}");

        Assert.Contains("/repo", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailurePoints_WithRepo_FindsBothKinds()
    {
        using var fx = new RoslynFixture();
        var scope = CreateScope(_root, "sample");
        var tool = new RepoFailurePointsTool(scope, fx.Loader, fx.Analyzer);

        var result = await tool.InvokeAsync("{}");

        Assert.Contains("catch-without-log", result, StringComparison.Ordinal);
        Assert.Contains("empty-catch", result, StringComparison.Ordinal);
        // Путь относительный внутри области (guard), а не абсолютный.
        Assert.DoesNotContain(_root, result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SymbolSearch_WithRepo_FindsClass()
    {
        using var fx = new RoslynFixture();
        var scope = CreateScope(_root, "sample");
        var tool = new RepoSymbolSearchTool(scope, fx.Search);

        var result = await tool.InvokeAsync("""{"name":"OrderProcessor"}""");

        Assert.Contains("class", result, StringComparison.Ordinal);
        Assert.Contains("OrderProcessor", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SymbolSearch_WithoutRepo_ReturnsRepoHint()
    {
        using var fx = new RoslynFixture();
        var scope = CreateScope(_root, repo: null);
        var tool = new RepoSymbolSearchTool(scope, fx.Search);

        var result = await tool.InvokeAsync("""{"name":"OrderProcessor"}""");

        Assert.Contains("/repo", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallGraph_WithRepo_FindsCallerOfRun()
    {
        using var fx = new RoslynFixture();
        var scope = CreateScope(_root, "sample");
        var tool = new RepoCallGraphTool(scope, fx.Graph);

        var result = await tool.InvokeAsync("""{"method":"Run","direction":"callers"}""");

        Assert.Contains("Process", result, StringComparison.Ordinal);
    }

    // --- RoslynAvailable: динамический реестр ChatSession -------------------

    /// <summary>Минимальный фейк IChatClient (в этих тестах не вызывается).</summary>
    private sealed class NoopChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            MeaiChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            MeaiChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static ChatSession CreateSession(ChatScope scope, RoslynFixture fx, RoslynOptions? roslynOptions) =>
        new(
            new NoopChatClient(),
            scope,
            new LlmChatOptions(),
            new LlmOptions { Endpoint = "http://x", ApiKey = "k", Model = "m" },
            NullLoggerFactory.Instance,
            new StringReader(""),
            TextWriter.Null,
            fx.Loader,
            fx.Search,
            fx.Graph,
            fx.Analyzer,
            roslynOptions);

    [Fact]
    public void RoslynAvailable_DisabledLayer_False()
    {
        using var fx = new RoslynFixture();
        var scope = CreateScope(_root, "sample");
        var session = CreateSession(scope, fx, new RoslynOptions { Enabled = false });

        Assert.False(session.RoslynAvailable);
    }

    [Fact]
    public void RoslynAvailable_AllRepos_False()
    {
        using var fx = new RoslynFixture();
        var scope = CreateScope(_root, repo: null);
        var session = CreateSession(scope, fx, new RoslynOptions { Enabled = true });

        // Roslyn включён, но область — все репозитории => инструменты не регистрируются.
        Assert.False(session.RoslynAvailable);
    }

    [Fact]
    public void RoslynAvailable_EnabledAndRepoWithSolution_True()
    {
        using var fx = new RoslynFixture();
        var scope = CreateScope(_root, "sample");
        var session = CreateSession(scope, fx, new RoslynOptions { Enabled = true });

        Assert.True(session.RoslynAvailable);
    }

    [Fact]
    public void RoslynAvailable_EnabledButNoServices_False()
    {
        var scope = CreateScope(_root, "sample");
        var session = new ChatSession(
            new NoopChatClient(),
            scope,
            new LlmChatOptions(),
            new LlmOptions { Endpoint = "http://x", ApiKey = "k", Model = "m" },
            NullLoggerFactory.Instance,
            new StringReader(""),
            TextWriter.Null);

        // Roslyn-зависимости не переданы (null) => недоступны, даже если Enabled.
        Assert.False(session.RoslynAvailable);
    }
}
