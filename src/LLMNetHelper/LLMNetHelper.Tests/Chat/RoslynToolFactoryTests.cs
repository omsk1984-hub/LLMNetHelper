using LLMNetHelper.Abstractions;
using LLMNetHelper.Chat;
using LLMNetHelper.Chat.Tools;
using LLMNetHelper.GitLab;
using LLMNetHelper.Roslyn;
using LLMCore.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using LlmChatOptions = LLMNetHelper.Chat.ChatOptions;
using MeaiChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace LLMNetHelper.Tests.Chat;

/// <summary>
/// Тесты фабрики Roslyn-инструментов (план 22, этап 2.8):
/// <see cref="ChatSession"/> обязан получать Roslyn-инструменты через
/// абстракцию <see cref="IRoslynToolFactory"/>, а не через 5 конкретных
/// Roslyn-классов в конструкторе. Фабрика возвращает список инструментов
/// либо пусто (слой выключен / не выбран репозиторий / решение не найдено).
/// </summary>
public class RoslynToolFactoryTests : IDisposable
{
    private readonly string _root;

    public RoslynToolFactoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"repo_factory_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

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
                public void Run()
                {
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

    /// <summary>
    /// Фабрика с включённым Roslyn и выбранным /repo даёт три инструмента.
    /// </summary>
    [Fact]
    public void CreateTools_RepoWithSolution_ReturnsThreeTools()
    {
        using var fx = new RoslynFixture();
        var factory = new RoslynToolFactory(fx.Loader, fx.Search, fx.Graph, fx.Analyzer,
            new RoslynOptions { Enabled = true });
        var scope = CreateScope("sample");

        var tools = factory.CreateTools(scope);

        Assert.Equal(3, tools.Count);
        Assert.Contains(tools, t => t.Name == "repo_failure_points");
        Assert.Contains(tools, t => t.Name == "repo_call_graph");
        Assert.Contains(tools, t => t.Name == "repo_symbol_search");
        Assert.True(factory.IsAvailable(scope));
    }

    /// <summary>
    /// Фабрика без выбранного /repo даёт пусто (требование «только /repo», план 21).
    /// </summary>
    [Fact]
    public void CreateTools_NoRepo_ReturnsEmpty()
    {
        using var fx = new RoslynFixture();
        var factory = new RoslynToolFactory(fx.Loader, fx.Search, fx.Graph, fx.Analyzer,
            new RoslynOptions { Enabled = true });
        var scope = CreateScope(repo: null);

        Assert.Empty(factory.CreateTools(scope));
        Assert.False(factory.IsAvailable(scope));
    }

    /// <summary>
    /// Отключённый слой Roslyn — пусто, даже при выбранном /repo.
    /// </summary>
    [Fact]
    public void CreateTools_DisabledLayer_ReturnsEmpty()
    {
        using var fx = new RoslynFixture();
        var factory = new RoslynToolFactory(fx.Loader, fx.Search, fx.Graph, fx.Analyzer,
            new RoslynOptions { Enabled = false });
        var scope = CreateScope("sample");

        Assert.Empty(factory.CreateTools(scope));
        Assert.False(factory.IsAvailable(scope));
    }

    /// <summary>
    /// ChatSession с фабрикой-заглушкой: Roslyn-инструменты берутся из фабрики
    /// (контракт), а не из конкретных классов — проверка устранения DIP-нарушения.
    /// </summary>
    [Fact]
    public void ChatSession_UsesFactoryAbstraction()
    {
        var scope = CreateScope("sample");
        var session = new ChatSession(
            new NoopChatClient(),
            scope,
            new LlmChatOptions(),
            new LlmOptions { Endpoint = "http://x", ApiKey = "k", Model = "m" },
            NullLoggerFactory.Instance,
            new StringReader(""),
            TextWriter.Null,
            new StubRoslynToolFactory());

        // Заглушка сообщает: доступно => сессия обязана это отразить.
        Assert.True(session.RoslynAvailable);
    }

    /// <summary>
    /// ChatSession без фабрики (null) — Roslyn недоступен (прежнее поведение).
    /// </summary>
    [Fact]
    public void ChatSession_NoFactory_RoslynUnavailable()
    {
        var scope = CreateScope("sample");
        var session = new ChatSession(
            new NoopChatClient(),
            scope,
            new LlmChatOptions(),
            new LlmOptions { Endpoint = "http://x", ApiKey = "k", Model = "m" },
            NullLoggerFactory.Instance,
            new StringReader(""),
            TextWriter.Null);

        Assert.False(session.RoslynAvailable);
    }

    private ChatScope CreateScope(string? repo)
    {
        var scope = new ChatScope(
            new GitLabOptions { CloneRoot = _root },
            new LlmChatOptions());
        if (repo is not null)
        {
            scope.TrySetRepo(repo, out _);
        }
        return scope;
    }

    /// <summary>Рослин-сервисы с общим загрузчиком (паттерн RepoRoslynToolsTests).</summary>
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

    /// <summary>Фабрика-заглушка: всегда «доступна», инструменты — маркерные.</summary>
    private sealed class StubRoslynToolFactory : IRoslynToolFactory
    {
        public bool IsAvailable(ChatScope scope) => true;

        public IReadOnlyList<IAgentTool> CreateTools(ChatScope scope) =>
            [new RepoFailurePointsToolStub()];

        public string DescribeStatus(ChatScope scope) => "stub";

        private sealed class RepoFailurePointsToolStub : IAgentTool
        {
            public string Name => "repo_failure_points";
            public string Description => "stub";
            public string ParametersJsonSchema => """{"type":"object"}""";

            public Task<string> InvokeAsync(string parametersJson, CancellationToken ct = default) =>
                Task.FromResult("");
        }
    }

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

        public void Dispose()
        {
        }
    }
}
