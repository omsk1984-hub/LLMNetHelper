#if SK_ENABLED

using LLMNetHelper.Abstractions;
using LLMNetHelper.Agent.Orchestration;
using LLMNetHelper.Agent.Tools;
using LLMCore.Orchestration.SemanticKernel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMCore.Orchestration.SemanticKernel.Tests;

/// <summary>
/// Паритетные тесты: нативный и SK-оркестраторы на одинаковом ответе модели
/// должны давать эквивалентный <see cref="Diagnosis"/>.
/// <para>
/// Это ключевая проверка гибридной архитектуры: переключение
/// <c>Agent:Orchestration:Orchestrator</c> не должно менять поведение агента.
/// </para>
/// </summary>
public class SemanticKernelOrchestratorTests
{
    /// <summary>
    /// Фейковый чат-клиент с очередью ответов.
    /// </summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<string> _responses = new();

        public ScriptedChatClient(params string[] responses)
        {
            foreach (var r in responses)
            {
                _responses.Enqueue(r);
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var text = _responses.Count > 0 ? _responses.Dequeue() : "";
            var message = new ChatMessage(ChatRole.Assistant, text);

            var response = new ChatResponse(message)
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = "scripted"
            };

            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Инструмент-заглушка.
    /// </summary>
    private sealed class StubTool(string name) : IAgentTool
    {
        public string Name => name;

        public string Description => "заглушка";

        public string ParametersJsonSchema => """{"type":"object"}""";

        public Task<string> InvokeAsync(string parametersJson, CancellationToken ct = default)
            => Task.FromResult("ok");
    }

    private static AgentContext Context() => new(
        RunId: 1,
        WindowFrom: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        WindowTo: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        SnapshotText: "# Сводка",
        ContextDocuments: [],
        CodeStructureSummary: null);

    private const string WarningJson = """
        {
          "verdict": "WARNING",
          "summary": "тестовый инцидент",
          "findings": [],
          "hypotheses": [
            { "root_cause": "таймаут", "confidence": "high", "evidence": "лог", "references": ["a:1"] }
          ],
          "questions_for_owner": ["проверить?"]
        }
        """;

    private static NativeAgentOrchestrator CreateNative(IChatClient chat, IToolRegistry registry) =>
        new(chat, registry, new AgentOrchestrationOptions(), NullLogger<NativeAgentOrchestrator>.Instance);

    private static SemanticKernelAgentOrchestrator CreateSk(IChatClient chat, IToolRegistry registry) =>
        new(chat, registry, new AgentOrchestrationOptions(), NullLoggerFactory.Instance);

    [Fact]
    public async Task Diagnose_ParityWithNative_SameVerdict()
    {
        var registry = new ToolRegistry([new StubTool("docs_search")]);

        var native = CreateNative(new ScriptedChatClient(WarningJson), registry);
        var sk = CreateSk(new ScriptedChatClient(WarningJson), registry);

        var nativeDiagnosis = await native.DiagnoseAsync(Context());
        var skDiagnosis = await sk.DiagnoseAsync(Context());

        Assert.Equal(nativeDiagnosis.Verdict, skDiagnosis.Verdict);
        Assert.Equal(nativeDiagnosis.Summary, skDiagnosis.Summary);
        Assert.Equal(nativeDiagnosis.Hypotheses.Count, skDiagnosis.Hypotheses.Count);
    }

    [Fact]
    public async Task Diagnose_ParityWithNative_SameHypotheses()
    {
        var registry = new ToolRegistry([]);

        var native = CreateNative(new ScriptedChatClient(WarningJson), registry);
        var sk = CreateSk(new ScriptedChatClient(WarningJson), registry);

        var nativeDiagnosis = await native.DiagnoseAsync(Context());
        var skDiagnosis = await sk.DiagnoseAsync(Context());

        Assert.Equal(
            nativeDiagnosis.Hypotheses[0].RootCause,
            skDiagnosis.Hypotheses[0].RootCause);
        Assert.Equal(
            nativeDiagnosis.QuestionsForOwner.Count,
            skDiagnosis.QuestionsForOwner.Count);
    }

    [Fact]
    public void ProviderNames_AreDifferent()
    {
        var registry = new ToolRegistry([]);

        var native = CreateNative(new ScriptedChatClient(), registry);
        var sk = CreateSk(new ScriptedChatClient(), registry);

        Assert.Equal("native", native.ProviderName);
        Assert.Equal("semantic-kernel", sk.ProviderName);
    }

    [Fact]
    public void Kernel_RegistersAllToolsAsFunctions()
    {
        var registry = new ToolRegistry([
            new StubTool("docs_search"),
            new StubTool("code_search_symbol")
        ]);

        var sk = CreateSk(new ScriptedChatClient(), registry);

        Assert.Contains(sk.RegisteredFunctions, static f => f.Contains("docs_search"));
        Assert.Contains(sk.RegisteredFunctions, static f => f.Contains("code_search_symbol"));
    }

    [Fact]
    public async Task PlanFix_ParityWithNative()
    {
        const string planJson = """
            {
              "rationale": "правка",
              "changes": [
                { "file_path": "A.cs", "symbol_name": "Run", "change_kind": "ReplaceMethodBody",
                  "new_code": "return 1;", "explanation": "почему" }
              ],
              "risks": ["риск"],
              "requires_manual_review": true
            }
            """;

        var registry = new ToolRegistry([]);

        var native = CreateNative(new ScriptedChatClient(planJson), registry);
        var sk = CreateSk(new ScriptedChatClient(planJson), registry);

        var diagnosis = new Diagnosis(Verdict.Warning, "s", [], [], [], 0, "native");

        var nativePlan = await native.PlanFixAsync(diagnosis, Context());
        var skPlan = await sk.PlanFixAsync(diagnosis, Context());

        Assert.Equal(nativePlan.Changes.Count, skPlan.Changes.Count);
        Assert.Equal(nativePlan.Changes[0].SymbolName, skPlan.Changes[0].SymbolName);
        Assert.Equal(nativePlan.RequiresManualReview, skPlan.RequiresManualReview);
    }

    [Fact]
    public void SkPluginAdapter_CreatesFunctionWithToolMetadata()
    {
        var tool = new StubTool("my_tool");

        var function = SkPluginAdapter.ToKernelFunction(tool);

        Assert.Equal("my_tool", function.Name);
        Assert.Equal("заглушка", function.Description);
    }

    [Fact]
    public void SkPluginAdapter_CreatesAllFunctions()
    {
        var registry = new ToolRegistry([new StubTool("a"), new StubTool("b")]);

        var functions = SkPluginAdapter.ToKernelFunctions(registry);

        Assert.Equal(2, functions.Count);
        Assert.Contains(functions, static f => f.Name == "a");
        Assert.Contains(functions, static f => f.Name == "b");
    }

    [Fact]
    public async Task Diagnose_EmptyResponse_ReturnsUnknownOnBoth()
    {
        var registry = new ToolRegistry([]);

        var native = CreateNative(new ScriptedChatClient(""), registry);
        var sk = CreateSk(new ScriptedChatClient(""), registry);

        var nativeDiagnosis = await native.DiagnoseAsync(Context());
        var skDiagnosis = await sk.DiagnoseAsync(Context());

        Assert.Equal(Verdict.Unknown, nativeDiagnosis.Verdict);
        Assert.Equal(Verdict.Unknown, skDiagnosis.Verdict);
    }
}

#endif
