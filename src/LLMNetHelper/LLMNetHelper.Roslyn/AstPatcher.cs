using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LLMNetHelper.Roslyn;

/// <summary>
/// Результат применения правки.
/// </summary>
/// <param name="Succeeded">Удалось ли применить.</param>
/// <param name="Reason">Причина отказа (если не удалось).</param>
/// <param name="UpdatedSolution">Новое решение (если удалось).</param>
public sealed record PatchResult(bool Succeeded, string? Reason, Solution? UpdatedSolution)
{
    public static PatchResult Fail(string reason) => new(false, reason, null);

    public static PatchResult Ok(Solution solution) => new(true, null, solution);
}

/// <summary>
/// Безопасное применение правок через AST.
/// <para>
/// Поддерживаются только две операции, обе — через Roslyn, без текстовых замен:
/// <list type="number">
///   <item><c>ReplaceMethodBody</c> — замена тела метода с сохранением trivia (отступов, комментариев);</item>
///   <item><c>InsertNullCheck</c> — вставка проверки на null в начало тела метода.</item>
/// </list>
/// </para>
/// <para>
/// Правка сигнатур и добавление/удаление членов намеренно не поддерживаются:
/// они ломают вызовы по всему решению.
/// </para>
/// </summary>
public sealed class AstPatcher
{
    /// <summary>
    /// Применяет правку к решению.
    /// </summary>
    /// <param name="solution">Исходное решение.</param>
    /// <param name="filePath">Путь к файлу (абсолютный или относительный).</param>
    /// <param name="symbolName">Имя метода, тело которого меняем.</param>
    /// <param name="changeKind">"ReplaceMethodBody" | "InsertNullCheck".</param>
    /// <param name="newCode">Новый код тела метода.</param>
    public async Task<PatchResult> ApplyAsync(
        Solution solution,
        string filePath,
        string symbolName,
        string changeKind,
        string newCode,
        CancellationToken ct = default)
    {
        if (solution is null)
        {
            return PatchResult.Fail("решение не загружено");
        }

        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(symbolName))
        {
            return PatchResult.Fail("не указан файл или символ");
        }

        if (string.IsNullOrWhiteSpace(newCode))
        {
            return PatchResult.Fail("пустой новый код");
        }

        var document = FindDocument(solution, filePath);
        if (document is null)
        {
            return PatchResult.Fail($"файл не найден в решении: {filePath}");
        }

        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (root is null)
        {
            return PatchResult.Fail("не удалось получить синтаксическое дерево");
        }

        var method = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => string.Equals(m.Identifier.Text, symbolName, StringComparison.Ordinal));

        if (method is null)
        {
            return PatchResult.Fail($"метод не найден: {symbolName}");
        }

        if (method.Body is null)
        {
            return PatchResult.Fail($"метод без тела (expression-bodied): {symbolName}");
        }

        try
        {
            SyntaxNode newRoot = changeKind switch
            {
                "ReplaceMethodBody" => ReplaceBody(root, method, newCode),
                "InsertNullCheck" => InsertNullCheck(root, method, newCode),
                _ => throw new NotSupportedException($"неподдерживаемый тип правки: {changeKind}")
            };

            var updated = solution.WithDocumentSyntaxRoot(document.Id, newRoot);
            return PatchResult.Ok(updated);
        }
        catch (NotSupportedException ex)
        {
            return PatchResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            return PatchResult.Fail($"сбой разбора нового кода: {ex.Message}");
        }
    }

    /// <summary>
    /// Заменяет тело метода, сохраняя отступы и комментарии исходного тела.
    /// </summary>
    private static SyntaxNode ReplaceBody(SyntaxNode root, MethodDeclarationSyntax method, string newCode)
    {
        var original = method.Body!;

        // Оборачиваем входной код в фигурные скобки: ParseStatement("return x;")
        // даёт StatementSyntax, а нам нужен BlockSyntax с trivia.
        var parsed = SyntaxFactory.ParseStatement("{\n" + newCode + "\n}");

        if (parsed is not BlockSyntax newBody)
        {
            throw new NotSupportedException("новый код не является блоком операторов");
        }

        // Забираем trivia (отступы/комментарии) у исходного блока.
        newBody = newBody.WithTriviaFrom(original);

        // Проверяем, что новый блок синтаксически валиден: нет error-узлов.
        if (newBody.ContainsDiagnostics)
        {
            var diagnostic = newBody.GetDiagnostics().FirstOrDefault();
            throw new NotSupportedException(
                $"новый код содержит синтаксическую ошибку: {diagnostic?.GetMessage()}");
        }

        return root.ReplaceNode(original, newBody);
    }

    /// <summary>
    /// Вставляет проверку в начало тела метода.
    /// </summary>
    private static SyntaxNode InsertNullCheck(SyntaxNode root, MethodDeclarationSyntax method, string newCode)
    {
        var original = method.Body!;
        var parsed = SyntaxFactory.ParseStatement(newCode + (newCode.TrimEnd().EndsWith(';') ? "" : ";"));

        if (parsed is null || parsed.ContainsDiagnostics)
        {
            var diagnostic = parsed?.GetDiagnostics().FirstOrDefault();
            throw new NotSupportedException(
                $"проверка содержит синтаксическую ошибку: {diagnostic?.GetMessage()}");
        }

        var newStatements = original.Statements.Insert(0, parsed);
        var newBody = original.WithStatements(newStatements);

        return root.ReplaceNode(original, newBody);
    }

    /// <summary>
    /// Ищет документ по пути: сначала точное совпадение, затем по суффиксу.
    /// </summary>
    private static Document? FindDocument(Solution solution, string filePath)
    {
        var normalized = filePath.Replace('\\', '/');

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath is null)
                {
                    continue;
                }

                var docPath = document.FilePath.Replace('\\', '/');

                if (string.Equals(docPath, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return document;
                }
            }
        }

        // Фолбэк: путь пришёл относительный — сравниваем по суффиксу.
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath is null)
                {
                    continue;
                }

                var docPath = document.FilePath.Replace('\\', '/');
                if (docPath.EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return document;
                }
            }
        }

        return null;
    }
}

/// <summary>
/// Проверка компиляции после правки.
/// </summary>
public sealed class CompilationValidator
{
    /// <summary>
    /// Проверяет, что проект компилируется без ошибок.
    /// </summary>
    /// <param name="solution">Решение после правки.</param>
    /// <param name="projectName">Имя проекта (null — проверять все).</param>
    public async Task<ValidationResult> ValidateAsync(
        Solution solution,
        string? projectName = null,
        CancellationToken ct = default)
    {
        var errors = new List<string>();

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();

            if (projectName is not null &&
                !string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null)
            {
                errors.Add($"{project.Name}: не удалось получить компиляцию");
                continue;
            }

            var projectNameForMessage = project.Name;
            var diagnostics = compilation.GetDiagnostics(ct)
                .Where(static d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"{projectNameForMessage}: {d.Id} {d.GetMessage()} ({d.Location.GetLineSpan().StartLinePosition.Line + 1})")
                .Take(20);

            errors.AddRange(diagnostics);
        }

        return new ValidationResult(errors.Count == 0, errors);
    }
}

/// <summary>
/// Результат проверки компиляции.
/// </summary>
/// <param name="Succeeded">Нет ли ошибок компиляции.</param>
/// <param name="Errors">Список ошибок (не более 20).</param>
public sealed record ValidationResult(bool Succeeded, IReadOnlyList<string> Errors);
