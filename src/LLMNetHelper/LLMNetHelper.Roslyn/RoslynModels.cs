namespace LLMNetHelper.Roslyn;

/// <summary>
/// Найденный символ (тип или член) с координатой в исходниках.
/// </summary>
/// <param name="Kind">"class" | "interface" | "record" | "method" | "property" | "field".</param>
/// <param name="FullName">Полное имя, включая пространства имён и тип-контейнер.</param>
/// <param name="FileName">Путь к файлу относительно корня решения.</param>
/// <param name="Line">Строка (1-based).</param>
public sealed record CodeSymbol(
    string Kind,
    string FullName,
    string FileName,
    int Line);

/// <summary>
/// Исходный текст члена с номерами строк.
/// </summary>
/// <param name="Symbol">Символ, к которому относится текст.</param>
/// <param name="Text">Исходный текст (обрезан по <c>MaxSourceChars</c>).</param>
/// <param name="Truncated">Был ли текст обрезан.</param>
public sealed record CodeSnippet(
    CodeSymbol Symbol,
    string Text,
    bool Truncated);

/// <summary>
/// Регистрация сервиса в DI-контейнере.
/// </summary>
/// <param name="ServiceType">Тип сервиса (<c>typeof(...)</c>).</param>
/// <param name="ImplementationType">Тип реализации.</param>
/// <param name="Lifetime">"singleton" | "scoped" | "transient".</param>
/// <param name="FileName">Путь к файлу.</param>
/// <param name="Line">Строка.</param>
public sealed record ServiceRegistration(
    string ServiceType,
    string ImplementationType,
    string Lifetime,
    string FileName,
    int Line);

/// <summary>
/// Итог анализа структуры решения.
/// </summary>
/// <param name="ProjectCount">Сколько проектов в решении.</param>
/// <param name="TypeCount">Сколько типов найдено.</param>
/// <param name="MethodCount">Сколько методов найдено.</param>
/// <param name="FailurePointCount">Сколько потенциальных проблемных мест.</param>
/// <param name="Projects">Имена проектов.</param>
public sealed record CodeStructureSummary(
    int ProjectCount,
    int TypeCount,
    int MethodCount,
    int FailurePointCount,
    IReadOnlyList<string> Projects);
