using System.Text;
using LLMNetHelper;
using LLMNetHelper.GitLab;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

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
                // appsettings.json ищется ОТ РАБОЧЕГО КАТАЛОГА процесса. При запуске
                // через `dotnet run` (раннер) или через Start-Process с другим
                // -WorkingDirectory рабочий каталог не совпадает с каталогом сборки,
                // и файл молча НЕ читался — конфигурация агент получала только из
                // .env/окружения, а значения по умолчанию из appsettings.json
                // (например GitLab:GroupName) оказывались пустыми.
                // Поэтому добавляем оба расположения: явный (каталог сборки, куда
                // файл копируется по CopyToOutputDirectory) и рабочий каталог.
                config.AddJsonFile(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    optional: true,
                    reloadOnChange: false);

                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

                // Переменные окружения (.env, экспортированный раннером) — приоритетнее файла.
                config.AddEnvironmentVariables();
            })
            .ConfigureLogging(logging =>
            {
                // --- Serilog ---
                Log.Logger = LoggingConfig.CreateLogger(
                    logDir: "logs",
                    retentionDays: 30,
                    errorRetentionDays: 90,
                    enabled: true);

                logging.ClearProviders();
                logging.AddSerilog();
            })
            .ConfigureServices((hostContext, services) =>
            {
                services.AddMonitorAgent(hostContext.Configuration);
            })
            .Build();

        try
        {
            // --- Режим: клонирование/обновление репозиториев группы GitLab ---
            if (args.Contains("--clone-all"))
            {
                return await RunCloneAllAsync(host, forceClone: args.Contains("--force-clone"));
            }

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
        finally
        {
            // Корректное закрытие логгера при остановке
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Режим <c>--clone-all</c>: скачать или обновить все репозитории группы GitLab.
    /// </summary>
    /// <returns>0 — успех, 1 — есть неудачные операции, 4 — GitLab отключён/не настроен.</returns>
    private static async Task<int> RunCloneAllAsync(IHost host, bool forceClone)
    {
        Console.WriteLine("[INFO] Режим клонирования репозиториев GitLab...");

        GitLabService? service;
        GitLabOptions options;

        try
        {
            service = host.Services.GetService<GitLabService>();
            options = host.Services.GetRequiredService<GitLabOptions>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Не удалось разрешить GitLab-сервисы: {ex.Message}");
            return 4;
        }

        if (service == null)
        {
            Console.WriteLine("[ERROR] GitLab-функциональность отключена (GitLab:Enabled = false).");
            Console.WriteLine("[HINT]  Запустите через run_LLMNetHelper.ps1 -CloneAll или задайте GITLAB__ENABLED=true.");
            return 4;
        }

        if (string.IsNullOrWhiteSpace(options.Token))
        {
            Console.WriteLine("[ERROR] GitLab:Token пуст — клонирование приватных репозиториев невозможно.");
            Console.WriteLine("[HINT]  Задайте GITLAB__TOKEN в src/LLMNetHelper/.env.");
            return 4;
        }

        Console.WriteLine($"[INFO] API     : {options.ApiUrl}");
        Console.WriteLine($"[INFO] Группа  : {(options.GroupId > 0 ? options.GroupId.ToString() : "(резолв по имени)")} / {options.GroupName}");
        Console.WriteLine($"[INFO] Каталог : {options.CloneRoot}{(forceClone ? " (force: переклонирование)" : "")}");
        Console.WriteLine($"[INFO] TLS     : {(string.IsNullOrWhiteSpace(options.TlsProtocols) ? "<системный выбор>" : options.TlsProtocols)}");

        // Синхронный адаптер: Progress<T> маршалирует колбэки через очередь
        // (SynchronizationContext/ThreadPool), из-за чего вывод по отдельным
        // репозиториям мог бы появиться уже после итогового сообщения.
        var progress = new SyncProgress<RepoOperationResult>(r =>
        {
            var mark = r.Success ? "OK  " : "FAIL";
            Console.WriteLine($"[{mark}] {r.Action,-8} {r.RepoName} ({r.DurationMs} ms){(r.Error != null ? " — " + r.Error : "")}");
        });

        try
        {
            var withProgress = new GitLabService(
                host.Services.GetRequiredService<GitLabApiClient>(),
                options,
                progress);

            var result = await withProgress.CloneAllAsync(forceClone, CancellationToken.None);

            Console.WriteLine($"[INFO] Итог: всего={result.Total} cloned={result.Cloned} updated={result.Updated} failed={result.Failed}");

            return result.AllSucceeded ? 0 : 1;
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"[ERROR] {ex.Message}");
            return 4;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Сбой клонирования: {Describe(ex)}");
            return 1;
        }
    }

    /// <summary>Полная цепочка исключений (тип + сообщение на каждый уровень).</summary>
    private static string Describe(Exception ex)
    {
        var sb = new StringBuilder();

        for (var e = ex; e != null; e = e.InnerException)
        {
            if (sb.Length > 0)
            {
                sb.Append(" <- ");
            }

            sb.Append(e.GetType().Name).Append(": ").Append(e.Message);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Синхронный <see cref="IProgress{T}"/>: вызывает делегат непосредственно
    /// в потоке вызывающего (без очереди маршалинга).
    /// </summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SyncProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }
}
