#if SK_ENABLED

using ArtasTools.MonitorAgent.Abstractions;
using Microsoft.SemanticKernel;

namespace ArtasTools.MonitorAgent.Orchestration.SemanticKernel;

/// <summary>
/// Адаптер <see cref="IAgentTool"/> → <see cref="KernelFunction"/>.
/// <para>
/// Один и тот же инструмент используется и нативным оркестратором
/// (через MEAI), и SK-оркестратором — прикладной код не меняется.
/// </para>
/// <para>
/// Важно: в отличие от плана [`plans/01.md`](plans/01.md:1), здесь используется
/// <see cref="KernelFunctionFactory.CreateFromMethod(Delegate, string?, string?, System.Collections.Generic.IEnumerable{KernelParameterMetadata}?, KernelReturnParameterMetadata?, Microsoft.Extensions.Logging.ILoggerFactory?)"/>
/// — именно эта перегрузка существует в SK 1.81.0. Перегрузки
/// <c>kernel.InvokeAsync<T>(pluginName, functionName, args)</c> из плана в SK нет.
/// </para>
/// </summary>
public static class SkPluginAdapter
{
    /// <summary>
    /// Имя единственного параметра всех инструментов: JSON со всеми аргументами.
    /// </summary>
    private const string ArgumentsParameter = "parametersJson";

    /// <summary>
    /// Оборачивает инструмент в <see cref="KernelFunction"/>.
    /// </summary>
    public static KernelFunction ToKernelFunction(IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        // Делегат принимает JSON-строку и возвращает результат инструмента.
        Func<string, CancellationToken, Task<string>> method = async (json, ct) =>
            await tool.InvokeAsync(json, ct).ConfigureAwait(false);

        return KernelFunctionFactory.CreateFromMethod(
            method,
            functionName: tool.Name,
            description: tool.Description);
    }

    /// <summary>
    /// Оборачивает все инструменты реестра.
    /// </summary>
    public static IReadOnlyList<KernelFunction> ToKernelFunctions(IToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return registry.Tools
            .Select(ToKernelFunction)
            .ToArray();
    }

    /// <summary>
    /// Имя параметра, под которым оркестратор передаёт JSON инструменту.
    /// </summary>
    public static string ParameterName => ArgumentsParameter;
}

#endif
