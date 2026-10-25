using LLMNetHelper.Abstractions;
using LLMNetHelper.Agent;
using LLMNetHelper.Agent.Orchestration;
using LLMNetHelper.Chat;
using LLMNetHelper.CodeChange;
using LLMNetHelper.Context;
using LLMNetHelper.Core.Collection;
using LLMNetHelper.Core.Reporting;
using LLMNetHelper.Core.Snapshot;
using LLMNetHelper.Core.Storage;
using LLMNetHelper.GitLab;
using LLMNetHelper.Health;
using LLMNetHelper.Infrastructure.Context;
using LLMNetHelper.Infrastructure.Loki;
using LLMNetHelper.Infrastructure.Prometheus;
using LLMNetHelper.Infrastructure.Reporting;
using LLMCore.Abstractions;
using LLMCore.Llm;
using LLMNetHelper.Roslyn;
using LLMNetHelper.Running;
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
        var gitLabOptions = configuration.GetSection(GitLabOptions.SectionName).Get<GitLabOptions>()
            ?? new GitLabOptions();
        var orchestrationOptions = configuration
            .GetSection(AgentOrchestrationOptions.SectionName)
            .Get<AgentOrchestrationOptions>()
            ?? new AgentOrchestrationOptions();
        var chatOptions = configuration
            .GetSection(ChatOptions.SectionName)
            .Get<ChatOptions>()
            ?? new ChatOptions();

        // Режим чата (--chat): параметры областью поиска CloneRoot. Регистрация
        // готового синглтона, а не IOptions<>: чат создаётся вне hosted-слоя.
        services.AddSingleton(chatOptions);

        // План 22, этап 6.1: промпты чата — экземплярный сервис по интерфейсу
        // (подмена в тестах, версионирование).
        services.AddSingleton<IChatPromptBuilder, ChatPromptBuilder>();

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
        services.AddSingleton<ISnapshotBudgetEnforcer, TokenBudgetGuard>();
        // План 22, этап 2.5: регистрация по интерфейсу ISnapshotBuilder.
        services.AddSingleton<ISnapshotBuilder, SnapshotBuilder>();
        services.AddSingleton<IReportWriter>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            return new FileReportWriter(options.ReportsDir);
        });
        // План 22, этап 6.2: форматы отчёта — экземплярные сервисы по интерфейсам
        // (подмена формата в тестах и верционирование без правки шага прогона).
        services.AddSingleton<IMarkdownReportRenderer, MarkdownReportRenderer>();
        services.AddSingleton<IJsonReportRenderer, JsonReportRenderer>();

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

        // План 22, этап 2.8: фабрика Roslyn-инструментов чата (регистрация по
        // интерфейсу; конкретные Roslyn-сервисы инкапсулированы внутри).
        services.AddSingleton<IRoslynToolFactory, RoslynToolFactory>();

        // --- LLM-транспорты ---
        services.AddMonitorAgentLlm(configuration);

        // --- Инструменты и оркестратор ---
        services.AddAgentTools();
        services.AddAgentOrchestrator(orchestrationOptions);

        // --- Применение правок ---
        services.AddCodeChange(codeChangeOptions);

        // --- GitLab: клонирование и обновление репозиториев ---
        services.AddSingleton(gitLabOptions);
        if (gitLabOptions.Enabled)
        {
            services.AddGitLab();
        }

        // --- Шаги прогона ---
        services.AddSingleton<IRunStep, CollectLokiStep>();
        services.AddSingleton<IRunStep, CollectPrometheusStep>();
        services.AddSingleton<IRunStep, BuildSnapshotStep>();
        services.AddSingleton<IRunStep, CollectContextStep>();
        services.AddSingleton<IRunStep, DescribeCodeStep>();
        services.AddSingleton<IRunStep, DiagnoseStep>();
        services.AddSingleton<IRunStep, PlanFixStep>();
        services.AddSingleton<IRunStep, ApplyChangesStep>();
        services.AddSingleton<IRunStep, GenerateReportStep>();

        // --- Пайплайн и прогон ---
        services.AddSingleton<IAgentPipeline, AgentPipeline>();
        services.AddSingleton<IAgentRunner, AgentRunner>();

        // --- Хостинг ---
        services.AddHostedService<NightlyScheduleHostedService>();
        services.AddSingleton<AgentHealthState>();

        // Health port из конфига
        var agentOptionsConfig = configuration.GetSection("Agent").Get<AgentOptions>() ?? new AgentOptions();
        services.AddKeyedSingleton("HealthPort", agentOptionsConfig.HealthPort.ToString());
        services.AddHostedService<HealthServer>();

        return services;
    }
}
