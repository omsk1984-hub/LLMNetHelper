using System.Text;
using System.Text.Json;
using ArtasTools.MonitorAgent.Abstractions;

namespace ArtasTools.MonitorAgent.Infrastructure.Reporting;

/// <summary>
/// Запись отчётов в файлы: Markdown, JSON, Snapshot.
/// </summary>
public sealed class FileReportWriter : IReportWriter
{
    private readonly string _reportsDir;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

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

    public async Task WriteJsonAsync(string path, object data, CancellationToken ct = default)
    {
        var fullPath = EnsurePath(path);
        var json = JsonSerializer.Serialize(data, JsonOpts);
        await File.WriteAllTextAsync(fullPath, json, Encoding.UTF8, ct);
    }

    public async Task WriteSnapshotAsync(string path, string content, CancellationToken ct = default)
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
