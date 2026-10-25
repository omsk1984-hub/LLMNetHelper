using LLMNetHelper.Abstractions;
using LLMNetHelper.Agent.AgentLoop;
using LLMNetHelper.Agent.Tools;
using Microsoft.Extensions.AI;

namespace LLMNetHelper.Tests.Agent;

/// <summary>
/// Паритетные тесты общего ReAct-движка (план 22, этап 3.4) на стратегии
/// диагноза: финализация диагнозом, цикл tool-call → финал, лимиты шагов/вызовов,
/// сбой LLM и пустой ответ. Механика движка здесь проверяется напрямую — те же
/// сценарии, что покрывают <c>NativeAgentOrchestratorTests</c> на уровне оркестратора.
/// </summary>
public class ReActLoopEngineTests
{
    private sealed class ScriptedChatClient(params string[] responses) : IChatClient
    {
        private readonly Queue<string> _responses = new(responses);

        public int CallCount { get; private set; }
        public List<IList<ChatMessage>> ReceivedMessages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ReceivedMessages.Add(messages.ToList());

            var text = _responses.Count > 0 ? _responses.Dequeue() : "";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = "engine-test"
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose()
        {
        }
    }

    private sealed class CountingInvoker(
        string toolName,
        int resultChars,
        bool succeeded = true) : IToolInvoker
    {
        public int Calls { get; private set; }

        public Task<ToolCallRecord> InvokeAsync(string name, string argumentsJson, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new ToolCallRecord(
                toolName, argumentsJson, resultChars, 5, succeeded));
        }

        public void Reset()
        {
            Calls = 0;
        }
    }

    private static readonly ReActOptions Options = new(MaxSteps: 3, ToolCallLimit: 2);

    private static DiagnosisReActStrategy Strategy(IToolInvoker invoker) => new(invoker);

    private static IReadOnlyList<ChatMessage> History() => [new ChatMessage(ChatRole.System, "system")];

    [Fact]
    public async Task RunAsync_JsonDiagnosis_FinishesAtOnce()
    {
        var chat = new ScriptedChatClient("""{"verdict":"OK","summary":"всё хорошо"}""");
        var engine = new ReActLoopEngine();

        var outcome = await engine.RunAsync(
            chat, History(), Strategy(new CountingInvoker("x", 1)), Options, null, CancellationToken.None);

        Assert.Equal(Verdict.Ok, outcome.Diagnosis!.Verdict);
        Assert.Equal("всё хорошо", outcome.Diagnosis.Summary);
        Assert.Equal(0, outcome.ToolCalls);
        Assert.Equal(1, chat.CallCount);
    }

    [Fact]
    public async Task RunAsync_ToolCallThenDiagnosis_InvokesAndFinishes()
    {
        var chat = new ScriptedChatClient(
            """{"tool":"docs_search","arguments":{"query":"timeout"}}""",
            """{"verdict":"OK","summary":"после чтения всё ясно"}""");
        var invoker = new CountingInvoker("docs_search", 16);
        var engine = new ReActLoopEngine();

        var outcome = await engine.RunAsync(
            chat, History(), Strategy(invoker), Options, null, CancellationToken.None);

        Assert.Equal(1, invoker.Calls);
        Assert.Equal(Verdict.Ok, outcome.Diagnosis!.Verdict);
        Assert.Equal(1, outcome.ToolCalls);
        Assert.Equal(2, chat.CallCount);

        // История второго запроса содержит подпись с числом символов, не полный текст.
        var second = chat.ReceivedMessages[1];
        Assert.Contains(second, m => (m.Text ?? "").Contains("успешно, 16 символов", StringComparison.Ordinal));
        Assert.DoesNotContain(second, m => (m.Text ?? "").Contains("найдено: runbook", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_FreeText_FallsBackToUnknownDiagnosis()
    {
        var chat = new ScriptedChatClient("Похоже, проблема во времени ожидания.");
        var engine = new ReActLoopEngine();

        var outcome = await engine.RunAsync(
            chat, History(), Strategy(new CountingInvoker("x", 1)), Options, null, CancellationToken.None);

        Assert.Equal(Verdict.Unknown, outcome.Diagnosis!.Verdict);
        Assert.Contains("проблема во времени ожидания", outcome.Diagnosis.Summary);
        Assert.NotEmpty(outcome.Diagnosis.QuestionsForOwner);
    }

    [Fact]
    public async Task RunAsync_StepLimit_ReturnsUnknown()
    {
        // ToolCallLimit больше MaxSteps, чтобы первым сработал именно лимит шагов.
        var toolCall = """{"tool":"docs_search","arguments":{"query":"x"}}""";
        var chat = new ScriptedChatClient(Enumerable.Repeat(toolCall, 10).ToArray());
        var engine = new ReActLoopEngine();

        var outcome = await engine.RunAsync(
            chat, History(), Strategy(new CountingInvoker("docs_search", 2)), new ReActOptions(3, 30),
            null, CancellationToken.None);

        Assert.Equal(Verdict.Unknown, outcome.Diagnosis!.Verdict);
        Assert.Contains("лимит шагов", outcome.Diagnosis.Summary, StringComparison.Ordinal);
        Assert.Equal(3, chat.CallCount);
    }

    [Fact]
    public async Task RunAsync_ToolCallLimit_ReturnsUnknown()
    {
        var toolCall = """{"tool":"docs_search","arguments":{"query":"x"}}""";
        var chat = new ScriptedChatClient(Enumerable.Repeat(toolCall, 10).ToArray());
        var engine = new ReActLoopEngine();

        var outcome = await engine.RunAsync(
            chat, History(), Strategy(new CountingInvoker("docs_search", 2)), new ReActOptions(10, 2),
            null, CancellationToken.None);

        Assert.Equal(Verdict.Unknown, outcome.Diagnosis!.Verdict);
        Assert.Contains("лимит вызовов инструментов", outcome.Diagnosis.Summary, StringComparison.Ordinal);
        Assert.Equal(2, outcome.ToolCalls);
    }

    [Fact]
    public async Task RunAsync_EmptyResponse_ReturnsUnknown()
    {
        var chat = new ScriptedChatClient("");
        var engine = new ReActLoopEngine();

        var outcome = await engine.RunAsync(
            chat, History(), Strategy(new CountingInvoker("x", 1)), Options, null, CancellationToken.None);

        Assert.Equal(Verdict.Unknown, outcome.Diagnosis!.Verdict);
        Assert.Contains("пустой ответ", outcome.Diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_UnknownTool_ContinuesLoop()
    {
        var chat = new ScriptedChatClient(
            """{"tool":"rm_rf","arguments":{}}""",
            """{"verdict":"WARNING","summary":"инструмент недоступен"}""");
        var engine = new ReActLoopEngine();

        var outcome = await engine.RunAsync(
            chat, History(), Strategy(new CountingInvoker("x", 1)), Options, null, CancellationToken.None);

        Assert.Equal(Verdict.Warning, outcome.Diagnosis!.Verdict);
        Assert.Equal(1, outcome.ToolCalls);
    }

    [Fact]
    public async Task RunAsync_LlmFailure_ReturnsUnknownReason()
    {
        var engine = new ReActLoopEngine();

        var outcome = await engine.RunAsync(
            new ThrowingChatClient(), History(), Strategy(new CountingInvoker("x", 1)),
            Options, null, CancellationToken.None);

        Assert.Equal(Verdict.Unknown, outcome.Diagnosis!.Verdict);
        Assert.Contains("Сбой LLM", outcome.Diagnosis.Summary, StringComparison.Ordinal);
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("транспорт недоступен");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose()
        {
        }
    }
}