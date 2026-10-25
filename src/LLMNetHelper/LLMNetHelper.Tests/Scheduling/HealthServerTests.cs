using System.Net;
using System.Net.Sockets;
using LLMNetHelper.Health;
using LLMNetHelper.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LLMNetHelper.Tests.Scheduling;

/// <summary>
/// Тесты health-сервера (план 22, дефект N1):
/// <c>StopAsync</c> не должен блокировать поток через <c>Task.Wait</c>
/// и не должен заворачивать сбой фонового слушателя в <c>AggregateException</c>.
/// </summary>
public class HealthServerTests
{
    /// <summary>
    /// Дефект N1 (план 22): <c>HealthServer.StopAsync</c> вызывал
    /// <c>_serverTask?.Wait(ct)</c>. Если фоновая задача <c>RunAsync</c>
    /// завершилась сбоём (например, порт уже занят другим слушателем),
    /// <c>Wait</c> бросает <c>AggregateException</c> — остановка хоста
    /// падала вместо корректного завершения. После фикса (await с
    /// перехватом) StopAsync обязан завершиться без исключения.
    /// </summary>
    [Fact]
    public async Task StopAsync_ServerFaulted_DoesNotThrow()
    {
        var port = GetFreePort();

        // Первый сервер занимает порт и успешно стартует.
        var first = CreateServer(port);
        await first.StartAsync(CancellationToken.None);

        try
        {
            // Второй сервер на том же порту: RunAsync падает с
            // HttpListenerException (конфликт префиксов), задача — faulted.
            var second = CreateServer(port);
            await second.StartAsync(CancellationToken.None);

            // На текущем коде здесь летит AggregateException из Wait().
            await second.StopAsync(CancellationToken.None);
        }
        finally
        {
            await first.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Штатный сценарий: старт, остановка по отмене — без исключений
    /// и без зависаний (защита от регрессии при переходе на await).
    /// </summary>
    [Fact]
    public async Task StopAsync_RunningServer_CompletesCleanly()
    {
        var port = GetFreePort();
        var server = CreateServer(port);

        await server.StartAsync(CancellationToken.None);
        await server.StopAsync(CancellationToken.None);
    }

    private static HealthServer CreateServer(int port) =>
        new(new AgentHealthState(), port.ToString(), NullLogger<HealthServer>.Instance);

    /// <summary>
    /// Свободный порт через TCP-пробу (loopback, офлайн).
    /// </summary>
    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
