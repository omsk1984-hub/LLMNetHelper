using System.Net;
using LLMNetHelper.Health;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LLMNetHelper.Scheduling;

/// <summary>
/// Фоновый сервис для healthcheck-эндпоинтов.
/// Заменяет inline-код HTTP-сервера из <c>Program.cs</c>.
/// </summary>
public sealed class HealthServer : IHostedService
{
    private readonly AgentHealthState _healthState;
    private readonly int _port;
    private readonly ILogger<HealthServer> _logger;
    private Task? _serverTask;
    private CancellationTokenSource? _cts;

    public HealthServer(
        AgentHealthState healthState,
        [FromKeyedServices("HealthPort")] string portString,
        ILogger<HealthServer> logger)
    {
        _healthState = healthState;
        _port = int.Parse(portString);
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _serverTask = RunAsync(_cts.Token);
        _logger.LogInformation("[HEALTH] Listening on http://localhost:{Port}/", _port);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[HEALTH] Stopping health server on port {Port}", _port);
        _cts?.Cancel();

        // Дефект N1 (план 22): раньше здесь был _serverTask?.Wait(ct) —
        // sync-over-async, блокировавший поток остановки и заворачивавший
        // сбой слушателя (например, конфликт портов) в AggregateException.
        // Теперь полноценный await; сбой фонового цикла логируем, но не роняем остановку.
        if (_serverTask is { } serverTask)
        {
            try
            {
                await serverTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[HEALTH] Фоновый цикл health-сервера завершился сбоем");
            }
            catch (OperationCanceledException)
            {
                // Ожидается при отмене — штатная остановка.
            }
        }

        _cts?.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{_port}/");
        listener.Start();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var ctx = await listener.GetContextAsync().WaitAsync(ct);
                    HandleRequest(ctx);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[HEALTH] Error handling request");
                }
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath;

        if (path == "/health" || path == "/")
        {
            SendJson(context, 200, new { status = "running", port = _port });
        }
        else if (path == "/status")
        {
            SendJson(context, 200, _healthState);
        }
        else if (path == "/exit")
        {
            SendJson(context, 200, new { status = "shutting_down" });
            _logger.LogInformation("[HEALTH] /exit called — shutting down...");
            _cts?.Cancel();
        }
        else
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
        }
    }

    private static void SendJson(HttpListenerContext context, int statusCode, object data)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = statusCode;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }
}
