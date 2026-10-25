using LLMNetHelper.Abstractions;
using LLMNetHelper.Agent.Orchestration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LLMNetHelper.Tests.Agent;

/// <summary>
/// Тесты сегрегации реестра инструментов (план 22, этап 2.6):
/// <see cref="NativeAgentOrchestrator"/> обязан зависеть от абстракций
/// <c>IToolCatalog</c> (запросы + описание для промпта) и <c>IToolInvoker</c>
/// (вызов с аудитом), а не от конкретного <c>ToolRegistry</c>.
/// Проверка типа <c>_registry is Tools.ToolRegistry</c> устранена:
/// сторонняя реализация каталога обязана давать своё описание промпту.
/// </summary>
public class ToolCatalogSegregationTests
{
    /// <summary>
    /// Сторонний каталог (не ToolRegistry): оркестратор обязан взять
    /// <c>DescribeForPrompt()</c> из контракта, а не собирать описание
    /// самому по <c>Tools</c> (прежняя fallback-ветка проверки типа).
    /// </summary>
    [Fact]
    public async Task Diagnose_ExternalCatalog_UsesContractDescribeForPrompt()
    {
        var catalog = new ExternalCatalog();
        var invoker = new ExternalInvoker();
        var chat = new CapturingChatClient("""{"verdict":"OK","summary":"s"}""");

        var orchestrator = new NativeAgentOrchestrator(
            chat, catalog, invoker, new AgentOrchestrationOptions(),
            NullLogger<NativeAgentOrchestrator>.Instance);

        await orchestrator.DiagnoseAsync(Context());

        // Системный промпт обязан содержать строку из DescribeForPrompt() контракта.
        var systemPrompt = chat.Received[0].Single(m => m.Role == ChatRole.System).Text;
        Assert.Contains("custom-catalog-description", systemPrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Вызов инструмента идёт через IToolInvoker (аудит), а не через конкретный реестр.
    /// </summary>
    [Fact]
    public async Task Diagnose_UsesInvokerContract_ForToolCalls()
    {
        var catalog = new ExternalCatalog();
        var invoker = new ExternalInvoker();
        var chat = new CapturingChatClient(
            """{"tool":"external_tool","arguments":{}}""",
            """{"verdict":"OK","summary":"s"}""");

        var orchestrator = new NativeAgentOrchestrator(
            chat, catalog, invoker, new AgentOrchestrationOptions(),
            NullLogger<NativeAgentOrchestrator>.Instance);

        var diagnosis = await orchestrator.DiagnoseAsync(Context());

        Assert.Equal(1, invoker.InvokeCount);
        Assert.Equal(1, diagnosis.ToolCallCount);
    }

    private static AgentContext Context() => new(
        RunId: 1,
        WindowFrom: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        WindowTo: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        SnapshotText: "# Сводка",
        ContextDocuments: [],
        CodeStructureSummary: null);

    /// <summary>
    /// Каталог-заглушка: описание — маркер, по которому проверяется,
    /// что оркестратор использует контракт, а не сборку по Tools.
    /// </summary>
    private sealed class ExternalCatalog : IToolCatalog
    {
        public IReadOnlyList<IAgentTool> Tools => [new ExternalTool()];

        public IAgentTool? Find(string name) =>
            name == "external_tool" ? new ExternalTool() : null;

        public string DescribeForPrompt() => "- `external_tool`: custom-catalog-description";
    }

    private sealed class ExternalInvoker : IToolInvoker
    {
        public int InvokeCount { get; private set; }

        public Task<ToolCallRecord> InvokeAsync(string toolName, string argumentsJson, CancellationToken ct = default)
        {
            InvokeCount++;
            return Task.FromResult(new ToolCallRecord(toolName, argumentsJson, 2, 1, Succeeded: true));
        }

        public void Reset()
        {
        }
    }

    private sealed class ExternalTool : IAgentTool
    {
        public string Name => "external_tool";
        public string Description => "внешний инструмент";
        public string ParametersJsonSchema => """{"type":"object"}""";

        public Task<string> InvokeAsync(string parametersJson, CancellationToken ct = default) =>
            Task.FromResult("ok");
    }

    /// <summary>
    /// Мок IChatClient: отдаёт очередь ответов и запоминает сообщения.
    /// </summary>
    private sealed class CapturingChatClient(params string[] responses) : IChatClient
    {
        private int _index;

        public List<IList<ChatMessage>> Received { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Received.Add(messages.ToList());
            var text = _index < responses.Length ? responses[_index] : responses[^1];
            _index++;

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
            {
                FinishReason = ChatFinishReason.Stop
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
