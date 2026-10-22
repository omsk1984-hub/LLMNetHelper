using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LLMNetHelper.Abstractions;
using Microsoft.Extensions.Options;

namespace LLMNetHelper.Llm;

/// <summary>
/// Реализация A: собственный HTTP-клиент для [OI]-совместимого API.
/// Полный контроль над телом запроса, JSON-схемой, ретраями и токенами.
/// </summary>
public sealed class CustomOpenAiCompatibleClient : IAnalysisLlmClient
{
    private readonly HttpClient _http;
    private readonly LlmOptions _options;

    public string ProviderName => "custom";

    public CustomOpenAiCompatibleClient(IHttpClientFactory factory, IOptions<LlmOptions> options)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _http = factory.CreateClient("llm");
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    public async Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var fallbackUsed = false;

        try
        {
            var response = await SendWithRetriesAsync(request, ct);
            stopwatch.Stop();
            return ParseResponse(response, request, (int)stopwatch.Elapsed.TotalMilliseconds, fallbackUsed);
        }
        catch (JsonSchemaFallbackException)
        {
            fallbackUsed = true;
            // Retry without JSON schema
            var response2 = await SendWithoutSchemaAsync(request, ct);
            stopwatch.Stop();
            return ParseResponse(response2, request, (int)stopwatch.Elapsed.TotalMilliseconds, true);
        }
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(LlmRequest request, CancellationToken ct)
    {
        var body = new
        {
            model = _options.Model,
            messages = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt }
            },
            max_tokens = _options.MaxTokens,
            temperature = (float)_options.Temperature,
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    strict = true,
                    name = "analysis_report",
                    schema = JsonDocument.Parse(request.ExpectedJsonSchema).RootElement
                }
            }
        };

        for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            try
            {
                var response = await _http.PostAsJsonAsync("/v1/chat/completions", body, ct);

                // Ретрай на 429/5xx
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                    response.StatusCode >= System.Net.HttpStatusCode.InternalServerError)
                {
                    TimeSpan delay;
                    if (response.Headers.RetryAfter?.Date.HasValue == true)
                    {
                        delay = response.Headers.RetryAfter.Date.Value - DateTime.UtcNow;
                        if (delay < TimeSpan.Zero) delay = TimeSpan.FromSeconds(1);
                    }
                    else
                    {
                        delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    }
                    await Task.Delay(delay, ct);
                    continue;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    // Провайдер не поддерживает json_schema
                    throw new JsonSchemaFallbackException("Provider rejected json_schema");
                }

                response.EnsureSuccessStatusCode();
                return response;
            }
            catch (JsonSchemaFallbackException)
            {
                throw;
            }
            catch (Exception)
            {
                if (attempt == _options.MaxRetries) throw;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }

        throw new HttpRequestException("LLM request failed after retries");
    }

    private async Task<HttpResponseMessage> SendWithoutSchemaAsync(LlmRequest request, CancellationToken ct)
    {
        var body = new
        {
            model = _options.Model,
            messages = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt }
            },
            max_tokens = _options.MaxTokens,
            temperature = (float)_options.Temperature
        };

        var response = await _http.PostAsJsonAsync("/v1/chat/completions", body, ct);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private LlmCompletion ParseResponse(
        HttpResponseMessage response, LlmRequest request, int durationMs, bool fallbackUsed)
    {
        var json = response.Content.ReadAsStringAsync().Result;
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = "";
        var jsonContent = (string?)null;
        int? promptTokens = null;
        int? completionTokens = null;
        string? finishReason = null;

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                text = content.GetString() ?? "";
                jsonContent = text; // Для custom: текст = потенциальный JSON
            }
            finishReason = choice.GetProperty("finish_reason").GetString();
        }

        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt))
                promptTokens = pt.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var ct))
                completionTokens = ct.GetInt32();
        }

        return new LlmCompletion(
            Text: text,
            Json: jsonContent,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            FinishReason: finishReason,
            Model: _options.Model,
            FallbackUsed: fallbackUsed,
            Duration: TimeSpan.FromMilliseconds(durationMs));
    }

    private sealed class JsonSchemaFallbackException : Exception
    {
        public JsonSchemaFallbackException(string message) : base(message) { }
    }
}
