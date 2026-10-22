using ArtasTools.MonitorAgent.Roslyn;
using Microsoft.Extensions.DependencyInjection;

namespace ArtasTools.MonitorAgent.CodeChange;

/// <summary>
/// Регистрация слоя применения правок.
/// </summary>
public static class CodeChangeRegistration
{
    /// <summary>
    /// Регистрирует сервисы транзакции правок.
    /// <paramref name="buildRunnerFactory"/> позволяет подменить прогон сборки
    /// в интеграционных тестах.
    /// </summary>
    public static IServiceCollection AddCodeChange(
        this IServiceCollection services,
        CodeChangeOptions options,
        Func<IServiceProvider, IBuildRunner>? buildRunnerFactory = null)
    {
        services.AddSingleton(options);
        services.AddSingleton<GitService>();
        services.AddSingleton<AstPatcher>();
        services.AddSingleton<CompilationValidator>();
        services.AddSingleton<ChangeTransaction>();

        if (buildRunnerFactory is not null)
        {
            services.AddSingleton(buildRunnerFactory);
        }
        else
        {
            services.AddSingleton<IBuildRunner, BuildTestRunner>();
        }

        return services;
    }
}
