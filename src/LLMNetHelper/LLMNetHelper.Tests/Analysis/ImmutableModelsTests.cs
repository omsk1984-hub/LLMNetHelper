using LLMNetHelper.Abstractions;
using Xunit;

namespace LLMNetHelper.Tests.Analysis;

/// <summary>
/// Тесты неизменяемости моделей контрактного слоя (план 22, этап 1.7):
/// модели Abstractions — record-типы (value equality) с <c>init</c>-свойствами;
/// мутация после создания невозможна на уровне компиляции.
/// </summary>
public class ImmutableModelsTests
{
    private static readonly DateTime T = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Finding — record: два одинаковых экземпляра равны по значению.
    /// Сейчас — sealed class (reference equality) → тест падает (красный).
    /// </summary>
    [Fact]
    public void Finding_HasValueEquality()
    {
        var a = new Finding { Id = "F1", Severity = FindingSeverity.Warning, Title = "t" };
        var b = new Finding { Id = "F1", Severity = FindingSeverity.Warning, Title = "t" };

        Assert.Equal(a, b);
    }

    /// <summary>
    /// LogFact — record (value equality).
    /// </summary>
    [Fact]
    public void LogFact_HasValueEquality()
    {
        var a = new LogFact { Id = 1, RunId = 2, BucketStart = T, Job = "artas_writer", Count = 5 };
        var b = new LogFact { Id = 1, RunId = 2, BucketStart = T, Job = "artas_writer", Count = 5 };

        Assert.Equal(a, b);
    }

    /// <summary>
    /// ErrorSample — record (value equality).
    /// </summary>
    [Fact]
    public void ErrorSample_HasValueEquality()
    {
        var a = new ErrorSample { Signature = "sig", Lvl = "fail", FirstSeen = T, LastSeen = T, Count = 3 };
        var b = new ErrorSample { Signature = "sig", Lvl = "fail", FirstSeen = T, LastSeen = T, Count = 3 };

        Assert.Equal(a, b);
    }

    /// <summary>
    /// MetricFact — record (value equality).
    /// </summary>
    [Fact]
    public void MetricFact_HasValueEquality()
    {
        var a = new MetricFact { Name = "m", Value = 1.5, WindowFrom = T, WindowTo = T };
        var b = new MetricFact { Name = "m", Value = 1.5, WindowFrom = T, WindowTo = T };

        Assert.Equal(a, b);
    }

    /// <summary>
    /// RunInfo — record (value equality).
    /// </summary>
    [Fact]
    public void RunInfo_HasValueEquality()
    {
        var a = new RunInfo { Id = 1, StartedAt = T, WindowFrom = T, WindowTo = T, Status = RunStatus.Ok };
        var b = new RunInfo { Id = 1, StartedAt = T, WindowFrom = T, WindowTo = T, Status = RunStatus.Ok };

        Assert.Equal(a, b);
    }

    /// <summary>
    /// LlmRunInfo — record (value equality).
    /// </summary>
    [Fact]
    public void LlmRunInfo_HasValueEquality()
    {
        var a = new LlmRunInfo { RunId = 1, Provider = "custom", Model = "m", PromptTokens = 10 };
        var b = new LlmRunInfo { RunId = 1, Provider = "custom", Model = "m", PromptTokens = 10 };

        Assert.Equal(a, b);
    }
}
