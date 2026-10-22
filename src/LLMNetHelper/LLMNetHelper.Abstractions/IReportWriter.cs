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
    /// Записывает JSON-отчёт.
    /// </summary>
    Task WriteJsonAsync(string path, object data, CancellationToken ct = default);

    /// <summary>
    /// Записывает snapshot (сводку для LLM).
    /// </summary>
    Task WriteSnapshotAsync(string path, string content, CancellationToken ct = default);
}
