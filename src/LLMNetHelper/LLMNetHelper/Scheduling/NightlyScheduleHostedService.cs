using LLMNetHelper.CodeChange;
using LLMNetHelper.Core.Storage;
using LLMNetHelper.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLMNetHelper.Scheduling;

/// <summary>
/// Фоновый сервис с планировщиком: запуск в 03:00 UTC каждый день.
/// Использует BackgroundService + TimeProvider для тестируемости.
/// </summary>
public sealed class NightlyScheduleHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptions<AgentOptions> _options;
    private readonly ILogger<NightlyScheduleHostedService> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly TimeProvider _timeProvider;

    public NightlyScheduleHostedService(
        IServiceProvider serviceProvider,
        IOptions<AgentOptions> options,
        ILogger<NightlyScheduleHostedService> logger,
        TimeProvider? timeProvider = null)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runAt = ParseRunTime(_options.Value.RunAtUtc);
        var nextRun = CalculateNextRun(runAt, _timeProvider.GetUtcNow().UtcDateTime);

        _logger.LogInformation("Планировщик запущен. Следующий запуск: {NextRun}", nextRun);

        // Ждём до следующего запуска
        var delay = nextRun - _timeProvider.GetUtcNow().UtcDateTime;
        if (delay.TotalMilliseconds > 0)
        {
            await Task.Delay(delay, stoppingToken);
        }

        // Проверяем catch-up: если нет успешного прогона за 24ч — запускаем немедленно
        await RunOnceAsync(stoppingToken, catchUp: true);

        // Основной цикл
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken, catchUp: false);

            // Рассчитываем следующий запуск
            nextRun = CalculateNextRun(runAt, _timeProvider.GetUtcNow().UtcDateTime);
            delay = nextRun - _timeProvider.GetUtcNow().UtcDateTime;

            _logger.LogInformation("Следующий запуск: {NextRun} (ожидание {Delay}с)",
                nextRun, delay.TotalSeconds);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Планировщик остановлен");
    }

    /// <summary>
    /// Ручной запуск прогона (для --run-now).
    /// </summary>
    public async Task RunNowAsync(CancellationToken ct)
    {
        if (!await _semaphore.WaitAsync(0, ct))
        {
            throw new InvalidOperationException("Прогон уже выполняется");
        }

        try
        {
            await RunOnceAsync(ct, catchUp: false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task RunOnceAsync(CancellationToken ct, bool catchUp)
    {
        if (!await _semaphore.WaitAsync(0, ct))
        {
            _logger.LogWarning("Прогон пропущен: уже выполняется");
            return;
        }

        try
        {
            _logger.LogInformation(catchUp ? "Catch-up прогон..." : "Начало ночного прогона...");

            // Обновление репозитория перед прогоном.
            var gitService = _serviceProvider.GetRequiredService<CodeChange.GitService>();
            var fetched = await gitService.FetchAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Git fetch: {Fetched}, catch-up pull: {CatchUp}", fetched, catchUp);

            if (catchUp && fetched)
            {
                var pull = await gitService.PullHardAsync(ct).ConfigureAwait(false);
                if (!pull.Succeeded)
                {
                    _logger.LogWarning(
                        "Catch-up pull не удался (не критично): {Error}", pull.Describe());
                }
            }

            // Хранилище и агент — синглтоны; scope нужен для будущих scoped-зависимостей.
            var factStore = _serviceProvider.GetRequiredService<Abstractions.IFactStore>();
            await factStore.InitializeAsync(ct);

            var runner = _serviceProvider.GetRequiredService<IAgentRunner>();

            var exitCode = await runner.RunAsync(
                dryRun: false,
                catchUp,
                since: null,
                hours: null,
                ct);

            _logger.LogInformation("Прогон завершён с кодом {Code}", exitCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка прогона");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    internal static TimeSpan ParseRunTime(string runAt)
    {
        var parts = runAt.Split(':');
        return new TimeSpan(int.Parse(parts[0]), int.Parse(parts[1]), 0);
    }

    internal static DateTime CalculateNextRun(TimeSpan runTime, DateTime nowUtc)
    {
        var next = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day,
            runTime.Hours, runTime.Minutes, 0, DateTimeKind.Utc);

        if (next <= nowUtc)
            next = next.AddDays(1);

        return next;
    }
}
