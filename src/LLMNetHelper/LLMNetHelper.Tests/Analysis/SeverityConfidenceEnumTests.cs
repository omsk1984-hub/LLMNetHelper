using System.Text.Json;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Analysis;
using Xunit;

namespace LLMNetHelper.Tests.Analysis;

/// <summary>
/// Тесты типобезопасности severity/confidence (план 22, этап 1.4):
/// строковые магические значения заменены перечислениями
/// <see cref="FindingSeverity"/> и <see cref="Confidence"/>;
/// нераспознанное значение модели сохраняется как <c>Other</c>
/// с исходной строкой (решение пользователя: не терять сигнал).
/// </summary>
public class SeverityConfidenceEnumTests
{
    private static JsonElement Elem(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>
    /// Распознанные severity маппятся в enum.
    /// </summary>
    [Theory]
    [InlineData("critical", FindingSeverity.Critical)]
    [InlineData("warning", FindingSeverity.Warning)]
    [InlineData("info", FindingSeverity.Info)]
    [InlineData("CRITICAL", FindingSeverity.Critical)]
    public void ParseFinding_KnownSeverity_MapsToEnum(string wire, FindingSeverity expected)
    {
        var finding = ResponseParser.ParseFinding(Elem($$"""{"severity":"{{wire}}"}"""));

        Assert.Equal(expected, finding.Severity);
        Assert.Null(finding.RawSeverity);
    }

    /// <summary>
    /// Нераспознанная severity (например "fatal") сохраняет исходную строку
    /// и не превращается молча в Info — вариант (б), согласованный с пользователем.
    /// </summary>
    [Fact]
    public void ParseFinding_UnknownSeverity_IsOtherWithRaw()
    {
        var finding = ResponseParser.ParseFinding(Elem("""{"severity":"fatal"}"""));

        Assert.Equal(FindingSeverity.Other, finding.Severity);
        Assert.Equal("fatal", finding.RawSeverity);
    }

    /// <summary>
    /// Распознанные confidence маппятся в enum.
    /// </summary>
    [Theory]
    [InlineData("high", Confidence.High)]
    [InlineData("medium", Confidence.Medium)]
    [InlineData("low", Confidence.Low)]
    [InlineData("High", Confidence.High)]
    public void ParseFinding_KnownConfidence_MapsToEnum(string wire, Confidence expected)
    {
        var finding = ResponseParser.ParseFinding(Elem($$"""{"confidence":"{{wire}}"}"""));

        Assert.Equal(expected, finding.Confidence);
        Assert.Null(finding.RawConfidence);
    }

    /// <summary>
    /// Нераспознанный confidence сохраняется как Other с исходной строкой.
    /// </summary>
    [Fact]
    public void ParseFinding_UnknownConfidence_IsOtherWithRaw()
    {
        var finding = ResponseParser.ParseFinding(Elem("""{"confidence":"very-sure"}"""));

        Assert.Equal(Confidence.Other, finding.Confidence);
        Assert.Equal("very-sure", finding.RawConfidence);
    }

    /// <summary>
    /// Wire-формат находок не меняется: канонические значения — строчные
    /// "critical"/"warning"/"info", Other — исходная строка модели.
    /// </summary>
    [Fact]
    public void SeverityWire_KnownAndUnknown_PreserveOriginalFormat()
    {
        Assert.Equal("critical", FindingFormat.ToWire(FindingSeverity.Critical, null));
        Assert.Equal("warning", FindingFormat.ToWire(FindingSeverity.Warning, null));
        Assert.Equal("info", FindingFormat.ToWire(FindingSeverity.Info, null));
        Assert.Equal("fatal", FindingFormat.ToWire(FindingSeverity.Other, "fatal"));
        // Other без сохранённой строки (ружная конструкция) — не падает.
        Assert.Equal("other", FindingFormat.ToWire(FindingSeverity.Other, null));
    }

    /// <summary>
    /// Wire-формат confidence не меняется.
    /// </summary>
    [Fact]
    public void ConfidenceWire_KnownAndUnknown_PreserveOriginalFormat()
    {
        Assert.Equal("high", FindingFormat.ToWire(Confidence.High, null));
        Assert.Equal("medium", FindingFormat.ToWire(Confidence.Medium, null));
        Assert.Equal("low", FindingFormat.ToWire(Confidence.Low, null));
        Assert.Equal("very-sure", FindingFormat.ToWire(Confidence.Other, "very-sure"));
        Assert.Equal("other", FindingFormat.ToWire(Confidence.Other, null));
    }

    /// <summary>
    /// CorrectVerdict работает по enum: critical-находка повышает вердикт,
    /// warning-находка понижает CRITICAL.
    /// </summary>
    [Fact]
    public void CorrectVerdict_UsesEnumSeverity()
    {
        var critical = new Finding { Severity = FindingSeverity.Critical };
        var warning = new Finding { Severity = FindingSeverity.Warning };

        Assert.Equal(Verdict.Critical, ResponseParser.CorrectVerdict(Verdict.Warning, [critical]));
        Assert.Equal(Verdict.Warning, ResponseParser.CorrectVerdict(Verdict.Critical, [warning]));
    }

    /// <summary>
    /// Other-находка не влияет на вердикт (не считается ни critical, ни warning).
    /// </summary>
    [Fact]
    public void CorrectVerdict_OtherSeverity_DoesNotChangeVerdict()
    {
        var other = new Finding { Severity = FindingSeverity.Other, RawSeverity = "fatal" };

        Assert.Equal(Verdict.Ok, ResponseParser.CorrectVerdict(Verdict.Ok, [other]));
        Assert.Equal(Verdict.Warning, ResponseParser.CorrectVerdict(Verdict.Warning, [other]));
    }

    /// <summary>
    /// Hypothesis.Confidence — enum; пустая гипотеза использует Low.
    /// </summary>
    [Fact]
    public void Hypothesis_ConfidenceIsEnum_EmptyUsesLow()
    {
        Assert.Equal(Confidence.Low, Hypothesis.Empty.Confidence);

        var h = new Hypothesis("причина", Confidence.High, "доказательство", [], RawConfidence: null);
        Assert.Equal(Confidence.High, h.Confidence);
    }

    /// <summary>
    /// Нераспознанный confidence гипотезы сохраняется как Other с исходной строкой.
    /// </summary>
    [Fact]
    public void Hypothesis_UnknownConfidence_IsOtherWithRaw()
    {
        var h = new Hypothesis("причина", Confidence.Other, "доказательство", [], RawConfidence: "certain");

        Assert.Equal(Confidence.Other, h.Confidence);
        Assert.Equal("certain", h.RawConfidence);
    }
}
