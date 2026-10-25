using System.Text;
using LLMNetHelper;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Chat;
using LLMNetHelper.GitLab;
using LLMCore.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

            // --- Режим: интерактивный чат по скачанным репозиториям ---
            if (args.Contains("--chat"))
            {
                return await RunChatAsync(host, args);
            }

            var runNow = args.Contains("--run-now");
            var dryRun = args.Contains("--dry-run");

            if (runNow || dryRun)
            {
                Console.WriteLine(dryRun
                    ? "[INFO] Dry-run: собираем факты и сводку, LLM не вызывается"
                    : "[INFO] Запуск по команде --run-now...");

                // Fail-fast конфигурации LLM (только реальный прогон: DryRun
                // модель не вызывает). Источник истины — слитная конфигурация
                // приложения (appsettings.json + окружение), поэтому предпроверка
                // живёт здесь, а не в run_LLMNetHelper.ps1.
                if (!dryRun)
                {
                    var llm = host.Services.GetRequiredService<IOptions<LlmOptions>>().Value;
                    if (string.IsNullOrWhiteSpace(llm.Endpoint) || string.IsNullOrWhiteSpace(llm.ApiKey))
                    {
                        Console.WriteLine("[ERROR] Llm:Endpoint / Llm:ApiKey пустые — прогон не сможет обратиться к модели.");
                        Console.WriteLine("[HINT]  Задайте LLM__ENDPOINT/LLM__APIKEY в src/LLMNetHelper/.env или Llm:Endpoint/Llm:ApiKey в appsettings.json.");
                        Console.WriteLine("[HINT]  Либо используйте --dry-run (LLM не вызывается).");
                        return 4;
                    }
                }

                var agentRunner = host.Services.GetRequiredService<IAgentRunner>();

                // План 22, этап 2.7: инициализация хранилища — внутренняя деталь
                // реализации (ленивая); ручной вызов InitializeAsync больше не нужен.

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
    /// Режим <c>--chat</c>: консольный чат с LLM по вопросам из каталога
    /// скачанных репозиториев (<c>GitLab:CloneRoot</c>).
    /// <para>
    /// Hosted-сервисы (<c>HealthServer</c>, <c>NightlyScheduleHostedService</c>)
    /// стартуют ВСЕГДА — требование наблюдаемости: health-эндпоинт доступен
    /// и во время чата. Остановка хоста — в <c>finally</c>.
    /// </para>
    /// </summary>
    /// <param name="host">Собранный хост агента.</param>
    /// <param name="args">Аргументы командной строки (<c>--ask=</c>, <c>--repo=</c>, <c>--tools</c>).</param>
    /// <returns>0 — успех, 1 — сбой LLM/IO/хоста, 4 — неверная конфигурация.</returns>
    private static async Task<int> RunChatAsync(IHost host, string[] args)
    {
        var ask = GetOptionValue(args, "--ask");
        var repo = GetOptionValue(args, "--repo");
        var tools = args.Contains("--tools");

        ChatOptions chatOptions;
        LlmOptions llmOptions;
        GitLabOptions gitLabOptions;

        try
        {
            chatOptions = host.Services.GetRequiredService<ChatOptions>();
            llmOptions = host.Services.GetRequiredService<IOptions<LlmOptions>>().Value;
            gitLabOptions = host.Services.GetRequiredService<GitLabOptions>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Не удалось разрешить сервисы чата: {ex.Message}");
            return 4;
        }

        if (string.IsNullOrWhiteSpace(llmOptions.Endpoint) || string.IsNullOrWhiteSpace(llmOptions.ApiKey))
        {
            Console.WriteLine("[ERROR] Llm:Endpoint / Llm:ApiKey пустые — чат не сможет обратиться к модели.");
            Console.WriteLine("[HINT]  Задайте LLM__ENDPOINT/LLM__APIKEY в src/LLMNetHelper/.env или Llm:Endpoint/Llm:ApiKey в appsettings.json.");
            return 4;
        }

        var cloneRoot = Path.GetFullPath(gitLabOptions.CloneRoot);
        if (!Directory.Exists(cloneRoot))
        {
            Console.WriteLine($"[ERROR] Каталог репозиториев не найден: {cloneRoot}");
            Console.WriteLine("[HINT]  Сначала скачайте репозитории: run_LLMNetHelper.ps1 -CloneAll");
            return 4;
        }

        Console.WriteLine($"[INFO] Чат по репозиториям: {cloneRoot} (модель {llmOptions.Model})");
        Console.WriteLine("[INFO] Команды: /help — справка, /exit — выход.");

        // Hosted-сервисы стартуют ВСЕГДА (наблюдаемость): health-эндпоинт и
        // планировщик работают весь сеанс чата.
        try
        {
            await host.StartAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Не удалось запустить hosted-сервисы (health/планировщик): {ex.Message}");
            return 1;
        }

        try
        {
            var scope = new ChatScope(gitLabOptions, chatOptions);

            // Roslyn-слой для режима /tools: регистрируется всегда, но
            // инструменты появляются только при Roslyn:Enabled=true и /repo
            // (plan 21). План 22, этап 2.8: одна абстракция IRoslynToolFactory
            // вместо 5 конкретных Roslyn-классов. Разрешение — через GetService
            // (nullable), чтобы чат не падал, если слой не зарегистрирован.
            var session = new ChatSession(
                host.Services.GetRequiredService<Microsoft.Extensions.AI.IChatClient>(),
                scope,
                chatOptions,
                llmOptions,
                host.Services.GetRequiredService<ILoggerFactory>(),
                Console.In,
                Console.Out,
                host.Services.GetService<IRoslynToolFactory>(),
                // План 22, этап 6.1: промпты чата через абстракцию из контейнера.
                host.Services.GetRequiredService<IChatPromptBuilder>());

            if (tools)
            {
                Console.WriteLine($"[INFO] Roslyn-инструменты: {(session.RoslynAvailable ? "доступны (после /repo)" : "недоступны")}");
            }

            return await session.RunAsync(ask, repo, tools, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[INFO] Чат отменён.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Сбой чата: {Describe(ex)}");
            return 1;
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// Значение опции формата <c>--имя=значение</c> (префиксный разбор, как у
    /// <c>--hours=</c> в прогоне). <c>null</c> — опция не задана.
    /// </summary>
    private static string? GetOptionValue(string[] args, string prefix)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith(prefix + "=", StringComparison.Ordinal))
            {
                return arg[(prefix.Length + 1)..];
            }
        }

        return null;
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
            var suffix = r.Error != null ? " — " + r.Error : "";
            // Служебная пометка (автопереклонирование неполного клона,
            // очистка lock-файлов) показывается и при успехе.
            var note = r.Note != null ? (r.Success ? " — " : " | ") + r.Note : "";
            Console.WriteLine($"[{mark}] {r.Action,-8} {r.RepoName} ({r.DurationMs} ms){suffix}{note}");
        });

        try
        {
            var withProgress = new GitLabService(
                host.Services.GetRequiredService<IGitLabApiClient>(),
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
