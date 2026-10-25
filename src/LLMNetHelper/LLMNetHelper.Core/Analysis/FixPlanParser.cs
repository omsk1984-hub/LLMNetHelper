using System.Text.Json;
using LLMNetHelper.Abstractions;

namespace LLMNetHelper.Core.Analysis;

/// <summary>
/// Разбор плана правки из ответа модели (план 22, этап 3.2).
/// <para>
/// Единая реализация вместо приватного метода <c>NativeAgentOrchestrator.TryParseFixPlan</c>.
/// Формат: объект с <c>rationale</c>, <c>requires_manual_review</c>,
/// массивом <c>changes</c> (каждый: <c>file_path</c>, <c>symbol_name</c>,
/// <c>change_kind</c>, <c>new_code</c>, <c>explanation</c>) и массивом <c>risks</c>.
/// Правка без <c>file_path</c>/<c>symbol_name</c> или с нераспознанным
/// <c>change_kind</c> молча пропускается (прежнее поведение, план 22, этап 1.3).
/// </para>
/// <para>
/// Размещён в Core (не в Abstractions): использует доменных типы
/// <see cref="FixPlan"/>/<see cref="ProposedChange"/>, но является сервисной
/// утилитой, не контрактом.
/// </para>
/// </summary>
public static class FixPlanParser
{
    /// <summary>
    /// Пытается разобрать план правки. Возвращает <c>null</c> при отсутствии
    /// корректного JSON или при исключении разбора.
    /// </summary>
    public static FixPlan? TryParse(string text)
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

            var rationale = GetString(root, "rationale", "");
            var requiresReview = root.TryGetProperty("requires_manual_review", out var r) &&
                                 r.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                                 r.GetBoolean();

            var changes = new List<ProposedChange>();
            if (root.TryGetProperty("changes", out var cArr) && cArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cArr.EnumerateArray())
                {
                    var filePath = GetString(c, "file_path", "");
                    var symbol = GetString(c, "symbol_name", "");
                    var kind = ChangeKindFormat.Parse(GetString(c, "change_kind", ""));
                    var newCode = GetString(c, "new_code", "");

                    if (string.IsNullOrWhiteSpace(filePath) ||
                        string.IsNullOrWhiteSpace(symbol) ||
                        kind == ChangeKind.Unknown)
                    {
                        continue;
                    }

                    changes.Add(new ProposedChange(
                        filePath,
                        symbol,
                        kind,
                        newCode,
                        GetString(c, "explanation", "")));
                }
            }

            var risks = new List<string>();
            if (root.TryGetProperty("risks", out var risksArr) && risksArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var risk in risksArr.EnumerateArray())
                {
                    if (risk.ValueKind == JsonValueKind.String)
                    {
                        risks.Add(risk.GetString() ?? "");
                    }
                }
            }

            return new FixPlan(rationale, changes, risks, requiresReview);
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