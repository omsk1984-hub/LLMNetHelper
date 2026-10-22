using ArtasTools.MonitorAgent.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ArtasTools.MonitorAgent.Context;

/// <summary>
/// Регистрация источников контекста.
/// <para>
/// Добавление нового источника (<c>Confluence</c>, <c>Jira</c>, <c>pgvector</c>) — это
/// одна строка <c>services.AddContextSource<T>(...)</c> здесь либо в
/// регистрации конкретного модуля. Прикладной код не меняется.
/// </para>
/// </summary>
public static class ContextRegistration
{
    /// <summary>
    /// Регистрирует агрегатор и параметры слоя контекста.
    /// </summary>
    public static IServiceCollection AddContextLayer(this IServiceCollection services, ContextOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<ContextAggregator>();
        return services;
    }

    /// <summary>
    /// Регистрирует источник контекста как синглтон.
    /// </summary>
    public static IServiceCollection AddContextSource<TSource>(this IServiceCollection services)
        where TSource : class, IContextSource
    {
        services.AddSingleton<IContextSource, TSource>();
        return services;
    }

    /// <summary>
    /// Регистрирует источник контекста, создаваемый фабрикой.
    /// </summary>
    public static IServiceCollection AddContextSource(
        this IServiceCollection services,
        Func<IServiceProvider, IContextSource> factory)
    {
        services.AddSingleton(factory);
        return services;
    }
}
