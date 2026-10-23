using System.Text.Json;
using LLMNetHelper.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace LLMCore.Llm;

/// <summary>
/// Реализация B: Microsoft.Extensions.AI 10.5.0.
/// <para>
/// Работает поверх <see cref="IChatClient"/>: транспорт может быть [OI],
/// Azure [OI] или любой другой провайдер, поддерживающий MEAI.
/// </para>
/// <para>
/// Реализует два контракта:
/// <list type="bullet">
///   <item><see cref="IChatClient"/> — для агентского оркестратора (tool calling);</item>
///   <item><see cref="IAnalysisLlmClient"/> — для однократного вызова и генерации отчёта.</item>
/// </list>
/// </para>
/// </summary>
public sealed class MeaiLlmClient : IChatClient, IAnalysisLlmClient, IDisposable
{
    private readonly IChatClient _chatClient;
    private readonly LlmOptions _options;

    public MeaiLlmClient(IChatClient chatClient, IOptions<LlmOptions> options)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public string ProviderName => "meai";

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return _chatClient.GetResponseAsync(messages, options ?? DefaultChatOptions(), cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return _chatClient.GetStreamingResponseAsync(messages, options ?? DefaultChatOptions(), cancellationToken);
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return _chatClient.GetService(serviceType, serviceKey);
    }

    /// <summary>
    /// Однократный вызов для генерации отчёта.
    /// </summary>
    public async Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, request.SystemPrompt),
            new(ChatRole.User, request.UserPrompt)
        };

        try
        {
            var response = await _chatClient.GetResponseAsync(
                messages,
                new ChatOptions
                {
                    MaxOutputTokens = request.MaxTokens,
                    Temperature = (float)request.Temperature
                },
                ct).ConfigureAwait(false);

            stopwatch.Stop();

            var text = response.Text ?? "";
            var usage = response.Usage;

            return new LlmCompletion(
                Text: text,
                Json: ExtractJson(text),
                PromptTokens: (int?)usage?.InputTokenCount,
                CompletionTokens: (int?)usage?.OutputTokenCount,
                FinishReason: response.FinishReason?.Value,
                Model: _options.Model,
                FallbackUsed: false,
                Duration: stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // Не роняем прогон: отдаём ответ с пометкой об ошибке.
            return new LlmCompletion(
                Text: $"Ошибка LLM: {ex.Message}",
                Json: null,
                PromptTokens: null,
                CompletionTokens: null,
                FinishReason: "error",
                Model: _options.Model,
                FallbackUsed: true,
                Duration: stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Параметры по умолчанию из конфигурации.
    /// </summary>
    private ChatOptions DefaultChatOptions()
    {
        return new ChatOptions
        {
            MaxOutputTokens = _options.MaxTokens,
            Temperature = (float)_options.Temperature
        };
    }

    /// <summary>
    /// Извлекает JSON-объект из ответа модели.
    /// </summary>
    private static string? ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');

        if (start < 0 || end <= start)
        {
            return null;
        }

        var candidate = text[start..(end + 1)];

        try
        {
            using var _ = JsonDocument.Parse(candidate);
            return candidate;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_chatClient is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
