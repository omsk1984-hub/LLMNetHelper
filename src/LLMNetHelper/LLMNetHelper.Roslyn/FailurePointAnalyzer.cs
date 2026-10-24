using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using LLMNetHelper.Abstractions;

namespace LLMNetHelper.Roslyn;

/// <summary>
/// Поиск потенциально проблемных мест в коде.
/// Реализует <see cref="IFailurePointAnalyzer"/>.
/// Работает на синтаксисе: не требует семантической модели и потому
/// переживает частично загруженные решения.
/// </summary>
public sealed class FailurePointAnalyzer
{
    public FailurePointAnalyzer()
    {
    }

    /// <summary>
    /// Находит проблемные места в документах решения.
    /// </summary>
    public async Task<IReadOnlyList<FailurePoint>> AnalyzeAsync(
        IEnumerable<SourceDocument> documents,
        CancellationToken ct = default)
    {
        var results = new List<FailurePoint>();

        foreach (var doc in documents)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(doc.FilePath))
            {
                continue;
            }

            var parsed = CSharpSyntaxTree.ParseText(doc.Content);
            var root = await parsed.GetRootAsync(ct).ConfigureAwait(false);
            if (root is null)
            {
                continue;
            }

            AnalyzeRoot(root, doc.FilePath, results);
        }

        results.Sort(static (a, b) =>
        {
            var byFile = string.CompareOrdinal(a.FileName, b.FileName);
            return byFile != 0 ? byFile : a.Line.CompareTo(b.Line);
        });

        return results;
    }

    /// <summary>
    /// Находит проблемные места с фильтрацией по пути файла.
    /// Заглушка для основного прогона (CodeSearchTool): решение недоступно
    /// без <see cref="SolutionLoader"/> — вызывающий должен использовать
    /// перегрузку с <see cref="Solution"/> или <see cref="IEnumerable{SourceDocument}"/>.
    /// </summary>
    public Task<IReadOnlyList<FailurePoint>> AnalyzeAsync(
        string? fileFilter = null,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<FailurePoint>>([]);
    }

    /// <summary>
    /// Находит проблемные места в загруженном решении (режим чата <c>/repo</c>).
    /// </summary>
    public async Task<IReadOnlyList<FailurePoint>> AnalyzeAsync(
        Solution solution,
        string? fileFilter = null,
        CancellationToken ct = default)
    {
        var results = new List<FailurePoint>();

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                ct.ThrowIfCancellationRequested();

                if (document.FilePath is null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(fileFilter) &&
                    !document.FilePath.Contains(fileFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                if (root is null)
                {
                    continue;
                }

                AnalyzeRoot(root, document.FilePath, results);
            }
        }

        results.Sort(static (a, b) =>
        {
            var byFile = string.CompareOrdinal(a.FileName, b.FileName);
            return byFile != 0 ? byFile : a.Line.CompareTo(b.Line);
        });

        return results;
    }

    /// <summary>
    /// Анализ одного синтаксического дерева.
    /// </summary>
    private static void AnalyzeRoot(SyntaxNode root, string filePath, List<FailurePoint> results)
    {
        // 1. Пустые catch-блоки и catch без логирования.
        foreach (var catchClause in root.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            var block = catchClause.Block;
            var line = catchClause.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var member = FindContainingMember(catchClause);

            var statementCount = block.Statements.Count;

            if (statementCount == 0)
            {
                results.Add(new FailurePoint(
                    filePath,
                    line,
                    "empty-catch",
                    member,
                    "catch { } — исключение проглатывается молча"));
                continue;
            }

            var hasLogging = block.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(static inv => inv.Expression.ToString().Contains("Log", StringComparison.OrdinalIgnoreCase));

            if (!hasLogging)
            {
                // Тип один — "catch-without-log" — как объявлено в контракте
                // IFailurePointAnalyzer. Различие «широкий catch (Exception)/голый
                // catch» против «узкий catch конкретного типа» сохраняется в
                // описании, а не в отдельном типе: иначе появлялся бы четвёртый
                // тип ("catch-exception"), отсутствующий в контракте.
                var isBroad = catchClause.Declaration is null ||
                              catchClause.Declaration.Type.ToString() == "Exception";

                var description = isBroad
                    ? $"catch ({catchClause.Declaration?.Type.ToString() ?? ""}) без логирования"
                    : "catch конкретного исключения без логирования";

                results.Add(new FailurePoint(
                    filePath,
                    line,
                    "catch-without-log",
                    member,
                    description));
            }
        }

        // 2. throw null / default / null! (null-forgiving).
        foreach (var throwStatement in root.DescendantNodes().OfType<ThrowStatementSyntax>())
        {
            var expr = throwStatement.Expression;
            if (expr is null)
            {
                continue;
            }

            // suppress-null: "null!" разбирается как SuppressNullableWarningExpression,
            // поэтому простое сравнение строкой "null" его не ловит.
            var isNullLiteral = expr is LiteralExpressionSyntax literal &&
                                literal.IsKind(SyntaxKind.NullLiteralExpression);
            var isDefaultLiteral = expr is LiteralExpressionSyntax def &&
                                   def.IsKind(SyntaxKind.DefaultLiteralExpression);
            var isNullForgiving = expr is PostfixUnaryExpressionSyntax postfix &&
                                  postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression) &&
                                  postfix.Operand is LiteralExpressionSyntax op &&
                                  op.IsKind(SyntaxKind.NullLiteralExpression);

            if (isNullLiteral || isDefaultLiteral || isNullForgiving)
            {
                var line = throwStatement.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                results.Add(new FailurePoint(
                    filePath,
                    line,
                    "throw-null",
                    FindContainingMember(throwStatement),
                    $"throw {expr} — потеря исходной причины"));
            }
        }
    }

    /// <summary>
    /// Имя члена, в котором находится узел.
    /// </summary>
    private static string FindContainingMember(SyntaxNode node)
    {
        foreach (var ancestor in node.AncestorsAndSelf())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax m:
                    return m.Identifier.Text;
                case PropertyDeclarationSyntax p:
                    return p.Identifier.Text;
                case ConstructorDeclarationSyntax c:
                    return c.Identifier.Text;
                case LocalFunctionStatementSyntax lf:
                    return lf.Identifier.Text;
            }
        }

        return "<top-level>";
    }
}
