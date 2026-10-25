using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LLMCore.Abstractions;
using Microsoft.Extensions.Options;

namespace LLMCore.Llm;

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

        // Пустая схема — свободный ответ (режим чата, план 18/19): response_format
        // не отправляется вовсе. Без этой ветки JsonDocument.Parse("") падал
        // с «The input does not contain any JSON tokens» — чат с реальным LLM
        // не работал бы вообще (выявлено интеграционным тестом).
        if (string.IsNullOrWhiteSpace(request.ExpectedJsonSchema))
        {
            var plain = await SendWithoutSchemaAsync(request, ct);
            stopwatch.Stop();
            // Дефект N2 (план 22): ParseResponseAsync принимает владение ответом.
            return await ParseResponseAsync(plain, request, (int)stopwatch.Elapsed.TotalMilliseconds, fallbackUsed, ct);
        }

        try
        {
            var response = await SendWithRetriesAsync(request, ct);
            stopwatch.Stop();
            return await ParseResponseAsync(response, request, (int)stopwatch.Elapsed.TotalMilliseconds, fallbackUsed, ct);
        }
        catch (JsonSchemaFallbackException ex)
        {
            // Дефект N2 (план 22): 400-ответ, на котором провайдер отверг
            // json_schema, обязан быть освобождён до повтора без схемы.
            ex.Response?.Dispose();
            fallbackUsed = true;
            // Retry without JSON schema
            var response2 = await SendWithoutSchemaAsync(request, ct);
            stopwatch.Stop();
            return await ParseResponseAsync(response2, request, (int)stopwatch.Elapsed.TotalMilliseconds, true, ct);
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
            HttpResponseMessage? response = null;
            try
            {
                response = await _http.PostAsJsonAsync("/v1/chat/completions", body, ct);

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

                    // Дефект N2 (план 22): ответ перед повтором освобождается.
                    response.Dispose();
                    response = null;
                    await Task.Delay(delay, ct);
                    continue;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    // Провайдер не поддерживает json_schema.
                    // Дефект N2: ответ передаётся исключению, чтобы вызывающий
                    // (CompleteAsync) освободил его после перехода на фолбэк.
                    throw new JsonSchemaFallbackException("Provider rejected json_schema", response);
                }

                // Присваивание снимает ложное срабатывание DisposableDiscarded:
                // EnsureSuccessStatusCode возвращает тот же объект.
                response = response.EnsureSuccessStatusCode();
                var result = response;
                response = null; // Владение переходит к вызывающему.
                return result;
            }
            catch (JsonSchemaFallbackException)
            {
                throw;
            }
            catch (Exception)
            {
                // Дефект N2 (план 22): при сбое попытки ответ освобождается здесь.
                response?.Dispose();
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
        // Дефект N2 (план 22): владение ответом переходит к вызывающему —
        // ParseResponseAsync освобождает его после чтения контента.
        response = response.EnsureSuccessStatusCode();
        return response;
    }

    private async Task<LlmCompletion> ParseResponseAsync(
        HttpResponseMessage response, LlmRequest request, int durationMs, bool fallbackUsed,
        CancellationToken cancellationToken = default)
    {
        // Дефект N2 (план 22): метод принимает владение ответом —
        // освобождает его после чтения контента (finally).
        // Дефект D3 (план 18): ReadAsStringAsync().Result — sync-over-async,
        // риск голодания пула потоков. Теперь полноценный await.
        string json;
        try
        {
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            response.Dispose();
        }

        // Дефект N3 (план 22): JsonDocument — IDisposable, обязателен using.
        using var doc = JsonDocument.Parse(json);
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
        public JsonSchemaFallbackException(string message, HttpResponseMessage? response = null)
            : base(message)
        {
            Response = response;
        }

        /// <summary>Ответ провайдера, отвергшего json_schema (для освобождения вызывающим).</summary>
        public HttpResponseMessage? Response { get; }
    }
}
