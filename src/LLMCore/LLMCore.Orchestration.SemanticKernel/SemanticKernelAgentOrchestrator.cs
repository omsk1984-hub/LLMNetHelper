#if SK_ENABLED

using System.Text.Json;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Agent.Orchestration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace LLMCore.Orchestration.SemanticKernel;

/// <summary>
/// Реализация <see cref="IAgentOrchestrator"/> на Semantic Kernel 1.81.0.
/// <para>
/// Семантически эквивалентна <c>NativeAgentOrchestrator</c>: тот же системный
/// промпт, те же инструменты (через <see cref="SkPluginAdapter"/>), тот же
/// формат ответа. Отличается только «движок» — чат-клиент приходит из SK.
/// </para>
/// <para>
/// Контракт <see cref="IChatClient"/> в SK 1.81.0 совпадает с MEAI 10.5.0,
/// поэтому нативный оркестратор можно переиспользовать, подменив транспорт:
/// SK-чат-клиент оборачивается в MEAI-совместимый адаптер и передаётся
/// в тот же ReAct-цикл. Это гарантирует паритет поведения.
/// </para>
/// </summary>
public sealed class SemanticKernelAgentOrchestrator : IAgentOrchestrator
{
    private readonly IAgentOrchestrator _inner;
    private readonly IToolRegistry _registry;
    private readonly Kernel _kernel;

    public SemanticKernelAgentOrchestrator(
        IChatClient skChatClient,
        IToolRegistry registry,
        AgentOrchestrationOptions options,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(skChatClient);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _registry = registry;

        // Ядро регистрирует инструменты — это даёт доступ к SK-инфраструктуре
        // (метаданные функций, фильтры), даже если цикл нативный.
        _kernel = BuildKernel(registry);

        _inner = new NativeAgentOrchestrator(
            skChatClient,
            registry,
            options,
            loggerFactory.CreateLogger<NativeAgentOrchestrator>());
    }

    /// <inheritdoc />
    public string ProviderName => "semantic-kernel";

    /// <summary>
    /// Ядро SK с зарегистрированными инструментами.
    /// </summary>
    public Kernel Kernel => _kernel;

    /// <inheritdoc />
    public Task<Diagnosis> DiagnoseAsync(AgentContext context, CancellationToken ct = default)
    {
        return _inner.DiagnoseAsync(context, ct);
    }

    /// <inheritdoc />
    public Task<FixPlan> PlanFixAsync(
        Diagnosis diagnosis,
        AgentContext context,
        CancellationToken ct = default)
    {
        return _inner.PlanFixAsync(diagnosis, context, ct);
    }

    /// <summary>
    /// Собирает <see cref="Kernel"/> с плагинами инструментов.
    /// </summary>
    private static Kernel BuildKernel(IToolRegistry registry)
    {
        var builder = Kernel.CreateBuilder();

        var functions = SkPluginAdapter.ToKernelFunctions(registry);

        foreach (var function in functions)
        {
            // Плагин регистрируется под именем инструмента —
            // так модель видит его в списке доступных функций.
            builder.Plugins.AddFromFunctions(function.Name, [function]);
        }

        return builder.Build();
    }

    /// <summary>
    /// Список зарегистрированных в SK функций (для диагностики).
    /// </summary>
    public IReadOnlyList<string> RegisteredFunctions =>
        _kernel.Plugins
            .SelectMany(p => p.Select(f => $"{p.Name}.{f.Name}"))
            .ToArray();
}

#endif
