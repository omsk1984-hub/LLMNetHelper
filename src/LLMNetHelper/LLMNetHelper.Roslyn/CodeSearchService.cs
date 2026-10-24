using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LLMNetHelper.Roslyn;

/// <summary>
/// Поиск типов и членов по подстроке имени.
/// Работает по синтаксису, без семантической модели — этого достаточно,
/// чтобы агент нашёл нужный файл, а затем прочитал член целиком.
/// </summary>
public sealed class CodeSearchService
{
    private readonly SolutionLoader _loader;
    private readonly RoslynOptions _options;

    public CodeSearchService(SolutionLoader loader, RoslynOptions options)
    {
        _loader = loader;
        _options = options;
    }

    /// <summary>
    /// Ищет типы и члены, чьё имя содержит подстроку (без учёта регистра).
    /// Решение — из <see cref="RoslynOptions.SolutionPath"/>.
    /// </summary>
    public Task<IReadOnlyList<CodeSymbol>> SearchAsync(
        string query,
        CancellationToken ct = default)
    {
        return SearchAsync(query, solutionPath: null, ct);
    }

    /// <summary>
    /// Поиск в решении по явному пути (режим чата <c>/repo</c>).
    /// <paramref name="solutionPath"/> = null — глобальный путь из опций.
    /// </summary>
    public async Task<IReadOnlyList<CodeSymbol>> SearchAsync(
        string query,
        string? solutionPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var solution = solutionPath is null
            ? await _loader.LoadAsync(ct).ConfigureAwait(false)
            : await _loader.LoadAsync(solutionPath, ct).ConfigureAwait(false);
        if (solution is null)
        {
            return [];
        }

        return await SearchInSolutionAsync(query, solution, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<CodeSymbol>> SearchInSolutionAsync(
        string query,
        Solution solution,
        CancellationToken ct)
    {
        var results = new List<CodeSymbol>();

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                ct.ThrowIfCancellationRequested();

                if (results.Count >= _options.MaxSearchResults)
                {
                    break;
                }

                if (document.FilePath is null)
                {
                    continue;
                }

                var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                if (root is null)
                {
                    continue;
                }

                CollectFromRoot(root, document.FilePath, query, results);
            }
        }

        // Детерминированный порядок: по файлу, затем по строке.
        results.Sort(static (a, b) =>
        {
            var byFile = string.CompareOrdinal(a.FileName, b.FileName);
            return byFile != 0 ? byFile : a.Line.CompareTo(b.Line);
        });

        return results.Count > _options.MaxSearchResults
            ? results.GetRange(0, _options.MaxSearchResults)
            : results;
    }

    private static void CollectFromRoot(
        SyntaxNode root,
        string filePath,
        string query,
        List<CodeSymbol> results)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case ClassDeclarationSyntax cls when Matches(cls.Identifier.Text, query):
                    results.Add(Make("class", cls.Identifier.Text, cls, filePath, cls.Parent));
                    break;

                case InterfaceDeclarationSyntax iface when Matches(iface.Identifier.Text, query):
                    results.Add(Make("interface", iface.Identifier.Text, iface, filePath, iface.Parent));
                    break;

                case RecordDeclarationSyntax rec when Matches(rec.Identifier.Text, query):
                    results.Add(Make("record", rec.Identifier.Text, rec, filePath, rec.Parent));
                    break;

                case MethodDeclarationSyntax method when Matches(method.Identifier.Text, query):
                    results.Add(Make("method", method.Identifier.Text, method, filePath,
                        method.Parent?.Parent));
                    break;

                case PropertyDeclarationSyntax prop when Matches(prop.Identifier.Text, query):
                    results.Add(Make("property", prop.Identifier.Text, prop, filePath,
                        prop.Parent?.Parent));
                    break;
            }
        }
    }

    /// <summary>
    /// Строит запись о символе с полным именем (Namespace.Type.Member).
    /// </summary>
    private static CodeSymbol Make(
        string kind,
        string name,
        SyntaxNode node,
        string filePath,
        SyntaxNode? typeOrNamespaceParent)
    {
        var container = typeOrNamespaceParent is not null
            ? BuildContainerName(typeOrNamespaceParent)
            : "";

        var fullName = string.IsNullOrEmpty(container) ? name : $"{container}.{name}";
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        return new CodeSymbol(kind, fullName, filePath, line);
    }

    /// <summary>
    /// Собирает цепочку Namespace.Type для узла.
    /// </summary>
    private static string BuildContainerName(SyntaxNode node)
    {
        var parts = new List<string>();

        foreach (var ancestor in node.AncestorsAndSelf())
        {
            switch (ancestor)
            {
                case TypeDeclarationSyntax t:
                    parts.Add(t.Identifier.Text);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    parts.Add(ns.Name.ToString());
                    break;
            }
        }

        parts.Reverse();
        return string.Join('.', parts);
    }

    private static bool Matches(string candidate, string query)
    {
        return candidate.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
