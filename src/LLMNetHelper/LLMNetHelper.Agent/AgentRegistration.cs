using LLMNetHelper.Abstractions;
using LLMNetHelper.Agent.Orchestration;
using LLMNetHelper.Agent.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace LLMNetHelper.Agent;

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
                "Оркестратор 'semantic-kernel' требует подключения LLMCore.Orchestration.SemanticKernel " +
                "(AddSemanticKernelOrchestrator). Пока он не подключён, используйте Agent:Orchestration:Orchestrator=native."),
            var other => throw new InvalidOperationException(
                $"Неизвестный оркестратор: '{other}'. Допустимо: native, semantic-kernel.")
        };
    }
}
