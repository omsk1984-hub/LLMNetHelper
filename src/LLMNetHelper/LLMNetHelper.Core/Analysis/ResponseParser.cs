using LLMNetHelper.Abstractions;
using System.Text.Json;

namespace LLMNetHelper.Core.Analysis;

/// <summary>
/// Модель ответа LLM после парсинга.
/// </summary>
public sealed class AnalysisResult
{
    public string Verdict { get; set; } = "UNKNOWN";
    public string Summary { get; set; } = "";
    public List<Finding> Findings { get; set; } = [];
    public List<string> NoiseCandidates { get; set; } = [];
    public List<string> QuestionsForOwner { get; set; } = [];
    public bool FallbackUsed { get; set; }
}

// Finding перенесён в LLMNetHelper.Abstractions (план 18, пункт 0.1):
// тип участвует в контракте Diagnosis между оркестратором и отчётностью.

/// <summary>
/// Разбор ответа LLM: валидный JSON, обрезанный JSON, Markdown-фолбэк.
/// Общий для обеих реализаций (custom и meai).
/// </summary>
public static class ResponseParser
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Парсит ответ LLM.
    /// </summary>
    public static AnalysisResult Parse(string? jsonText, string? textContent, bool fallbackUsed)
    {
        var result = new AnalysisResult { FallbackUsed = fallbackUsed };

        if (!string.IsNullOrEmpty(jsonText))
        {
            try
            {
                // Дефект N3 (план 22): JsonDocument — IDisposable, обязателен using.
                using var doc = JsonDocument.Parse(jsonText);
                ParseJsonObject(doc.RootElement, result);
                ValidateVerdict(result);
                return result;
            }
            catch
            {
                // Fallback to truncated JSON handling
            }
        }

        // Путь 2: извлечение JSON-блока из text
        if (!string.IsNullOrEmpty(textContent))
        {
            var extractedJson = ExtractJsonBlock(textContent);
            if (!string.IsNullOrEmpty(extractedJson))
            {
                try
                {
                    // Дефект N3 (план 22): JsonDocument — IDisposable, обязателен using.
                    using var doc = JsonDocument.Parse(extractedJson);
                    ParseJsonObject(doc.RootElement, result);
                    ValidateVerdict(result);
                    return result;
                }
                catch
                {
                    // Fall through to markdown fallback
                }
            }

            // Путь 3: Markdown-фолбэк
            result.Verdict = "UNKNOWN";
            result.Summary = "LLM вернул ответ в формате Markdown, структура не распознана.";
            result.Findings = [new Finding
            {
                Id = "F1",
                Severity = FindingSeverity.Info,
                Title = "Ответ неструктурирован",
                Evidence = textContent.Length > 500 ? textContent.Substring(0, 500) + "..." : textContent,
                Impact = "Невозно автоматически оценить критичность",
                Recommendation = "Просмотрите ответ вручную",
                Confidence = Confidence.Low
            }];
            return result;
        }

        result.Verdict = "UNKNOWN";
        result.Summary = "LLM не вернул содержимого.";
        return result;
    }

    private static void ParseJsonObject(JsonElement root, AnalysisResult result)
    {
        if (root.TryGetProperty("verdict", out var v) && v.ValueKind == JsonValueKind.String)
            result.Verdict = v.GetString() ?? "UNKNOWN";

        if (root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String)
            result.Summary = s.GetString() ?? "";

        if (root.TryGetProperty("findings", out var findings) && findings.ValueKind == JsonValueKind.Array)
        {
            foreach (var findingElem in findings.EnumerateArray())
            {
                result.Findings.Add(ParseFinding(findingElem));
            }
        }

        if (root.TryGetProperty("noise_candidates", out var noise) && noise.ValueKind == JsonValueKind.Array)
        {
            result.NoiseCandidates = noise.EnumerateArray()
                .Where(n => n.ValueKind == JsonValueKind.String)
                .Select(n => n.GetString() ?? "")
                .ToList();
        }

        if (root.TryGetProperty("questions_for_owner", out var questions) && questions.ValueKind == JsonValueKind.Array)
        {
            result.QuestionsForOwner = questions.EnumerateArray()
                .Where(q => q.ValueKind == JsonValueKind.String)
                .Select(q => q.GetString() ?? "")
                .ToList();
        }
    }

    /// <summary>
    /// Маппинг одного элемента массива `findings` в типизированную модель
    /// <see cref="Finding"/>. Общий для неагентского парсера и оркестратора
    /// (план 18, пункт 0.1): раньше оркестратор клал сырые JsonElement, и
    /// находки терялись при фильтрации по типу в отчётности (дефект D1).
    /// Необъектный элемент даёт пустую находку без исключения — оркестратор
    /// обязан возвращать UNKNOWN, а не падать.
    /// </summary>
    public static Finding ParseFinding(JsonElement findingElem)
    {
        var finding = new Finding();

        if (findingElem.ValueKind != JsonValueKind.Object)
        {
            return finding;
        }

        if (findingElem.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
            finding.Id = idProp.GetString() ?? "";
        // План 22, этап 1.4: severity/confidence — перечисления; нераспознанное
        // значение сохраняется как Other с исходной строкой.
        if (findingElem.TryGetProperty("severity", out var sevProp) && sevProp.ValueKind == JsonValueKind.String)
        {
            finding.Severity = FindingFormat.ParseSeverity(sevProp.GetString(), out var rawSeverity);
            finding.RawSeverity = rawSeverity;
        }
        if (findingElem.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String)
            finding.Title = titleProp.GetString() ?? "";
        if (findingElem.TryGetProperty("evidence", out var evProp) && evProp.ValueKind == JsonValueKind.String)
            finding.Evidence = evProp.GetString() ?? "";
        if (findingElem.TryGetProperty("impact", out var impProp) && impProp.ValueKind == JsonValueKind.String)
            finding.Impact = impProp.GetString() ?? "";
        if (findingElem.TryGetProperty("recommendation", out var recProp) && recProp.ValueKind == JsonValueKind.String)
            finding.Recommendation = recProp.GetString() ?? "";
        if (findingElem.TryGetProperty("confidence", out var confProp) && confProp.ValueKind == JsonValueKind.String)
        {
            finding.Confidence = FindingFormat.ParseConfidence(confProp.GetString(), out var rawConfidence);
            finding.RawConfidence = rawConfidence;
        }

        return finding;
    }

    private static string? ExtractJsonBlock(string text)
    {
        // Ищем fenced JSON-блок: ```json ... ``` или ``` ... ```
        var jsonStart = text.IndexOf("```", StringComparison.Ordinal);
        if (jsonStart >= 0)
        {
            var afterFence = text.Substring(jsonStart + 3);
            var jsonEnd = afterFence.IndexOf("```", StringComparison.Ordinal);
            if (jsonEnd > 0)
            {
                var inner = afterFence.Substring(0, jsonEnd).Trim();
                // Убираем "json" если есть
                if (inner.StartsWith("json", StringComparison.OrdinalIgnoreCase))
                    inner = inner.Substring(4).Trim();
                return inner;
            }
        }

        // Фолбэк: ищем первый { ... }
        var firstBrace = text.IndexOf('{');
        var lastBrace = text.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
            return text.Substring(firstBrace, lastBrace - firstBrace + 1);

        return null;
    }

    private static void ValidateVerdict(AnalysisResult result)
    {
        result.Verdict = CorrectVerdict(result.Verdict, result.Findings);
    }

    /// <summary>
    /// Коррекция вердикта по находкам: вердикт обязан коррелировать с
    /// severity находок. Общий для неагентского парсера и оркестратора
    /// (план 18, пункт 0.5, дефект D5): раньше агентский путь принимал
    /// вердикт модели как есть, и одна и та же комбинация находок давала
    /// разный вердикт на разных путях.
    /// </summary>
    public static string CorrectVerdict(string verdict, IReadOnlyList<Finding> findings)
    {
        // План 22, этап 1.4: сравнение по enum; Other не влияет на вердикт.
        var hasCritical = findings.Any(f => f.Severity == FindingSeverity.Critical);
        var hasWarning = findings.Any(f => f.Severity == FindingSeverity.Warning);

        if (hasCritical && verdict != "CRITICAL")
            return "CRITICAL"; // Повышаем: есть critical-находка
        if (!hasCritical && hasWarning && verdict == "CRITICAL")
            return "WARNING"; // Нет critical → не CRITICAL

        return verdict;
    }
}
