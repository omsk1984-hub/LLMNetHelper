using Microsoft.Extensions.DependencyInjection;

namespace LLMNetHelper.Roslyn;

/// <summary>
/// Регистрация слоя анализа кода.
/// </summary>
public static class RoslynRegistration
{
    /// <summary>
    /// Регистрирует загрузчик решения и сервисы анализа.
    /// При <c>Roslyn:Enabled = false</c> сервисы регистрируются,
    /// но <see cref="SolutionLoader.LoadAsync"/> возвращает null,
    /// и агент работает без анализа кода.
    /// </summary>
    public static IServiceCollection AddRoslynAnalysis(
        this IServiceCollection services,
        RoslynOptions options)
    {
        services.AddSingleton(options);
        // План 22, этап 2.4: регистрация по интерфейсу ISolutionLoader.
        services.AddSingleton<ISolutionLoader, SolutionLoader>();
        services.AddSingleton<CodeSearchService>();
        services.AddSingleton<CodeReader>();
        services.AddSingleton<CallGraphService>();
        services.AddSingleton<StructureAnalyzer>();
        services.AddSingleton<FailurePointAnalyzer>();
        return services;
    }
}
