using LLMNetHelper.Abstractions;
using LLMNetHelper.GitLab;
using Microsoft.Extensions.DependencyInjection;

namespace LLMNetHelper.GitLab;

/// <summary>
/// Регистрация GitLab-функциональности в DI.
/// </summary>
public static class GitLabRegistration
{
    /// <summary>
    /// Регистрирует GitLab-сервисы и инструменты агента.
    /// </summary>
    public static IServiceCollection AddGitLab(this IServiceCollection services)
    {
        // HTTP-клиент для GitLab API
        services.AddHttpClient<GitLabApiClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        // Сервисы
        services.AddSingleton<GitLabService>();

        // Инструменты агента
        services.AddSingleton<IAgentTool, GitLabListReposTool>();
        services.AddSingleton<IAgentTool, GitLabCloneAllTool>();
        services.AddSingleton<IAgentTool, GitLabUpdateRepoTool>();

        return services;
    }
}
