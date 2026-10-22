using ArtasTools.MonitorAgent.Abstractions;
using ArtasTools.MonitorAgent.Agent;
using ArtasTools.MonitorAgent.Agent.Orchestration;
using ArtasTools.MonitorAgent.CodeChange;
using ArtasTools.MonitorAgent.Context;
using ArtasTools.MonitorAgent.Core.Snapshot;
using ArtasTools.MonitorAgent.Core.Storage;
using ArtasTools.MonitorAgent.Health;
using ArtasTools.MonitorAgent.Infrastructure.Context;
using ArtasTools.MonitorAgent.Infrastructure.Loki;
using ArtasTools.MonitorAgent.Infrastructure.Prometheus;
using ArtasTools.MonitorAgent.Infrastructure.Reporting;
using ArtasTools.MonitorAgent.Llm;
using ArtasTools.MonitorAgent.Roslyn;
using ArtasTools.MonitorAgent.Scheduling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArtasTools.MonitorAgent;

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
