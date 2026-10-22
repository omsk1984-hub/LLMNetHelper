using System.Collections;

namespace LLMNetHelper.Abstractions;

/// <summary>
/// Результат анализа проблемных мест в коде.
/// </summary>
/// <param name="FileName">Путь к файлу.</param>
/// <param name="Line">Номер строки.</param>
/// <param name="Type">Тип проблемы: "empty-catch", "catch-without-log", "throw-null".</param>
/// <param name="Member">Имя метода/свойства/конструктора.</param>
/// <param name="Description">Описание проблемы.</param>
public sealed record FailurePoint(
    string FileName,
    int Line,
    string Type,
    string? Member,
    string Description);

/// <summary>
/// Информация о исходном документе для анализатора.
/// </summary>
/// <param name="FilePath">Путь к файлу.</param>
/// <param name="Content">Содержимое файла.</param>
public sealed record SourceDocument(string FilePath, string Content);
