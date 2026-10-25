using Microsoft.CodeAnalysis;

namespace LLMNetHelper.Roslyn;

/// <summary>
/// Контракт применения правок через AST (план 22, этап 2.2). Извлечён из
/// <see cref="AstPatcher"/>, чтобы потребители (транзакция правок) зависели
/// от абстракции, а не от конкретного класса.
/// <para>
/// Размещён в проекте Roslyn (не в Abstractions): сигнатура использует
/// Roslyn-типы (<see cref="Solution"/>, <see cref="PatchResult"/>), и их утечка
/// в слой абстракций нарушила бы архитектурные границы.
/// </para>
/// </summary>
public interface IAstPatcher
{
    /// <summary>
    /// Применяет правку к решению.
    /// </summary>
    /// <param name="solution">Исходное решение.</param>
    /// <param name="filePath">Путь к файлу (абсолютный или относительный).</param>
    /// <param name="symbolName">Имя метода, тело которого меняем.</param>
    /// <param name="changeKind">Вид правки.</param>
    /// <param name="newCode">Новый код тела метода.</param>
    Task<PatchResult> ApplyAsync(
        Solution solution,
        string filePath,
        string symbolName,
        LLMNetHelper.Abstractions.ChangeKind changeKind,
        string newCode,
        CancellationToken ct = default);
}

/// <summary>
/// Контракт проверки компиляции после правки (план 22, этап 2.2). Извлечён из
/// <see cref="CompilationValidator"/>.
/// </summary>
public interface ICompilationValidator
{
    /// <summary>
    /// Проверяет, что решение компилируется без ошибок.
    /// </summary>
    /// <param name="solution">Решение после правки.</param>
    /// <param name="projectName">Имя проекта (null — проверять все).</param>
    Task<ValidationResult> ValidateAsync(
        Solution solution,
        string? projectName = null,
        CancellationToken ct = default);
}
