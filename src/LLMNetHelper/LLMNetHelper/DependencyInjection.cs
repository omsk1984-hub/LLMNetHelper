using LLMNetHelper.Abstractions;
using LLMNetHelper.Agent;
using LLMNetHelper.Agent.Orchestration;
using LLMNetHelper.CodeChange;
using LLMNetHelper.Context;
using LLMNetHelper.Core.Snapshot;
using LLMNetHelper.Core.Storage;
using LLMNetHelper.Health;
using LLMNetHelper.Infrastructure.Context;
using LLMNetHelper.Infrastructure.Loki;
using LLMNetHelper.Infrastructure.Prometheus;
using LLMNetHelper.Infrastructure.Reporting;
using LLMNetHelper.Llm;
using LLMNetHelper.Roslyn;
using LLMNetHelper.Scheduling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LLMNetHelper;

/// <summary>
/// Регистрация DI-зависимостей агента.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Регистрирует все слои агента: сбор фактов, контекст, анализ кода,
    /// оркестратор, применение правок и отчётность.
    /// </summary>
    public static IServiceCollection AddMonitorAgent(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // --- Options ---
        services.Configure<AgentOptions>(configuration.GetSection("Agent"));
        services.Configure<LokiOptions>(configuration.GetSection("Loki"));
        services.Configure<PrometheusOptions>(configuration.GetSection("Prometheus"));
        services.Configure<LlmOptions>(configuration.GetSection("Llm"));

        var contextOptions = configuration.GetSection(ContextOptions.SectionName).Get<ContextOptions>()
            ?? new ContextOptions();
        var localFolderOptions = configuration.GetSection(LocalFolderOptions.SectionName).Get<LocalFolderOptions>()
            ?? new LocalFolderOptions();
        var roslynOptions = configuration.GetSection(RoslynOptions.SectionName).Get<RoslynOptions>()
            ?? new RoslynOptions();
        var codeChangeOptions = configuration.GetSection(CodeChangeOptions.SectionName).Get<CodeChangeOptions>()
            ?? new CodeChangeOptions();
        var orchestrationOptions = configuration
            .GetSection(AgentOrchestrationOptions.SectionName)
            .Get<AgentOrchestrationOptions>()
            ?? new AgentOrchestrationOptions();

        // --- Хранилище фактов ---
        services.AddSingleton<IFactStore>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            return new SqliteFactStore(options.DatabasePath);
        });

        // --- HTTP-клиенты источников ---
        services.AddSingleton<ILokiClient, LokiHttpClient>();
        services.AddSingleton<IPrometheusClient, PrometheusHttpClient>();

        // --- Ядро: сводка и отчётность ---
        services.AddSingleton<SnapshotBuilder>();
        services.AddSingleton<IReportWriter>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            return new FileReportWriter(options.ReportsDir);
        });

        // --- Внешний контекст: источники регистрируются списком ---
        services.AddContextLayer(contextOptions);
        services.AddSingleton(localFolderOptions);

        if (contextOptions.Enabled)
        {
            // Добавление нового источника (Confluence, Jira, pgvector) —
            // одна строка здесь; прикладной код не меняется.
            services.AddContextSource<LocalFolderContextSource>();
        }

        // --- Анализ кода ---
        services.AddRoslynAnalysis(roslynOptions);

        // --- LLM-транспорты ---
        services.AddMonitorAgentLlm(configuration);

        // --- Инструменты и оркестратор ---
        services.AddAgentTools();
        services.AddAgentOrchestrator(orchestrationOptions);

        // --- Применение правок ---
        services.AddCodeChange(codeChangeOptions);

        // --- Прогон ---
        services.AddSingleton<IAgentRunner, AgentRunner>();

        // --- Хостинг ---
        services.AddHostedService<NightlyScheduleHostedService>();
        services.AddSingleton<AgentHealthState>();

        return services;
    }
}
