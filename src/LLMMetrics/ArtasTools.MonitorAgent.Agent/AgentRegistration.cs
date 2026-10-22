using ArtasTools.MonitorAgent.Abstractions;
using ArtasTools.MonitorAgent.Agent.Orchestration;
using ArtasTools.MonitorAgent.Agent.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ArtasTools.MonitorAgent.Agent;

/// <summary>
/// Регистрация агентского слоя: инструменты, реестр, оркестратор.
/// </summary>
public static class AgentRegistration
{
    /// <summary>
    /// Регистрирует инструменты и реестр.
    /// </summary>
    public static IServiceCollection AddAgentTools(this IServiceCollection services)
    {
        services.AddSingleton<IAgentTool, DocsSearchTool>();
        services.AddSingleton<IAgentTool, CodeSearchTool>();
        services.AddSingleton<IAgentTool, CodeReadTool>();
        services.AddSingleton<IAgentTool, CodeCallGraphTool>();
        services.AddSingleton<IAgentTool, CodeFailurePointsTool>();

        services.AddSingleton<IToolRegistry>(sp =>
            new ToolRegistry(sp.GetServices<IAgentTool>()));

        return services;
    }

    /// <summary>
    /// Регистрирует нативный оркестратор.
    /// </summary>
    public static IServiceCollection AddNativeOrchestrator(
        this IServiceCollection services,
        AgentOrchestrationOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<IAgentOrchestrator, NativeAgentOrchestrator>();
        return services;
    }

    /// <summary>
    /// Регистрирует оркестратор по имени из конфигурации.
    /// <para>
    /// "native" — доступен всегда. "semantic-kernel" включается только при
    /// наличии пакетов SK; иначе выбрасывается понятная ошибка конфигурации.
    /// </para>
    /// </summary>
    public static IServiceCollection AddAgentOrchestrator(
        this IServiceCollection services,
        AgentOrchestrationOptions options)
    {
        services.AddSingleton(options);

        return options.Orchestrator.ToLowerInvariant() switch
        {
            "native" or "" => services.AddSingleton<IAgentOrchestrator, NativeAgentOrchestrator>(),
            "semantic-kernel" or "sk" => throw new InvalidOperationException(
                "Оркестратор 'semantic-kernel' требует отдельного проекта ArtasTools.MonitorAgent.Orchestration.SemanticKernel. " +
                "Пока он не подключён, используйте Agent:Orchestration:Orchestrator=native."),
            var other => throw new InvalidOperationException(
                $"Неизвестный оркестратор: '{other}'. Допустимо: native, semantic-kernel.")
        };
    }

    /// <summary>
    /// Регистрирует <see cref="IChatClient"/> из готовой фабрики.
    /// Вызывается из проекта Llm, где живёт транспорт.
    /// </summary>
    public static IServiceCollection AddChatClient(
        this IServiceCollection services,
        Func<IServiceProvider, IChatClient> factory)
    {
        services.AddSingleton(factory);
        return services;
    }
}
