using System.Net;
using System.Text;
using LLMCore.Abstractions;
using LLMCore.Llm;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMNetHelper.Tests.Llm;

/// <summary>
/// Тесты освобождения HTTP-ответов (план 22, дефект N2):
/// <c>CustomOpenAiCompatibleClient</c> обязан диспозить каждый
/// <see cref="HttpResponseMessage"/> на всех путях — ретрай (429/5xx),
/// фолбэк без схемы (400), исключение попытки и успешный разбор.
/// </summary>
public class CustomOpenAiClientDisposalTests
{
    private const string Schema = """{"type":"object"}""";

    private const string OkJson =
        "{\u0022choices\u0022:[{\u0022message\u0022:{\u0022content\u0022:\u0022{}\u0022},\u0022finish_reason\u0022:\u0022stop\u0022}]," +
        "\u0022usage\u0022:{\u0022prompt_tokens\u0022:1,\u0022completion_tokens\u0022:2}}";

    /// <summary>
    /// Дефект N2: ответ 429 на пути ретрая не освобождался (continue без Dispose).
    /// </summary>
    [Fact]
    public async Task Retry429ThenSuccess_AllResponsesDisposed()
    {
        var handler = new CannedHandler(attempt => attempt == 0
            ? new TrackingResponse(HttpStatusCode.TooManyRequests)
            : new TrackingResponse(HttpStatusCode.OK, OkJson));
        var client = CreateClient(handler, maxRetries: 1);

        await client.CompleteAsync(Request(Schema), CancellationToken.None);

        Assert.Equal(2, handler.Created.Count);
        Assert.All(handler.Created, r => Assert.True(r.Disposed, "HttpResponseMessage не освобождён (дефект N2)"));
    }

    /// <summary>
    /// Дефект N2: ответ 400 на пути фолбэка без json_schema не освобождался.
    /// </summary>
    [Fact]
    public async Task BadRequest_FallbackToNoSchema_AllResponsesDisposed()
    {
        var handler = new CannedHandler(attempt => attempt == 0
            ? new TrackingResponse(HttpStatusCode.BadRequest, "{}")
            : new TrackingResponse(HttpStatusCode.OK, OkJson));
        var client = CreateClient(handler, maxRetries: 1);

        var completion = await client.CompleteAsync(Request(Schema), CancellationToken.None);

        Assert.True(completion.FallbackUsed);
        Assert.Equal(2, handler.Created.Count);
        Assert.All(handler.Created, r => Assert.True(r.Disposed, "HttpResponseMessage не освобождён (дефект N2)"));
    }

    /// <summary>
    /// Дефект N2: успешный ответ не освобождался после разбора
    /// (ParseResponseAsync обязан принимать владение).
    /// </summary>
    [Fact]
    public async Task Success_FinalResponseDisposed()
    {
        var handler = new CannedHandler(_ => new TrackingResponse(HttpStatusCode.OK, OkJson));
        var client = CreateClient(handler, maxRetries: 0);

        await client.CompleteAsync(Request(Schema), CancellationToken.None);

        Assert.Single(handler.Created);
        Assert.True(handler.Created[0].Disposed, "Финальный HttpResponseMessage не освобождён (дефект N2)");
    }

    /// <summary>
    /// Дефект N2: при исключении попытки (EnsureSuccessStatusCode на 403)
    /// ответ не освобождался.
    /// </summary>
    [Fact]
    public async Task Forbidden_RetriesExhausted_ResponseDisposed()
    {
        var handler = new CannedHandler(_ => new TrackingResponse(HttpStatusCode.Forbidden, "{}"));
        var client = CreateClient(handler, maxRetries: 0);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.CompleteAsync(Request(Schema), CancellationToken.None));

        Assert.Single(handler.Created);
        Assert.True(handler.Created[0].Disposed, "HttpResponseMessage не освобождён на пути исключения (дефект N2)");
    }

    private static LlmRequest Request(string schema) => new("sys", "user", schema, 10, 0.1);

    private static CustomOpenAiCompatibleClient CreateClient(CannedHandler handler, int maxRetries) =>
        new(new FakeHttpClientFactory(handler), Options.Create(new LlmOptions
        {
            Endpoint = "http://localhost",
            ApiKey = "test",
            Model = "test-model",
            MaxRetries = maxRetries,
            TimeoutSeconds = 5
        }));

    /// <summary>
    /// Ответ, отслеживающий факт освобождения.
    /// </summary>
    private sealed class TrackingResponse : HttpResponseMessage
    {
        public TrackingResponse(HttpStatusCode statusCode, string? content = null)
            : base(statusCode)
        {
            if (content is not null)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json");
            }
        }

        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Обработчик, отдающий ответ по номеру попытки и запоминающий все созданные ответы.
    /// </summary>
    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly Func<int, TrackingResponse> _factory;
        private int _call;

        public CannedHandler(Func<int, TrackingResponse> factory) => _factory = factory;

        public List<TrackingResponse> Created { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = _factory(_call++);
            Created.Add(response);
            return Task.FromResult<HttpResponseMessage>(response);
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        // BaseAddress обязателен: клиент слает относительный /v1/chat/completions.
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://llm.test") };
    }
}
