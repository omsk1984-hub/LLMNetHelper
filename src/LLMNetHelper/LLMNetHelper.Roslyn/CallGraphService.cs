using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LLMNetHelper.Roslyn;

/// <summary>
/// Граф вызовов на уровне синтаксиса.
/// Не использует <c>SymbolFinder</c>: он требует полной семантической модели
/// и падает на частично загруженных решениях, а агенту нужен обзор,
/// а не гарантированно точный CALL-граф.
/// </summary>
public sealed class CallGraphService
{
    // План 22, этап 2.4: зависимость от абстракции ISolutionLoader.
    private readonly ISolutionLoader _loader;
    private readonly RoslynOptions _options;

    public CallGraphService(ISolutionLoader loader, RoslynOptions options)
    {
        _loader = loader;
        _options = options;
    }

    /// <summary>
    /// Кто вызывает метод с указанным именем (решение из глобального пути).
    /// </summary>
    public Task<IReadOnlyList<CodeSymbol>> FindCallersAsync(
        string methodName,
        CancellationToken ct = default)
    {
        return FindCallersAsync(methodName, solutionPath: null, ct);
    }

    /// <summary>
    /// Callers в решении по явному пути (режим чата <c>/repo</c>).
    /// <paramref name="solutionPath"/> = null — глобальный путь из опций.
    /// </summary>
    public async Task<IReadOnlyList<CodeSymbol>> FindCallersAsync(
        string methodName,
        string? solutionPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(methodName))
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

        return await FindCallersInSolutionAsync(methodName, solution, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<CodeSymbol>> FindCallersInSolutionAsync(
        string methodName,
        Solution solution,
        CancellationToken ct)
    {
        var results = new List<CodeSymbol>();

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                ct.ThrowIfCancellationRequested();

                if (document.FilePath is null || results.Count >= _options.MaxSearchResults)
                {
                    continue;
                }

                var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                if (root is null)
                {
                    continue;
                }

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (!InvokesMethod(invocation, methodName))
                    {
                        continue;
                    }

                    var containing = FindContainingMethod(invocation);
                    if (containing is null)
                    {
                        continue;
                    }

                    var line = containing.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    results.Add(new CodeSymbol(
                        Kind: "method",
                        FullName: containing.Identifier.Text,
                        FileName: document.FilePath,
                        Line: line));
                }
            }
        }

        return Deduplicate(results);
    }

    /// <summary>
    /// Какие методы вызывает указанный метод (решение из глобального пути).
    /// </summary>
    public Task<IReadOnlyList<string>> FindCalleesAsync(
        string methodName,
        CancellationToken ct = default)
    {
        return FindCalleesAsync(methodName, solutionPath: null, ct);
    }

    /// <summary>
    /// Callees в решении по явному пути (режим чата <c>/repo</c>).
    /// <paramref name="solutionPath"/> = null — глобальный путь из опций.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindCalleesAsync(
        string methodName,
        string? solutionPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(methodName))
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

        return await FindCalleesInSolutionAsync(methodName, solution, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> FindCalleesInSolutionAsync(
        string methodName,
        Solution solution,
        CancellationToken ct)
    {
        var callees = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                ct.ThrowIfCancellationRequested();

                if (document.FilePath is null)
                {
                    continue;
                }

                var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                if (root is null)
                {
                    continue;
                }

                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (!string.Equals(method.Identifier.Text, methodName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        var name = ExtractInvokedName(invocation);
                        if (name is not null)
                        {
                            callees.Add(name);
                        }
                    }
                }
            }
        }

        return callees.ToArray();
    }

    private static bool InvokesMethod(InvocationExpressionSyntax invocation, string methodName)
    {
        return invocation.Expression switch
        {
            IdentifierNameSyntax id => string.Equals(id.Identifier.Text, methodName, StringComparison.OrdinalIgnoreCase),
            MemberAccessExpressionSyntax ma => string.Equals(ma.Name.Identifier.Text, methodName, StringComparison.OrdinalIgnoreCase),
            GenericNameSyntax g => string.Equals(g.Identifier.Text, methodName, StringComparison.OrdinalIgnoreCase),
            MemberBindingExpressionSyntax mb => string.Equals(mb.Name.Identifier.Text, methodName, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static string? ExtractInvokedName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            GenericNameSyntax g => g.Identifier.Text,
            MemberBindingExpressionSyntax mb => mb.Name.Identifier.Text,
            _ => null
        };
    }

    private static MethodDeclarationSyntax? FindContainingMethod(SyntaxNode node)
    {
        return node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
    }

    private static IReadOnlyList<CodeSymbol> Deduplicate(List<CodeSymbol> symbols)
    {
        var seen = new HashSet<(string, string, int)>();
        var result = new List<CodeSymbol>(symbols.Count);

        foreach (var s in symbols)
        {
            if (seen.Add((s.FullName, s.FileName, s.Line)))
            {
                result.Add(s);
            }
        }

        result.Sort(static (a, b) =>
        {
            var byFile = string.CompareOrdinal(a.FileName, b.FileName);
            return byFile != 0 ? byFile : a.Line.CompareTo(b.Line);
        });

        return result;
    }
}
