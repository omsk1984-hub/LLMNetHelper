using LLMNetHelper.Abstractions;
using LLMNetHelper.Chat;
using LLMNetHelper.GitLab;
using LLMCore.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using LlmChatOptions = LLMNetHelper.Chat.ChatOptions;
using MeaiChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace LLMNetHelper.Tests.Chat;

/// <summary>
/// Тесты подменяемости сборки промптов чата (план 22, этап 6.1):
/// <see cref="ChatSession"/> обязан получать промпты через абстракцию
/// <see cref="IChatPromptBuilder"/>, а не вызывать статический
/// <c>ChatPromptBuilder</c> — иначе промпты нельзя подменить в тестах
/// и версионировать.
/// </summary>
public class ChatPromptBuilderInjectionTests
{
    /// <summary>
    /// Системный и пользовательский промпты режима A берутся из внедрённого
    /// билдера: маркерные строки обязаны попасть в сообщения, отправленные модели.
    /// </summary>
    [Fact]
    public async Task ModeA_UsesInjectedPromptBuilder()
    {
        var builder = new StubChatPromptBuilder();
        var chat = new OneShotChatClient("ответ модели");
        var scope = new ChatScope(
            new GitLabOptions { CloneRoot = Path.Combine(Path.GetTempPath(), "chat_pb_" + Guid.NewGuid().ToString("N")) },
            new LlmChatOptions());

        var session = new ChatSession(
            chat,
            scope,
            new LlmChatOptions(),
            new LlmOptions { Endpoint = "http://x", ApiKey = "k", Model = "m" },
            NullLoggerFactory.Instance,
            new StringReader(""),
            TextWriter.Null,
            chatPromptBuilder: builder);

        await session.AnswerAsync("Что делает Program?", CancellationToken.None);

        var messages = chat.Received[0];
        Assert.Equal("stub-chat-system", messages.Single(m => m.Role == ChatRole.System).Text);
        Assert.Equal("stub-chat-user", messages.Single(m => m.Role == ChatRole.User).Text);
    }

    /// <summary>
    /// Билдер-заглушка с маркерными строками.
    /// </summary>
    private sealed class StubChatPromptBuilder : IChatPromptBuilder
    {
        public string BuildSystemPrompt(string scopeDescription, bool withTools, string? toolDescriptions = null) =>
            "stub-chat-system";

        public string BuildUserPrompt(string question, IReadOnlyList<ContextDocument> documents) =>
            "stub-chat-user";
    }

    /// <summary>Мок IChatClient: отдаёт один ответ, запоминает сообщения.</summary>
    private sealed class OneShotChatClient(string responseText) : IChatClient
    {
        public List<IList<ChatMessage>> Received { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            MeaiChatOptions? options = null,
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
            MeaiChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
