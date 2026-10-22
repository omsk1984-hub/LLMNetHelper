using ArtasTools.MonitorAgent.Abstractions;
using ArtasTools.MonitorAgent.Context;
using ArtasTools.MonitorAgent.Core.Snapshot;

namespace ArtasTools.MonitorAgent.Infrastructure.Context;

/// <summary>
/// Источник контекста «локальный каталог с файлами» (этап 1).
/// Обходит каталог, режет найденные файлы на чанки, ранжирует по BM25
/// и возвращает лучшие документы.
/// <para>
/// Особенности:
/// <list type="bullet">
///   <item>секреты вырезаются через <see cref="Redactor"/> до отдачи наружу;</item>
///   <item>недоступный каталог — это <c>IsAvailableAsync == false</c>, а не исключение;</item>
///   <item>порядок детерминирован (сортировка по пути и по ID чанка).</item>
/// </list>
/// </para>
/// </summary>
public sealed class LocalFolderContextSource : IContextSource
{
    private readonly LocalFolderOptions _options;
    private readonly ContextOptions _contextOptions;
    private readonly FileWalker _walker;
    private readonly KeywordRanker _ranker = new();

    public LocalFolderContextSource(LocalFolderOptions options, ContextOptions contextOptions)
    {
        _options = options;
        _contextOptions = contextOptions;
        _walker = new FileWalker(options);
    }

    /// <inheritdoc />
    public string Name => "local-folder";

    /// <summary>
    /// Каталог существует и содержит хотя бы один подходящий файл.
    /// </summary>
    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var root = _options.ResolveRoot();
        if (!Directory.Exists(root))
        {
            return Task.FromResult(false);
        }

        // Проверяем, что есть хотя бы один файл под Include — иначе источник
        // формально доступен, но бесполезен.
        var files = _walker.Walk(ct);
        return Task.FromResult(files.Count > 0);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContextDocument>> SearchAsync(
        ContextQuery query,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Text) || query.TopK <= 0)
        {
            return [];
        }

        var files = _walker.Walk(ct);
        if (files.Count == 0)
        {
            return [];
        }

        var maxChars = query.MaxChars > 0 ? query.MaxChars : _contextOptions.MaxChars;

        // 1. Читаем и нарезаем чанки.
        var chunks = new List<ChunkEntry>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var text = await TryReadAsync(file.FullPath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var title = Path.GetFileNameWithoutExtension(file.RelativePath);
            var parts = TextChunker.Split(text, title, _options.ChunkSize, _options.ChunkOverlap);

            for (var i = 0; i < parts.Count; i++)
            {
                var chunk = parts[i];
                chunks.Add(new ChunkEntry(
                    Id: $"{file.RelativePath}#{chunk.Ordinal}",
                    Path: file.RelativePath,
                    Title: chunk.Title,
                    Text: chunk.Text));
            }
        }

        if (chunks.Count == 0)
        {
            return [];
        }

        // 2. Ранжируем.
        var ranked = _ranker.Rank(
            query.Text,
            chunks,
            static c => c.Text + "\n" + c.Title + "\n" + c.Path,
            static c => c.Id);

        if (ranked.Count == 0)
        {
            return [];
        }

        // 3. Отбираем top-K и обрезаем по MaxChars, вырезая секреты.
        var byId = chunks.ToDictionary(static c => c.Id, StringComparer.Ordinal);
        var result = new List<ContextDocument>(Math.Min(query.TopK, ranked.Count));

        foreach (var scored in ranked)
        {
            if (result.Count >= query.TopK)
            {
                break;
            }

            var chunk = byId[scored.Id];
            var content = chunk.Text.Length > maxChars
                ? chunk.Text[..maxChars] + "\n… (обрезано)"
                : chunk.Text;

            content = Redactor.Redact(content);

            result.Add(new ContextDocument(
                Source: Name,
                Id: chunk.Id,
                Title: $"{chunk.Path} — {chunk.Title}",
                Content: content,
                Score: scored.Score));
        }

        return result;
    }

    /// <summary>
    /// Чтение файла с обработкой ошибок доступа. Недоступный файл — не ошибка прогона.
    /// </summary>
    private static async Task<string?> TryReadAsync(string path, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Внутренняя запись о чанке до ранжирования.
    /// </summary>
    private sealed record ChunkEntry(string Id, string Path, string Title, string Text);
}
