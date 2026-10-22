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
        services.AddSingleton<SolutionLoader>();
        services.AddSingleton<CodeSearchService>();
        services.AddSingleton<CodeReader>();
        services.AddSingleton<CallGraphService>();
        services.AddSingleton<StructureAnalyzer>();
        return services;
    }
}
