using System.Text.Json;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Analysis;
using Xunit;

namespace LLMNetHelper.Tests.Analysis;

/// <summary>
/// Тесты типобезопасности вердикта (план 22, этап 1.1):
/// магические строки "OK"/"WARNING"/"CRITICAL"/"UNKNOWN" заменены перечислением
/// <see cref="Verdict"/>; wire-формат отчётов и БД не меняется (конвертер
/// <see cref="VerdictFormat"/>).
/// </summary>
public class VerdictEnumTests
{
    /// <summary>
    /// Распознанные строки из ответа модели маппятся в enum (регистронезависимо).
    /// </summary>
    [Theory]
    [InlineData("OK", Verdict.Ok)]
    [InlineData("WARNING", Verdict.Warning)]
    [InlineData("CRITICAL", Verdict.Critical)]
    [InlineData("UNKNOWN", Verdict.Unknown)]
    [InlineData("critical", Verdict.Critical)]
    public void Parse_KnownWire_MapsToEnum(string wire, Verdict expected)
    {
        Assert.Equal(expected, VerdictFormat.Parse(wire));
    }

    /// <summary>
    /// Нераспознанная строка вердикта — Unknown (не падает, не «угадывает»).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("FATAL")]
    public void Parse_UnknownWire_ReturnsUnknown(string? wire)
    {
        Assert.Equal(Verdict.Unknown, VerdictFormat.Parse(wire));
    }

    /// <summary>
    /// Wire-формат не меняется: верхний регистр канонических значений.
    /// </summary>
    [Theory]
    [InlineData(Verdict.Ok, "OK")]
    [InlineData(Verdict.Warning, "WARNING")]
    [InlineData(Verdict.Critical, "CRITICAL")]
    [InlineData(Verdict.Unknown, "UNKNOWN")]
    public void ToWire_CanonicalUppercase(Verdict verdict, string expected)
    {
        Assert.Equal(expected, VerdictFormat.ToWire(verdict));
    }

    /// <summary>
    /// CorrectVerdict работает по enum: critical-находка повышает,
    /// отсутствие critical понижает CRITICAL до WARNING.
    /// </summary>
    [Fact]
    public void CorrectVerdict_UsesEnum()
    {
        var critical = new Finding { Severity = FindingSeverity.Critical };
        var warning = new Finding { Severity = FindingSeverity.Warning };

        Assert.Equal(Verdict.Critical, ResponseParser.CorrectVerdict(Verdict.Warning, [critical]));
        Assert.Equal(Verdict.Warning, ResponseParser.CorrectVerdict(Verdict.Critical, [warning]));
        Assert.Equal(Verdict.Ok, ResponseParser.CorrectVerdict(Verdict.Ok, []));
    }

    /// <summary>
    /// Парсинг JSON-ответа модели даёт enum-вердикт (граница входа).
    /// </summary>
    [Fact]
    public void Parse_ValidJson_VerdictIsEnum()
    {
        var json = """{"verdict":"WARNING","summary":"s","findings":[]}""";

        var result = ResponseParser.Parse(json, null, false);

        Assert.Equal(Verdict.Warning, result.Verdict);
    }

    /// <summary>
    /// Diagnosis несёт enum-вердикт.
    /// </summary>
    [Fact]
    public void Diagnosis_VerdictIsEnum()
    {
        var diagnosis = new Diagnosis(Verdict.Critical, "s", [], [], [], 0, "native");

        Assert.Equal(Verdict.Critical, diagnosis.Verdict);
    }
}
