using LLMNetHelper.Abstractions;
using LLMNetHelper.Agent.Tools;
using LLMNetHelper.Chat;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
// Алиасы: LLMNetHelper.Chat.ChatOptions конфликтует с Microsoft.Extensions.AI.ChatOptions,
// который нужен стабам IChatClient.
using LlmChatOptions = LLMNetHelper.Chat.ChatOptions;
using MeaiChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace LLMNetHelper.Tests.Chat;

/// <summary>
/// Тесты ReAct-цикла чата на фейковом <see cref="IChatClient"/> (без сети).
/// Паттерн — как в <c>NativeAgentOrchestratorTests</c>.
/// </summary>
public class ChatAgentLoopTests
{
    /// <summary>Фейковый клиент: отдаёт заранее заданную очередь ответов.</summary>
    private sealed class ScriptedChatClient(params string[] responses) : IChatClient
    {
        private readonly Queue<string> _responses = new(responses);

        public int CallCount { get; private set; }

        public List<IList<ChatMessage>> ReceivedMessages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            MeaiChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ReceivedMessages.Add(messages.ToList());

            var text = _responses.Count > 0 ? _responses.Dequeue() : "";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = "scripted-model"
            });
        }

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

    /// <summary>Инструмент-заглушка, считающий вызовы.</summary>
    private sealed class CountingTool(string name, string result) : IAgentTool
    {
        public int Calls { get; private set; }

        public string Name => name;

        public string Description => "тестовый инструмент";

        public string ParametersJsonSchema => """{"type":"object"}""";

        public Task<string> InvokeAsync(string parametersJson, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private static ChatAgentLoop Create(
        IChatClient chat,
        IEnumerable<IAgentTool> tools,
        LlmChatOptions? options = null)
    {
        var registry = new ToolRegistry(tools);
        return new ChatAgentLoop(chat, registry, options ?? new LlmChatOptions(), NullLogger<ChatAgentLoop>.Instance);
    }

    [Fact]
    public async Task PlainTextAnswer_IsFinalResponse()
    {
        var chat = new ScriptedChatClient("Ответ: класс App делает X.");
        var loop = Create(chat, []);

        var answer = await loop.RunAsync([], "Что делает App?", CancellationToken.None);

        Assert.Equal("Ответ: класс App делает X.", answer);
        Assert.Equal(1, chat.CallCount);
    }

    [Fact]
    public async Task ToolCall_ExecutesTool_AndReturnsFinalText()
    {
        var chat = new ScriptedChatClient(
            """{"tool":"repo_search","arguments":{"query":"App"}}""",
            "Итоговый ответ после инструмента.");
        var tool = new CountingTool("repo_search", "Найдено: 1 фрагмент");
        var loop = Create(chat, [tool]);

        var answer = await loop.RunAsync([], "Где App?", CancellationToken.None);

        Assert.Equal("Итоговый ответ после инструмента.", answer);
        Assert.Equal(1, tool.Calls);
        Assert.Equal(2, chat.CallCount);

        // Второй запрос должен содержать результат инструмента в истории.
        var second = chat.ReceivedMessages[1];
        Assert.Contains(second, m => (m.Text ?? "").Contains("Найдено: 1 фрагмент", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownTool_RejectedButLoopContinues()
    {
        var chat = new ScriptedChatClient(
            """{"tool":"not_in_whitelist","arguments":{}}""",
            "Финал.");
        var loop = Create(chat, [new CountingTool("repo_search", "ok")]);

        var answer = await loop.RunAsync([], "вопрос", CancellationToken.None);

        Assert.Equal("Финал.", answer);
        var second = chat.ReceivedMessages[1];
        Assert.Contains(second, m => (m.Text ?? "").Contains("не выполнен", StringComparison.Ordinal)
            || (m.Text ?? "").Contains("белом списке", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StepLimit_BreaksLoop()
    {
        // Модель бесконечно просит инструмент — цикл обрывается по MaxSteps.
        var toolCall = """{"tool":"repo_search","arguments":{"query":"x"}}""";
        var chat = new ScriptedChatClient(Enumerable.Repeat(toolCall, 10).ToArray());
        var loop = Create(chat, [new CountingTool("repo_search", "ok")], new LlmChatOptions { MaxSteps = 3 });

        var answer = await loop.RunAsync([], "вопрос", CancellationToken.None);

        Assert.Contains("лимит шагов", answer, StringComparison.Ordinal);
        Assert.Equal(3, chat.CallCount);
    }

    [Fact]
    public async Task ToolCallLimit_BreaksLoop()
    {
        var toolCall = """{"tool":"repo_search","arguments":{"query":"x"}}""";
        var chat = new ScriptedChatClient(Enumerable.Repeat(toolCall, 10).ToArray());
        var loop = Create(chat, [new CountingTool("repo_search", "ok")], new LlmChatOptions { MaxSteps = 10, ToolCallLimit = 2 });

        var answer = await loop.RunAsync([], "вопрос", CancellationToken.None);

        Assert.Contains("лимит вызовов инструментов", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyModelResponse_ReportsFailure()
    {
        var chat = new ScriptedChatClient("");
        var loop = Create(chat, []);

        var answer = await loop.RunAsync([], "вопрос", CancellationToken.None);

        Assert.Contains("пустой ответ", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelException_DoesNotThrow_ReturnsFailureText()
    {
        var chat = new ThrowingChatClient();
        var loop = Create(chat, []);

        var answer = await loop.RunAsync([], "вопрос", CancellationToken.None);

        Assert.Contains("Сбой LLM", answer, StringComparison.Ordinal);
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            MeaiChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("транспорт недоступен");

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

    [Theory]
    [InlineData("""{"tool":"repo_search","arguments":{"query":"x"}}""", "repo_search")]
    [InlineData("```json\n{\"tool\":\"repo_read_file\",\"arguments\":{\"path\":\"a.cs\"}}\n```", "repo_read_file")]
    [InlineData("""{"tool":"repo_search","arguments":"{\"query\":\"x\"}"}""", "repo_search")]
    public void TryParseToolCall_ValidForms(string text, string expectedTool)
    {
        Assert.True(ChatAgentLoop.TryParseToolCall(text, out var name, out var args));
        Assert.Equal(expectedTool, name);
        Assert.False(string.IsNullOrWhiteSpace(args));
    }

    [Theory]
    [InlineData("Обычный текст без JSON")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{broken")]
    public void TryParseToolCall_InvalidForms_ReturnsFalse(string text)
    {
        Assert.False(ChatAgentLoop.TryParseToolCall(text, out _, out _));
    }
}
