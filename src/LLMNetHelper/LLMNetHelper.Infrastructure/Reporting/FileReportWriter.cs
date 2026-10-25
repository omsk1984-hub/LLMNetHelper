using System.Text;
using LLMNetHelper.Abstractions;

namespace LLMNetHelper.Infrastructure.Reporting;

/// <summary>
/// Запись отчётов в файлы: Markdown, JSON, Snapshot.
/// </summary>
public sealed class FileReportWriter : IReportWriter
{
    private readonly string _reportsDir;

    public FileReportWriter(string reportsDir)
    {
        _reportsDir = reportsDir;
        Directory.CreateDirectory(reportsDir);
    }

    public async Task WriteMarkdownAsync(string path, string content, CancellationToken ct = default)
    {
        var fullPath = EnsurePath(path);
        await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8, ct);
    }

    public async Task WriteJsonAsync(string path, string json, CancellationToken ct = default)
    {
        // Пишем готовую JSON-строку как есть: повторная сериализация
        // превратила бы объект в экранированную строку (дефект D2, план 18).
        var fullPath = EnsurePath(path);
        await File.WriteAllTextAsync(fullPath, json, Encoding.UTF8, ct);
    }

    public async Task WriteTextAsync(string path, string content, CancellationToken ct = default)
    {
        var fullPath = EnsurePath(path);
        await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8, ct);
    }

    private string EnsurePath(string path)
    {
        // Если путь абсолютный — используем как есть, иначе относительный к reportsDir
        if (Path.IsPathRooted(path))
            return path;

        var fullPath = Path.Combine(_reportsDir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? _reportsDir);
        return fullPath;
    }
}
