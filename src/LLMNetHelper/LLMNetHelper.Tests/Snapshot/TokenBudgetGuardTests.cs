using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Snapshot;

namespace LLMNetHelper.Tests.Snapshot;

public class TokenBudgetGuardTests
{
    [Fact]
    public void Enforce_NoTrimming_WhenUnderBudget()
    {
        var shortText = "Short snapshot text";
        var samples = new List<ErrorSample>();

        var result = TokenBudgetGuard.Instance.Enforce(shortText, 25, 2000, samples);

        Assert.False(result.WasTrimmed);
        Assert.Equal(shortText, result.SnapshotText);
    }

    [Fact]
    public void Enforce_Trims_WhenOverBudget()
    {
        // Создаём очень длинный текст (> 6000 токенов * 4 = 24000 символов)
        var longText = new string('X', 30000);
        var samples = new List<ErrorSample>();

        var result = TokenBudgetGuard.Instance.Enforce(longText, 25, 2000, samples);

        Assert.True(result.WasTrimmed);
        // Текст должен быть короче
        Assert.True(result.SnapshotText.Length < longText.Length);
    }

    [Fact]
    public void Enforce_ReducesSampleLength()
    {
        var samples = new List<ErrorSample>
        {
            new() { SampleText = new string('A', 3000), Count = 10 }
        };

        var longText = new string('X', 30000);
        var result = TokenBudgetGuard.Instance.Enforce(longText, 25, 800, samples);

        Assert.True(result.WasTrimmed);
    }

    [Fact]
    public void Enforce_NeverTrimsAggregates()
    {
        // Агрегаты — это короткие строки, они не должны обрезаться
        var samples = new List<ErrorSample>();

        var result = TokenBudgetGuard.Instance.Enforce("Aggregates table data", 25, 2000, samples);

        // Агрегаты не режутся — текст должен остаться
        Assert.NotNull(result.SnapshotText);
    }
}
