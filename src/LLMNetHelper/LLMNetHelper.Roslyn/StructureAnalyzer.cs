using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using LLMNetHelper.Abstractions;

namespace LLMNetHelper.Roslyn;

/// <summary>
/// Анализ структуры решения: проекты, типы, методы, регистрации DI.
/// </summary>
public sealed class StructureAnalyzer
{
    // План 22, этап 2.4: зависимость от абстракции ISolutionLoader.
    private readonly ISolutionLoader _loader;

    public StructureAnalyzer(ISolutionLoader loader)
    {
        _loader = loader;
    }

    /// <summary>
    /// Сводка по решению.
    /// </summary>
    public async Task<CodeStructureSummary?> SummarizeAsync(CancellationToken ct = default)
    {
        var solution = await _loader.LoadAsync(ct).ConfigureAwait(false);
        if (solution is null)
        {
            return null;
        }

        var projects = new List<string>();
        var typeCount = 0;
        var methodCount = 0;

        var documents = new List<SourceDocument>();

        foreach (var project in solution.Projects)
        {
            projects.Add(project.Name);

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

                typeCount += root.DescendantNodes().OfType<TypeDeclarationSyntax>().Count();
                methodCount += root.DescendantNodes().OfType<MethodDeclarationSyntax>().Count();

                var text = await document.GetTextAsync(ct).ConfigureAwait(false);
                documents.Add(new SourceDocument(document.FilePath, text.ToString()));
            }
        }

        var failures = await new FailurePointAnalyzer().AnalyzeAsync(documents, ct)
            .ConfigureAwait(false);

        projects.Sort(StringComparer.Ordinal);

        return new CodeStructureSummary(
            ProjectCount: projects.Count,
            TypeCount: typeCount,
            MethodCount: methodCount,
            FailurePointCount: failures.Count,
            Projects: projects);
    }

    /// <summary>
    /// Регистрации сервисов в DI-контейнере.
    /// Ищет вызовы <c>AddSingleton</c>/<c>AddScoped</c>/<c>AddTransient</c>.
    /// </summary>
    public async Task<IReadOnlyList<ServiceRegistration>> FindRegistrationsAsync(
        CancellationToken ct = default)
    {
        var solution = await _loader.LoadAsync(ct).ConfigureAwait(false);
        if (solution is null)
        {
            return [];
        }

        var results = new List<ServiceRegistration>();

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

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var lifetime = ExtractLifetime(invocation);
                    if (lifetime is null)
                    {
                        continue;
                    }

                    var (serviceType, implementationType) = ExtractTypes(invocation);
                    if (serviceType is null)
                    {
                        continue;
                    }

                    var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    results.Add(new ServiceRegistration(
                        serviceType,
                        implementationType ?? serviceType,
                        lifetime,
                        document.FilePath,
                        line));
                }
            }
        }

        results.Sort(static (a, b) =>
        {
            var byService = string.CompareOrdinal(a.ServiceType, b.ServiceType);
            return byService != 0 ? byService : a.Line.CompareTo(b.Line);
        });

        return results;
    }

    private static string? ExtractLifetime(InvocationExpressionSyntax invocation)
    {
        var name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            GenericNameSyntax g => g.Identifier.Text,
            IdentifierNameSyntax id => id.Identifier.Text,
            _ => null
        };

        return name switch
        {
            "AddSingleton" or "AddSingletonService" => "singleton",
            "AddScoped" or "AddScopedService" => "scoped",
            "AddTransient" or "AddTransientService" => "transient",
            _ => null
        };
    }

    private static (string? ServiceType, string? ImplementationType) ExtractTypes(
        InvocationExpressionSyntax invocation)
    {
        var args = invocation.ArgumentList.Arguments;

        // Форма generic: AddSingleton<IFoo, Foo>()
        if (invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax generic })
        {
            var typeArgs = generic.TypeArgumentList.Arguments;
            if (typeArgs.Count == 2)
            {
                return (typeArgs[0].ToString(), typeArgs[1].ToString());
            }
            if (typeArgs.Count == 1)
            {
                return (typeArgs[0].ToString(), null);
            }
        }

        // Форма с typeof: AddSingleton(typeof(IFoo), typeof(Foo))
        if (args.Count >= 2)
        {
            var service = ExtractTypeOfArgument(args[0]);
            var impl = ExtractTypeOfArgument(args[1]);
            if (service is not null)
            {
                return (service, impl);
            }
        }
        else if (args.Count == 1)
        {
            return (ExtractTypeOfArgument(args[0]), null);
        }

        return (null, null);
    }

    private static string? ExtractTypeOfArgument(ArgumentSyntax arg)
    {
        return arg.Expression is TypeOfExpressionSyntax typeOf
            ? typeOf.Type.ToString()
            : null;
    }
}
