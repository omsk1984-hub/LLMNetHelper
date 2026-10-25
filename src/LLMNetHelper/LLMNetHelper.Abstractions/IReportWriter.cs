namespace LLMNetHelper.Abstractions;

/// <summary>
/// Контракт рендерера отчётов.
/// </summary>
public interface IReportWriter
{
    /// <summary>
    /// Записывает Markdown-отчёт.
    /// </summary>
    Task WriteMarkdownAsync(string path, string content, CancellationToken ct = default);

    /// <summary>
    /// Записывает JSON-отчёт. Принимает ГОТОВУЮ JSON-строку (например,
    /// результат <c>JsonReportRenderer.Render</c>) и пишет её как есть.
    /// Повторная сериализация запрещена — иначе в файл попадёт
    /// экранированная строка вместо объекта (дефект D2, план 18).
    /// </summary>
    Task WriteJsonAsync(string path, string json, CancellationToken ct = default);

    /// <summary>
    /// Записывает произвольный текстовый артефакт (snapshot-сводку, patch.diff
    /// и т.п.) как есть, без сериализации. Прежнее имя <c>WriteSnapshotAsync</c>
    /// вводило в заблуждение: метод использовался и для записи diff (дефект D7,
    /// план 18).
    /// </summary>
    Task WriteTextAsync(string path, string content, CancellationToken ct = default);
}
