using System.Text;
using LLMNetHelper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LLMNetHelper;

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

        var runNow = args.Contains("--run-now");
        var dryRun = args.Contains("--dry-run");

        if (runNow || dryRun)
        {
            Console.WriteLine(dryRun
                ? "[INFO] Dry-run: собираем факты и сводку, LLM не вызывается"
                : "[INFO] Запуск по команде --run-now...");

            var agentRunner = host.Services.GetRequiredService<IAgentRunner>();

            // Инициализация хранилища (WAL + миграции) обязательна ДО первого прогона.
            var factStore = host.Services.GetRequiredService<Abstractions.IFactStore>();
            await factStore.InitializeAsync(CancellationToken.None);

            try
            {
                return await agentRunner.RunAsync(dryRun, catchUp: false, since: null, hours: null, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[ERROR] Прогон отменён");
                return 1;
            }
        }

        // Запуск хоста (включает NightlyScheduleHostedService + HealthServer)
        await host.RunAsync();
        return 0;
    }
}
