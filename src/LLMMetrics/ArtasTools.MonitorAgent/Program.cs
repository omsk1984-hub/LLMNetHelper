using System.Text;
using ArtasTools.MonitorAgent.Health;
using ArtasTools.MonitorAgent.Abstractions;
using ArtasTools.MonitorAgent.Scheduling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArtasTools.MonitorAgent;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // UTF-8 console
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
                config.AddEnvironmentVariables();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices((hostContext, services) =>
            {
                services.AddMonitorAgent(hostContext.Configuration);
            })
            .Build();

        // Парсинг аргументов командной строки
        var agentOptions = host.Services.GetRequiredService<IOptions<AgentOptions>>().Value;

        var runNow = args.Contains("--run-now");
        var dryRun = args.Contains("--dry-run");
        var llmProvider = args.FirstOrDefault(a => a.StartsWith("--llm-provider="))?.Split('=')[1];
        var sinceArg = args.FirstOrDefault(a => a.StartsWith("--since="))?.Split('=')[1];
        var hoursArg = args.FirstOrDefault(a => a.StartsWith("--hours="))?.Split('=')[1];

        if (!string.IsNullOrEmpty(llmProvider))
        {
            Console.WriteLine($"[WARN] --llm-provider={llmProvider}: переопределение требует пересборки конфигурации");
        }

        if (runNow || dryRun)
        {
            Console.WriteLine(dryRun
                ? "[INFO] Dry-run: собираем факты и сводку, LLM не вызывается"
                : "[INFO] Запуск по команде --run-now...");

            DateTime? since = null;
            if (!string.IsNullOrWhiteSpace(sinceArg) &&
                DateTime.TryParse(sinceArg, null, System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsedSince))
            {
                since = DateTime.SpecifyKind(parsedSince, DateTimeKind.Utc);
            }

            int? hours = null;
            if (!string.IsNullOrWhiteSpace(hoursArg) && int.TryParse(hoursArg, out var parsedHours))
            {
                hours = parsedHours;
            }

            var agentRunner = host.Services.GetRequiredService<IAgentRunner>();

            // Инициализация хранилища (WAL + миграции) обязательна ДО первого прогона.
            // В планировщике её выполняет NightlyScheduleHostedService.RunOnceAsync,
            // а ручной путь (--run-now/--dry-run) шёл мимо неё и падал с
            // "SQLite Error 1: 'no such table: runs'" на пустой базе.
            var factStore = host.Services.GetRequiredService<IFactStore>();
            await factStore.InitializeAsync(CancellationToken.None);

            try
            {
                return await agentRunner.RunAsync(dryRun, catchUp: false, since, hours, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[ERROR] Прогон отменён");
                return 1;
            }
        }

        // Health endpoint (Kestrel)
        var healthPort = agentOptions.HealthPort;
        var runner = Task.Run(async () => await host.RunAsync());

        // Запускаем простой HTTP-сервер для healthcheck
        var healthTask = RunHealthServerAsync(host, healthPort, CancellationToken.None);

        await Task.WhenAll(runner, healthTask);

        return 0;
    }

    private static async Task RunHealthServerAsync(IHost host, int port, CancellationToken ct)
    {
        var healthState = host.Services.GetRequiredService<AgentHealthState>();
        var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        Console.WriteLine($"[HEALTH] Listening on http://localhost:{port}/");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                var path = ctx.Request.Url?.AbsolutePath;

                if (path == "/health" || path == "/")
                {
                    var response = System.Text.Encoding.UTF8.GetBytes(
                        $"{{\"status\":\"running\",\"port\":{port}}}");
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentLength64 = response.Length;
                    await ctx.Response.OutputStream.WriteAsync(response, ct);
                }
                else if (path == "/status")
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(healthState);
                    var response = System.Text.Encoding.UTF8.GetBytes(json);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentLength64 = response.Length;
                    await ctx.Response.OutputStream.WriteAsync(response, ct);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    continue;
                }

                ctx.Response.Close();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[HEALTH] Error: {ex.Message}");
            }
        }

        listener.Stop();
    }
}
