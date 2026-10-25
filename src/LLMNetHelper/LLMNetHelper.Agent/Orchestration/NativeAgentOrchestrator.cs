using System.Text;
using System.Text.Json;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Analysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LLMNetHelper.Agent.Orchestration;

/// <summary>
/// Нативная реализация оркестратора: собственный ReAct-цикл поверх
/// <see cref="IChatClient"/> из Microsoft.Extensions.AI.
/// <para>
/// Семантически эквивалентна SK-оркестратору (фаза 5): тот же контракт,
/// те же инструменты, тот же формат ответа. Отличается только «движок».
/// </para>
/// <para>
/// Ограничения цикла:
/// <list type="bullet">
///   <item><see cref="AgentOrchestrationOptions.MaxSteps"/> — максимум итераций;</item>
///   <item><see cref="AgentOrchestrationOptions.ToolCallLimit"/> — максимум вызовов инструментов;</item>
///   <item>любая ошибка модели превращается в вердикт UNKNOWN, а не в исключение.</item>
/// </list>
/// </para>
/// </summary>
public sealed class NativeAgentOrchestrator : IAgentOrchestrator
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IChatClient _chat;
    private readonly IToolRegistry _registry;
    private readonly AgentOrchestrationOptions _options;
    private readonly ILogger<NativeAgentOrchestrator> _logger;

    public NativeAgentOrchestrator(
        IChatClient chat,
        IToolRegistry registry,
        AgentOrchestrationOptions options,
        ILogger<NativeAgentOrchestrator> logger)
    {
        _chat = chat;
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderName => "native";

    /// <inheritdoc />
    public async Task<Diagnosis> DiagnoseAsync(AgentContext context, CancellationToken ct = default)
    {
        var toolDescriptions = _registry is Tools.ToolRegistry concrete
            ? concrete.DescribeForPrompt()
            : string.Join('\n', _registry.Tools.Select(static t => $"- `{t.Name}`: {t.Description}"));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, AgentPromptBuilder.BuildSystemPrompt(toolDescriptions, _options.DomainContext)),
            new(ChatRole.User, AgentPromptBuilder.BuildUserPrompt(context))
        };

        var toolCalls = 0;

        // Дефект N4 (план 22): накапливаем usage модели по всем вызовам цикла,
        // чтобы токены доходили до отчёта (раньше терялись между транспортом и рендерером).
        int? promptTokens = null;
        int? completionTokens = null;

        for (var step = 0; step < _options.MaxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();

            string? responseText;

            try
            {
                var response = await _chat.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
                responseText = response.Text;

                if (response.Usage is { } usage)
                {
                    if (usage.InputTokenCount is { } i)
                    {
                        promptTokens = (promptTokens ?? 0) + (int)Math.Min(i, int.MaxValue);
                    }

                    if (usage.OutputTokenCount is { } o)
                    {
                        completionTokens = (completionTokens ?? 0) + (int)Math.Min(o, int.MaxValue);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой вызова LLM на шаге {Step}", step);
                return Unknown($"Сбой LLM: {ex.Message}", toolCalls, promptTokens, completionTokens);
            }

            if (string.IsNullOrWhiteSpace(responseText))
            {
                return Unknown("LLM вернул пустой ответ", toolCalls, promptTokens, completionTokens);
            }

            // Пытаемся разобрать как финальный JSON.
            if (TryParseDiagnosis(responseText, toolCalls, promptTokens, completionTokens, out var diagnosis))
            {
                return diagnosis!;
            }

            // Иначе — ищем запрос на вызов инструмента.
            if (!TryParseToolCall(responseText, out var toolName, out var toolArgs))
            {
                // Модель ответила текстом без JSON — это финальный ответ в свободной форме.
                return FallbackDiagnosis(responseText, toolCalls, promptTokens, completionTokens);
            }

            if (toolCalls >= _options.ToolCallLimit)
            {
                _logger.LogWarning("Достигнут лимит вызовов инструментов ({Limit})", _options.ToolCallLimit);
                return Unknown("Достигнут лимит вызовов инструментов", toolCalls, promptTokens, completionTokens);
            }

            var record = await _registry.InvokeAsync(toolName, toolArgs, ct).ConfigureAwait(false);
            toolCalls++;

            _logger.LogInformation(
                "Инструмент {Tool}: {Chars} символов за {Ms} мс, успех={Ok}",
                record.ToolName, record.ResultChars, record.DurationMs, record.Succeeded);

            // Добавляем в историю и ответ модели, и результат инструмента.
            messages.Add(new ChatMessage(ChatRole.Assistant, responseText));
            messages.Add(new ChatMessage(ChatRole.User,
                $"Результат инструмента `{toolName}`:\n{DescribeToolResult(record, toolName)}"));
        }

        _logger.LogWarning("Достигнут лимит шагов ({Limit})", _options.MaxSteps);
        return Unknown("Достигнут лимит шагов", toolCalls, promptTokens, completionTokens);
    }

    /// <inheritdoc />
    public async Task<FixPlan> PlanFixAsync(
        Diagnosis diagnosis,
        AgentContext context,
        CancellationToken ct = default)
    {
        if (!_options.EnableFixPlanning)
        {
            return FixPlan.Empty;
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, AgentPromptBuilder.BuildSystemPrompt("(планирование правки)", _options.DomainContext)),
            new(ChatRole.User, AgentPromptBuilder.BuildUserPrompt(context)),
            new(ChatRole.User, AgentPromptBuilder.BuildPlanPrompt(diagnosis, context))
        };

        try
        {
            var response = await _chat.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            var text = response.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                return FixPlan.Empty;
            }

            return TryParseFixPlan(text) ?? FixPlan.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Сбой этапа планирования правки");
            return FixPlan.Empty;
        }
    }

    /// <summary>
    /// Дополняет результат инструмента, если тот провалился: модель должна
    /// понимать, что инструмент не сработал, а не думать, что данных нет.
    /// </summary>
    private string DescribeToolResult(ToolCallRecord record, string toolName)
    {
        if (!record.Succeeded)
        {
            return $"Инструмент `{toolName}` не зарегистрирован в белом списке. " +
                   "Используй только инструменты из системного промпта.";
        }

        return $"успешно, {record.ResultChars} символов";
    }

    /// <summary>
    /// Разбор финального диагноза.
    /// </summary>
    private bool TryParseDiagnosis(
        string text,
        int toolCalls,
        int? promptTokens,
        int? completionTokens,
        out Diagnosis? diagnosis)
    {
        diagnosis = null;

        var json = ExtractJson(text);
        if (json is null)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("verdict", out _))
            {
                return false;
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

            diagnosis = new Diagnosis(
                verdict,
                summary,
                findings,
                hypotheses,
                questions,
                toolCalls,
                ProviderName,
                promptTokens,
                completionTokens);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Разбор запроса на вызов инструмента.
    /// Ожидаемые формы:
    /// <c>{"tool":"docs_search","arguments":{"query":"..."}}</c>
    /// либо <c>{"tool":"docs_search","arguments":"{\"query\":\"...\"}"}</c>
    /// </summary>
    private static bool TryParseToolCall(string text, out string toolName, out string argumentsJson)
    {
        toolName = "";
        argumentsJson = "{}";

        var json = ExtractJson(text);
        if (json is null)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tool", out var toolProp) || toolProp.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            toolName = toolProp.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return false;
            }

            if (root.TryGetProperty("arguments", out var args))
            {
                argumentsJson = args.ValueKind switch
                {
                    JsonValueKind.String => args.GetString() ?? "{}",
                    JsonValueKind.Object => args.GetRawText(),
                    _ => "{}"
                };
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Разбор плана правки.
    /// </summary>
    private static FixPlan? TryParseFixPlan(string text)
    {
        var json = ExtractJson(text);
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
                    // План 22, этап 1.3: change_kind — перечисление; нераспознанный
                    // вид (как и пустая строка раньше) приводит к пропуску правки.
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

    /// <summary>
    /// Извлекает JSON-объект из ответа модели: убирает markdown-обёртку.
    /// </summary>
    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var end = text.LastIndexOf('}');
        if (end <= start)
        {
            return null;
        }

        return text[start..(end + 1)];
    }

    private static string GetString(JsonElement element, string property, string fallback)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    /// <summary>
    /// Диагноз при неструктурированном ответе модели.
    /// </summary>
    private Diagnosis FallbackDiagnosis(string text, int toolCalls, int? promptTokens, int? completionTokens)
    {
        var summary = text.Length > 500 ? text[..500] + "…" : text;

        return new Diagnosis(
            Verdict.Unknown,
            summary.Trim(),
            [],
            [],
            ["Модель вернула ответ в свободной форме — требуется ручной разбор."],
            toolCalls,
            ProviderName,
            promptTokens,
            completionTokens);
    }

    private Diagnosis Unknown(string reason, int toolCalls, int? promptTokens, int? completionTokens)
    {
        return new Diagnosis(Verdict.Unknown, reason, [], [], [], toolCalls, ProviderName, promptTokens, completionTokens);
    }
}
