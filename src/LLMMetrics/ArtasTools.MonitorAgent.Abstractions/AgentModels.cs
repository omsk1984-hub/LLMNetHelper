namespace ArtasTools.MonitorAgent.Abstractions;

/// <summary>
/// Гипотеза о корневой причине инцидента.
/// </summary>
/// <param name="RootCause">Формулировка причины.</param>
/// <param name="Confidence">"high" | "medium" | "low".</param>
/// <param name="Evidence">Чем подтверждается.</param>
/// <param name="References">Ссылки на доказательства: лог, метрика, файл:строка.</param>
public sealed record Hypothesis(
    string RootCause,
    string Confidence,
    string Evidence,
    IReadOnlyList<string> References)
{
    /// <summary>Пустая гипотеза-заглушка.</summary>
    public static Hypothesis Empty { get; } = new("", "low", "", []);
}

/// <summary>
/// Предлагаемое изменение кода.
/// </summary>
/// <param name="FilePath">Путь к файлу относительно корня репозитория.</param>
/// <param name="SymbolName">Имя метода или класса, который меняем.</param>
/// <param name="ChangeKind">"ReplaceMethodBody" | "InsertNullCheck".</param>
/// <param name="NewCode">Новый код (тело метода).</param>
/// <param name="Explanation">Зачем это изменение.</param>
public sealed record ProposedChange(
    string FilePath,
    string SymbolName,
    string ChangeKind,
    string NewCode,
    string Explanation);

/// <summary>
/// План исправления.
/// </summary>
/// <param name="Rationale">Обоснование плана.</param>
/// <param name="Changes">Предлагаемые изменения.</param>
/// <param name="Risks">Риски применения.</param>
/// <param name="RequiresManualReview">Требуется ручная проверка перед применением.</param>
public sealed record FixPlan(
    string Rationale,
    IReadOnlyList<ProposedChange> Changes,
    IReadOnlyList<string> Risks,
    bool RequiresManualReview)
{
    /// <summary>Пустой план.</summary>
    public static FixPlan Empty { get; } = new("", [], [], true);

    /// <summary>Есть ли что применять.</summary>
    public bool HasChanges => Changes.Count > 0;
}

/// <summary>
/// Результат диагностики инцидента.
/// </summary>
/// <param name="Verdict">"OK" | "WARNING" | "CRITICAL" | "UNKNOWN".</param>
/// <param name="Summary">Краткая сводка.</param>
/// <param name="Findings">Находки (переиспользуется существующая модель <c>Finding</c> из парсера).</param>
/// <param name="Hypotheses">Гипотезы о причине.</param>
/// <param name="QuestionsForOwner">Вопросы владельцу сервиса.</param>
/// <param name="ToolCallCount">Сколько вызовов инструментов потребовалось.</param>
/// <param name="Orchestrator">Имя оркестратора: "native" | "semantic-kernel".</param>
public sealed record Diagnosis(
    string Verdict,
    string Summary,
    IReadOnlyList<object> Findings,
    IReadOnlyList<Hypothesis> Hypotheses,
    IReadOnlyList<string> QuestionsForOwner,
    int ToolCallCount,
    string Orchestrator);

/// <summary>
/// Контекст одного агентского прогона: всё, что агент знает до начала работы.
/// </summary>
/// <param name="RunId">Идентификатор прогона в БД.</param>
/// <param name="WindowFrom">Начало окна анализа (UTC).</param>
/// <param name="WindowTo">Конец окна анализа (UTC).</param>
/// <param name="SnapshotText">Компактная сводка фактов.</param>
/// <param name="ContextDocuments">Найденные внешние документы.</param>
/// <param name="CodeStructureSummary">Краткая сводка по коду (если Roslyn доступен).</param>
public sealed record AgentContext(
    long RunId,
    DateTime WindowFrom,
    DateTime WindowTo,
    string SnapshotText,
    IReadOnlyList<ContextDocument> ContextDocuments,
    string? CodeStructureSummary);
