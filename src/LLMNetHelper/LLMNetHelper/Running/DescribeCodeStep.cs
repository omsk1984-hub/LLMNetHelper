using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LLMNetHelper.Roslyn;

namespace LLMNetHelper.Running;

/// <summary>
/// Шаг: краткая сводка структуры кода (Roslyn).
/// </summary>
public sealed class DescribeCodeStep : IRunStep
{
    public string Name => "DescribeCode";

    private readonly StructureAnalyzer _structureAnalyzer;
    private readonly IOptions<RoslynOptions> _roslynOptions;
    private readonly ILogger<DescribeCodeStep> _logger;

    public DescribeCodeStep(
        StructureAnalyzer structureAnalyzer,
        IOptions<RoslynOptions> roslynOptions,
        ILogger<DescribeCodeStep> logger)
    {
        _structureAnalyzer = structureAnalyzer;
        _roslynOptions = roslynOptions;
        _logger = logger;
    }

    public async Task ExecuteAsync(RunContext context, CancellationToken ct)
    {
        if (_roslynOptions.Value.Enabled is false)
        {
            return;
        }

        try
        {
            var summary = await _structureAnalyzer.SummarizeAsync(ct).ConfigureAwait(false);

            if (summary is null)
            {
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Проектов: {summary.ProjectCount}");
            sb.AppendLine($"Типов: {summary.TypeCount}");
            sb.AppendLine($"Методов: {summary.MethodCount}");
            sb.AppendLine($"Проблемных мест (catch/throw): {summary.FailurePointCount}");
            sb.AppendLine($"Проекты: {string.Join(", ", summary.Projects)}");

            context.CodeStructureSummary = sb.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Анализ структуры кода недоступен");
        }
    }
}
