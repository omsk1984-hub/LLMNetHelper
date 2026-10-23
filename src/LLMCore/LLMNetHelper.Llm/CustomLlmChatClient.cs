using System.Runtime.CompilerServices;
using System.Text;
using LLMNetHelper.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace LLMNetHelper.Llm;

/// <summary>
/// Адаптер <see cref="IChatClient"/> (MEAI 10.5.0) поверх существующего
/// <see cref="IAnalysisLlmClient"/>.
/// <para>
/// Позволяет агентскому оркестратору работать с <c>custom</c>-провайдером
/// без второго HTTP-транспорта: обе реализации используют один и тот же
/// запрос к [OI]-совместимому API.
/// </para>
/// <para>
/// Ограничение: tool calling через этот адаптер не поддерживается — оркестратор
/// получает текстовые ответы и сам решает, какой инструмент вызвать,
/// разбирая JSON-инструкцию из ответа модели.
/// </para>
/// </summary>
internal sealed class CustomLlmChatClient : IChatClient
{
    private readonly IAnalysisLlmClient _inner;
    private readonly LlmOptions _options;

    public CustomLlmChatClient(IAnalysisLlmClient inner, IOptions<LlmOptions> options)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return GetResponseCoreAsync(messages, options, cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Транспорт не стриминговый: отдаём единственным апдейтом.
        var response = await GetResponseCoreAsync(messages, options, cancellationToken).ConfigureAwait(false);

        var text = response.Text ?? "";
        yield return new ChatResponseUpdate(ChatRole.Assistant, text)
        {
            ResponseId = response.ResponseId,
            ModelId = response.ModelId,
            FinishReason = response.FinishReason
        };
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose()
    {
        // Владелец IAnalysisLlmClient управляет его жизненным циклом.
    }

    /// <summary>
    /// Разделяет сообщения на системный и пользовательский промпты,
    /// вызывает транспорт и упаковывает результат в <see cref="ChatResponse"/>.
    /// </summary>
    private async Task<ChatResponse> GetResponseCoreAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var system = new StringBuilder();
        var user = new StringBuilder();

        foreach (var message in messages)
        {
            var text = message.Text ?? "";
            if (text.Length == 0)
            {
                continue;
            }

            switch (message.Role.Value)
            {
                case "system":
                    system.AppendLine(text);
                    break;
                case "assistant":
                    // Историю ассистента подаём как часть пользовательского контекста:
                    // транспорт поддерживает только одну пару system/user.
                    user.AppendLine($"[assistant] {text}");
                    break;
                default:
                    user.AppendLine(text);
                    break;
            }
        }

        var request = new LlmRequest(
            SystemPrompt: system.ToString().Trim(),
            UserPrompt: user.ToString().Trim(),
            ExpectedJsonSchema: "",
            MaxTokens: options?.MaxOutputTokens ?? _options.MaxTokens,
            Temperature: options?.Temperature is { } t ? t : _options.Temperature);

        var completion = await _inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

        var assistantMessage = new ChatMessage(ChatRole.Assistant, completion.Text);

        return new ChatResponse(assistantMessage)
        {
            ModelId = completion.Model,
            FinishReason = MapFinishReason(completion.FinishReason),
            Usage = new UsageDetails
            {
                InputTokenCount = completion.PromptTokens,
                OutputTokenCount = completion.CompletionTokens
            }
        };
    }

    private static ChatFinishReason? MapFinishReason(string? reason)
    {
        return reason switch
        {
            "stop" => ChatFinishReason.Stop,
            "length" => ChatFinishReason.Length,
            "content_filter" => ChatFinishReason.ContentFilter,
            "tool_calls" => ChatFinishReason.ToolCalls,
            _ => null
        };
    }
}
