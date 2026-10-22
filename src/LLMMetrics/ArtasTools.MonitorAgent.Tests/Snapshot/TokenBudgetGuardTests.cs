using ArtasTools.MonitorAgent.Abstractions;
using ArtasTools.MonitorAgent.Core.Snapshot;

namespace ArtasTools.MonitorAgent.Tests.Snapshot;

public class TokenBudgetGuardTests
{
    [Fact]
    public void EnforceBudget_NoTrimming_WhenUnderBudget()
    {
        var options = new AgentOptions { TopErrorsLimit = 25, MaxSampleChars = 2000 };
        var shortText = "Short snapshot text";
        var samples = new List<ErrorSample>();

        var result = TokenBudgetGuard.EnforceBudget(shortText, 25, 2000, samples, options);

        Assert.False(result.WasTrimmed);
        Assert.Equal(shortText, result.SnapshotText);
    }

    [Fact]
    public void EnforceBudget_Trims_WhenOverBudget()
    {
        // Создаём очень длинный текст (> 6000 токенов * 4 = 24000 символов)
        var longText = new string('X', 30000);
        var options = new AgentOptions { TopErrorsLimit = 25, MaxSampleChars = 2000 };
        var samples = new List<ErrorSample>();

        var result = TokenBudgetGuard.EnforceBudget(longText, 25, 2000, samples, options);

        Assert.True(result.WasTrimmed);
        // Текст должен быть короче
        Assert.True(result.SnapshotText.Length < longText.Length);
    }

    [Fact]
    public void EnforceBudget_ReducesSampleLength()
    {
        var options = new AgentOptions { TopErrorsLimit = 25, MaxSampleChars = 2000 };
        var samples = new List<ErrorSample>
        {
            new() { SampleText = new string('A', 3000), Count = 10 }
        };

        var longText = new string('X', 30000);
        var result = TokenBudgetGuard.EnforceBudget(longText, 25, 800, samples, options);

        Assert.True(result.WasTrimmed);
    }

    [Fact]
    public void EnforceBudget_NeverTrimsAggregates()
    {
        // Агрегаты — это короткие строки, они не должны обрезаться
        var options = new AgentOptions { TopErrorsLimit = 25, MaxSampleChars = 2000 };
        var samples = new List<ErrorSample>();

        var result = TokenBudgetGuard.EnforceBudget("Aggregates table data", 25, 2000, samples, options);

        // Агрегаты не режутся — текст должен остаться
        Assert.NotNull(result.SnapshotText);
    }
}
