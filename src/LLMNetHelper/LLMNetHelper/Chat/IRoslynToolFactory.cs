using LLMNetHelper.Abstractions;
using LLMNetHelper.Chat.Tools;
using LLMNetHelper.Roslyn;

namespace LLMNetHelper.Chat;

/// <summary>
/// Фабрика Roslyn-инструментов чата (план 22, этап 2.8). Инкапсулирует
/// зависимости от конкретных Roslyn-сервисов (<c>SolutionLoader</c>,
/// <c>CodeSearchService</c>, <c>CallGraphService</c>, <c>FailurePointAnalyzer</c>,
/// <c>RoslynOptions</c>), чтобы <see cref="ChatSession"/> не принимал их
/// по отдельности (DIP: 5 конкретных классов → 1 абстракция).
/// <para>
/// Инструменты доступны только при: слой включён (<c>Roslyn:Enabled</c>),
/// выбрана конкретная область <c>/repo</c> и в ней найдено решение
/// (требование плана 21).
/// </para>
/// </summary>
public interface IRoslynToolFactory
{
    /// <summary>Доступны ли Roslyn-инструменты для области <paramref name="scope"/>.</summary>
    bool IsAvailable(ChatScope scope);

    /// <summary>
    /// Создаёт список Roslyn-инструментов для области. Пусто, если
    /// <see cref="IsAvailable"/> вернул false.
    /// </summary>
    IReadOnlyList<IAgentTool> CreateTools(ChatScope scope);

    /// <summary>Человекочитаемый статус слоя для команды <c>/status</c>.</summary>
    string DescribeStatus(ChatScope scope);
}

/// <summary>
/// Реализация <see cref="IRoslynToolFactory"/> поверх конкретных Roslyn-сервисов.
/// Регистрируется в DI как синглтон; инструменты создаются на каждый вопрос
/// (область может меняться командой <c>/repo</c>).
/// </summary>
public sealed class RoslynToolFactory : IRoslynToolFactory
{
    private readonly ISolutionLoader _loader;
    private readonly CodeSearchService _codeSearch;
    private readonly CallGraphService _callGraph;
    private readonly FailurePointAnalyzer _failureAnalyzer;
    private readonly RoslynOptions _roslynOptions;

    public RoslynToolFactory(
        ISolutionLoader loader,
        CodeSearchService codeSearch,
        CallGraphService callGraph,
        FailurePointAnalyzer failureAnalyzer,
        RoslynOptions roslynOptions)
    {
        _loader = loader;
        _codeSearch = codeSearch;
        _callGraph = callGraph;
        _failureAnalyzer = failureAnalyzer;
        _roslynOptions = roslynOptions;
    }

    /// <inheritdoc />
    public bool IsAvailable(ChatScope scope) =>
        _roslynOptions.Enabled
        && scope.TryGetSolutionPath(out _);

    /// <inheritdoc />
    public IReadOnlyList<IAgentTool> CreateTools(ChatScope scope)
    {
        if (!IsAvailable(scope))
        {
            return [];
        }

        return
        [
            new RepoFailurePointsTool(scope, _loader, _failureAnalyzer),
            new RepoCallGraphTool(scope, _callGraph),
            new RepoSymbolSearchTool(scope, _codeSearch)
        ];
    }

    /// <inheritdoc />
    public string DescribeStatus(ChatScope scope)
    {
        if (!_roslynOptions.Enabled)
        {
            return "выключен (Roslyn:Enabled=false)";
        }

        if (scope.CurrentRepo is null)
        {
            return "готов, требуется /repo <имя> (анализ только для одного репозитория)";
        }

        return IsAvailable(scope)
            ? "включён: repo_failure_points / repo_call_graph / repo_symbol_search"
            : "включён, но решение не найдено в " + scope.RootPath;
    }
}
