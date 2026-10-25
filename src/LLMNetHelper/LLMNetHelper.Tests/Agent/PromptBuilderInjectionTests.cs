using LLMNetHelper.Abstractions;
using LLMNetHelper.Agent.Orchestration;
using LLMNetHelper.Agent.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMNetHelper.Tests.Agent;

/// <summary>
/// Тесты подменяемости сборки промптов (план 22, этап 6.1):
/// <see cref="NativeAgentOrchestrator"/> обязан получать промпты через
/// абстракцию <see cref="IAgentPromptBuilder"/>, а не вызывать статический
/// <c>AgentPromptBuilder</c> — иначе промпты нельзя ни подменить в тестах,
/// ни версионировать.
/// </summary>
public class PromptBuilderInjectionTests
{
    /// <summary>
    /// Системный и пользовательский промпты берутся из внедрённого билдера:
    /// маркерные строки обязаны попасть в сообщения, отправленные модели.
    /// </summary>
    [Fact]
    public async Task Diagnose_UsesInjectedPromptBuilder()
    {
        var builder = new StubPromptBuilder();
        var chat = new OneShotChatClient("""{"verdict":"OK","summary":"s"}""");
        var registry = new ToolRegistry([]);

        var orchestrator = new NativeAgentOrchestrator(
            chat, registry, new AgentOrchestrationOptions(),
            NullLogger<NativeAgentOrchestrator>.Instance, builder);

        await orchestrator.DiagnoseAsync(Context());

        var messages = chat.Received[0];
        Assert.Equal("stub-system", messages.Single(m => m.Role == ChatRole.System).Text);
        Assert.Equal("stub-user", messages.Single(m => m.Role == ChatRole.User).Text);
    }

    /// <summary>
    /// Этап планирования правки тоже использует внедрённый билдер (BuildPlanPrompt).
    /// </summary>
    [Fact]
    public async Task PlanFix_UsesInjectedPromptBuilder()
    {
        var builder = new StubPromptBuilder();
        var chat = new OneShotChatClient("""{"rationale":"r","changes":[]}""");
        var registry = new ToolRegistry([]);

        var orchestrator = new NativeAgentOrchestrator(
            chat, registry,
            new AgentOrchestrationOptions { EnableFixPlanning = true },
            NullLogger<NativeAgentOrchestrator>.Instance, builder);

        await orchestrator.PlanFixAsync(
            new Diagnosis(Verdict.Warning, "s", [], [], [], 0, "native"), Context());

        var messages = chat.Received[0];
        Assert.Contains("stub-plan", messages.Select(m => m.Text), StringComparer.Ordinal);
        Assert.Equal("stub-system", messages.Single(m => m.Role == ChatRole.System).Text);
    }

    /// <summary>
    /// Без внедрённого билдера оркестратор обязан работать на стандартной
    /// реализации (обратная совместимость: все существующие вызовы живы).
    /// </summary>
    [Fact]
    public async Task Diagnose_WithoutInjectedBuilder_UsesDefault()
    {
        var chat = new OneShotChatClient("""{"verdict":"OK","summary":"s"}""");
        var registry = new ToolRegistry([]);

        var orchestrator = new NativeAgentOrchestrator(
            chat, registry, new AgentOrchestrationOptions(),
            NullLogger<NativeAgentOrchestrator>.Instance);

        var diagnosis = await orchestrator.DiagnoseAsync(Context());

        Assert.Equal(Verdict.Ok, diagnosis.Verdict);
        Assert.Contains("SRE", chat.Received[0].Single(m => m.Role == ChatRole.System).Text,
            StringComparison.Ordinal);
    }

    private static AgentContext Context() => new(
        RunId: 1,
        WindowFrom: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        WindowTo: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        SnapshotText: "# Сводка\nошибок: 0",
        ContextDocuments: [],
        CodeStructureSummary: null);

    /// <summary>Билдер-заглушка с маркерными строками.</summary>
    private sealed class StubPromptBuilder : IAgentPromptBuilder
    {
        public string BuildSystemPrompt(string toolDescriptions, string? domainContext = null) =>
            "stub-system";

        public string BuildUserPrompt(AgentContext context) => "stub-user";

        public string BuildPlanPrompt(Diagnosis diagnosis, AgentContext context) => "stub-plan";
    }

    /// <summary>Мок IChatClient: отдаёт один ответ, запоминает сообщения.</summary>
    private sealed class OneShotChatClient(string responseText) : IChatClient
    {
        public List<IList<ChatMessage>> Received { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Received.Add(messages.ToList());

            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, responseText))
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
