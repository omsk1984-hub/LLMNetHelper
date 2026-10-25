using System.Text.Json;
using LLMNetHelper.Abstractions;

namespace LLMNetHelper.Core.Analysis;

/// <summary>
/// Разбор ответа диагноза из модели (план 22, этапы 3.2/3.3).
/// <para>
/// Единая реализация вместо приватного <c>NativeAgentOrchestrator.TryParseDiagnosis</c>
/// и частичной копии в <c>ResponseParser.ParseJsonObject</c>. Формат JSON:
/// <c>verdict</c>, <c>summary</c>, <c>findings[]</c> (маппинг через
/// <see cref="ResponseParser.ParseFinding"/>), <c>hypotheses[]</c>, <c>questions_for_owner[]</c>.
/// Вердикт корректируется по находкам общим правилом
/// (<see cref="ResponseParser.CorrectVerdict"/>), поэтому обе ветки (агентская и
/// неагентская) дают одинаковый вердикт на одной комбинации находок (дефект D5, план 18).
/// </para>
/// <para>
/// Размещён в Core (не в Abstractions): сервисная утилита с доменными типами,
/// но не контракт.
/// </para>
/// </summary>
public static class DiagnosisParser
{
    /// <summary>
    /// Пытается разобрать ответ диагноза. Возвращает <c>null</c>, если в тексте
    /// нет объекта с полем <c>verdict</c> либо разбор упал (модель в ответе не
    /// дала структурированного диагноза).
    /// </summary>
    /// <param name="text">Текст ответа модели (JSON, возможно в markdown-обёртке).</param>
    /// <param name="orchestrator">Имя оркестратора ("native").</param>
    /// <param name="toolCalls">Сколько вызовов инструментов сделано к этому шагу.</param>
    /// <param name="promptTokens">Накопленные входные токены (может быть null).</param>
    /// <param name="completionTokens">Накопленные выходные токены (может быть null).</param>
    public static Diagnosis? TryParse(
        string text,
        string orchestrator,
        int toolCalls,
        int? promptTokens,
        int? completionTokens)
    {
        var json = JsonText.Extract(text);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("verdict", out _))
            {
                return null;
            }

            var verdict = VerdictFormat.Parse(GetString(root, "verdict", "UNKNOWN"));
            var summary = GetString(root, "summary", "");

            var hypotheses = new List<Hypothesis>();
            if (root.TryGetProperty("hypotheses", out var hArr) && hArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in hArr.EnumerateArray())
                {
                    var refs = new List<string>();
                    if (h.TryGetProperty("references", out var rArr) && rArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in rArr.EnumerateArray())
                        {
                            if (r.ValueKind == JsonValueKind.String)
                            {
                                refs.Add(r.GetString() ?? "");
                            }
                        }
                    }

                    // План 22, этап 1.4: confidence — перечисление; нераспознанное
                    // значение сохраняется как Other с исходной строкой.
                    var confidence = FindingFormat.ParseConfidence(
                        GetString(h, "confidence", "low"), out var rawConfidence);

                    hypotheses.Add(new Hypothesis(
                        GetString(h, "root_cause", ""),
                        confidence,
                        GetString(h, "evidence", ""),
                        refs,
                        rawConfidence));
                }
            }

            var questions = new List<string>();
            if (root.TryGetProperty("questions_for_owner", out var qArr) && qArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var q in qArr.EnumerateArray())
                {
                    if (q.ValueKind == JsonValueKind.String)
                    {
                        questions.Add(q.GetString() ?? "");
                    }
                }
            }

            // Находки маппим сразу в типизированную модель (дефект D1, план 18):
            // ранее сюда клались сырые JsonElement, которые терялись при
            // фильтрации `item is Finding` в отчётности.
            var findings = new List<Finding>();
            if (root.TryGetProperty("findings", out var fArr) && fArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in fArr.EnumerateArray())
                {
                    findings.Add(ResponseParser.ParseFinding(f));
                }
            }

            // Дефект D5 (план 18): вердикт корректируется по находкам тем же
            // общим правилом, что и в неагентском парсере.
            verdict = ResponseParser.CorrectVerdict(verdict, findings);

            return new Diagnosis(
                verdict,
                summary,
                findings,
                hypotheses,
                questions,
                toolCalls,
                orchestrator,
                promptTokens,
                completionTokens);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string GetString(JsonElement element, string property, string fallback)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }
}