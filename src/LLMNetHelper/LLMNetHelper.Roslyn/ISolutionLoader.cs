using Microsoft.CodeAnalysis;

namespace LLMNetHelper.Roslyn;

/// <summary>
/// Контракт загрузчика решения (план 22, этап 2.4). Извлечён из
/// <see cref="SolutionLoader"/>, чтобы потребители (<see cref="ChatSession"/>-инструменты,
/// транзакция правок, сервисы анализа) зависели от абстракции, а не от конкретного
/// класса — подмена в тестах без Roslyn/MSBuild.
/// <para>
/// Размещён в проекте Roslyn (не в Abstractions): сигнатура использует
/// Roslyn-тип <see cref="Solution"/>, его утечка в общий слой нарушила бы границы.
/// </para>
/// <para>
/// <see cref="IDisposable"/> в контракт не включён: жизненным циклом владеет
/// DI-контейнер; требовать disposal от любой реализации — нарушение LSP.
/// </para>
/// </summary>
public interface ISolutionLoader
{
    /// <summary>
    /// Загружает решение по пути из конфигурации. null — анализ недоступен
    /// или загрузка не удалась (без исключения).
    /// </summary>
    Task<Solution?> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Загружает решение (или проект) по явному пути. null — недоступно.
    /// </summary>
    Task<Solution?> LoadAsync(string solutionPath, CancellationToken ct = default);

    /// <summary>
    /// Сбрасывает кэш — вызывается после применения правок.
    /// </summary>
    void InvalidateCache();
}
