using LLMNetHelper.Abstractions;
using Xunit;

namespace LLMNetHelper.Tests.Analysis;

/// <summary>
/// Тесты типобезопасности вида правки (план 22, этап 1.3): строковые
/// «ReplaceMethodBody»/«InsertNullCheck» заменены перечислением <see cref="ChangeKind"/>;
/// нераспознанный вид — <see cref="ChangeKind.Unknown"/> (отказ применения,
/// прежнее поведение для произвольной строки).
/// </summary>
public class ChangeKindEnumTests
{
    /// <summary>
    /// Канонические строки из ответа модели маппятся в enum (точное совпадение).
    /// </summary>
    [Theory]
    [InlineData("ReplaceMethodBody", ChangeKind.ReplaceMethodBody)]
    [InlineData("InsertNullCheck", ChangeKind.InsertNullCheck)]
    public void Parse_KnownWire_MapsToEnum(string wire, ChangeKind expected)
    {
        Assert.Equal(expected, ChangeKindFormat.Parse(wire));
    }

    /// <summary>
    /// Нераспознанный вид — Unknown: транзакция обязана отказаться применять
    /// правку, а не применить её наугад.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("replaceMethodBody")]
    [InlineData("RenameSymbol")]
    public void Parse_UnknownWire_ReturnsUnknown(string? wire)
    {
        Assert.Equal(ChangeKind.Unknown, ChangeKindFormat.Parse(wire));
    }

    /// <summary>
    /// Wire-формат не меняется: канонические имена для сообщений коммита.
    /// </summary>
    [Theory]
    [InlineData(ChangeKind.ReplaceMethodBody, "ReplaceMethodBody")]
    [InlineData(ChangeKind.InsertNullCheck, "InsertNullCheck")]
    [InlineData(ChangeKind.Unknown, "Unknown")]
    public void ToWire_PreservesCanonicalNames(ChangeKind kind, string expected)
    {
        Assert.Equal(expected, ChangeKindFormat.ToWire(kind));
    }

    /// <summary>
    /// ProposedChange несёт enum-вид правки.
    /// </summary>
    [Fact]
    public void ProposedChange_ChangeKindIsEnum()
    {
        var change = new ProposedChange(
            "src/A.cs", "Run", ChangeKind.InsertNullCheck, "if (x is null) return;", "");

        Assert.Equal(ChangeKind.InsertNullCheck, change.ChangeKind);
    }
}
