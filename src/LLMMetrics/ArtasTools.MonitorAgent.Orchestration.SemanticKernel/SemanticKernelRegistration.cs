#if SK_ENABLED

using ArtasTools.MonitorAgent.Abstractions;
using ArtasTools.MonitorAgent.Agent.Orchestration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArtasTools.MonitorAgent.Orchestration.SemanticKernel;

/// <summary>
/// Регистрация SK-оркестратора.
/// </summary>
public static class SemanticKernelRegistration
{
    /// <summary>
    /// Регистрирует <see cref="SemanticKernelAgentOrchestrator"/> в качестве
    /// реализации <see cref="IAgentOrchestrator"/>.
    /// <para>
    /// Требует, чтобы в контейнере уже был зарегистрирован <see cref="IChatClient"/>
    /// (см. <c>LlmClientRegistration</c>).
    /// </para>
    /// </summary>
    public static IServiceCollection AddSemanticKernelOrchestrator(
        this IServiceCollection services,
        AgentOrchestrationOptions options)
    {
        services.AddSingleton(options);

        services.AddSingleton<IAgentOrchestrator>(sp =>
        {
            var chatClient = sp.GetRequiredService<IChatClient>();
            var registry = sp.GetRequiredService<IToolRegistry>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

            return new SemanticKernelAgentOrchestrator(
                chatClient,
                registry,
                options,
                loggerFactory);
        });

        return services;
    }
}

#endif
