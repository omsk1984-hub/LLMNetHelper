using LLMNetHelper.Roslyn;
using Microsoft.Extensions.DependencyInjection;

namespace LLMNetHelper.CodeChange;

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
        // План 22, этап 2.1: регистрация по интерфейсу (единственная реализация — GitService).
        services.AddSingleton<IGitService, GitService>();
        // План 22, этап 2.2: регистрация по интерфейсам IAstPatcher/ICompilationValidator.
        services.AddSingleton<IAstPatcher, AstPatcher>();
        services.AddSingleton<ICompilationValidator, CompilationValidator>();
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
