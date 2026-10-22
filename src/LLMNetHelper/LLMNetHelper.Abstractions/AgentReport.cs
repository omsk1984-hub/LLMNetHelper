namespace LLMNetHelper.Abstractions;

/// <summary>
/// Дополнительные сведения агентского прогона для отчёта.
/// <para>
/// Отдельная модель, а не расширение <see cref="AnalysisResult"/>: анализ
/// (вердикт, находки) формируется парсером ответа LLM, а агентские данные
/// (гипотезы, прочитанный контекст, правки) — оркестратором и транзакцией.
/// </para>
/// </summary>
/// <param name="Orchestrator">Имя оркестратора: "native" | "semantic-kernel".</param>
/// <param name="Hypotheses">Гипотезы о корневой причине.</param>
/// <param name="ContextDocuments">Документы, реально прочитанные агентом.</param>
/// <param name="UnavailableSources">Недоступные источники контекста.</param>
/// <param name="ToolCalls">Сколько вызовов инструментов сделано.</param>
/// <param name="CodeStructure">Краткая сводка по коду (если Roslyn доступен).</param>
/// <param name="FixPlanSummary">Краткое описание плана правки.</param>
/// <param name="ChangeBranch">Ветка с правками (если создана).</param>
/// <param name="ChangeCommit">SHA коммита (если создан).</param>
/// <param name="ChangeMessage">Сообщение о результате применения правок.</param>
/// <param name="PatchPath">Путь к файлу patch.diff (если сформирован).</param>
public sealed record AgentReport(
    string Orchestrator,
    IReadOnlyList<Hypothesis> Hypotheses,
    IReadOnlyList<ContextDocument> ContextDocuments,
    IReadOnlyList<string> UnavailableSources,
    int ToolCalls,
    string? CodeStructure,
    string? FixPlanSummary,
    string? ChangeBranch,
    string? ChangeCommit,
    string? ChangeMessage,
    string? PatchPath)
{
    /// <summary>Пустой агентский отчёт (режим без агента).</summary>
    public static AgentReport Empty { get; } = new(
        Orchestrator: "",
        Hypotheses: [],
        ContextDocuments: [],
        UnavailableSources: [],
        ToolCalls: 0,
        CodeStructure: null,
        FixPlanSummary: null,
        ChangeBranch: null,
        ChangeCommit: null,
        ChangeMessage: null,
        PatchPath: null);
}
