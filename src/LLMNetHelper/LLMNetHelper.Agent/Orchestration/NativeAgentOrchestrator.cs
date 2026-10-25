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
    private readonly IChatClient _chat;
    // План 22, этап 2.6: сегрегированные контракты вместо жирного IToolRegistry
    // и устранение проверки типа `_registry is Tools.ToolRegistry`.
    private readonly IToolCatalog _catalog;
    private readonly IToolInvoker _invoker;
    private readonly AgentOrchestrationOptions _options;
    private readonly ILogger<NativeAgentOrchestrator> _logger;
    // План 22, этап 6.1: промпты через абстракцию (подменяемость, версионирование).
    private readonly IAgentPromptBuilder _promptBuilder;

    public NativeAgentOrchestrator(
        IChatClient chat,
        IToolCatalog catalog,
        IToolInvoker invoker,
        AgentOrchestrationOptions options,
        ILogger<NativeAgentOrchestrator> logger,
        IAgentPromptBuilder? promptBuilder = null)
    {
        _chat = chat;
        _catalog = catalog;
        _invoker = invoker;
        _options = options;
        _logger = logger;
        _promptBuilder = promptBuilder ?? new AgentPromptBuilder();
    }

    /// <summary>
    /// Convenience-конструктор: единый реестр-фасад (<see cref="IToolRegistry"/>)
    /// как каталог и инвокер. Сохраняет совместимость с DI-регистрацией и тестами.
    /// </summary>
    public NativeAgentOrchestrator(
        IChatClient chat,
        IToolRegistry registry,
        AgentOrchestrationOptions options,
        ILogger<NativeAgentOrchestrator> logger,
        IAgentPromptBuilder? promptBuilder = null)
        : this(chat, registry, registry, options, logger, promptBuilder)
    {
    }

    /// <inheritdoc />
    public string ProviderName => "native";

    /// <inheritdoc />
    public async Task<Diagnosis> DiagnoseAsync(AgentContext context, CancellationToken ct = default)
    {
        // План 22, этап 2.6: описание берётся из контракта каталога —
        // проверка типа конкретного ToolRegistry устранена.
        var toolDescriptions = _catalog.DescribeForPrompt();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptBuilder.BuildSystemPrompt(toolDescriptions, _options.DomainContext)),
            new(ChatRole.User, _promptBuilder.BuildUserPrompt(context))
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
            if (!ToolCallParser.TryParse(responseText, out var toolName, out var toolArgs))
            {
                // Модель ответила текстом без JSON — это финальный ответ в свободной форме.
                return FallbackDiagnosis(responseText, toolCalls, promptTokens, completionTokens);
            }

            if (toolCalls >= _options.ToolCallLimit)
            {
                _logger.LogWarning("Достигнут лимит вызовов инструментов ({Limit})", _options.ToolCallLimit);
                return Unknown("Достигнут лимит вызовов инструментов", toolCalls, promptTokens, completionTokens);
            }

            var record = await _invoker.InvokeAsync(toolName, toolArgs, ct).ConfigureAwait(false);
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
            new(ChatRole.System, _promptBuilder.BuildSystemPrompt("(планирование правки)", _options.DomainContext)),
            new(ChatRole.User, _promptBuilder.BuildUserPrompt(context)),
            new(ChatRole.User, _promptBuilder.BuildPlanPrompt(diagnosis, context))
        };

        try
        {
            var response = await _chat.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            var text = response.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                return FixPlan.Empty;
            }

            return FixPlanParser.TryParse(text) ?? FixPlan.Empty;
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

        var json = JsonText.Extract(text);
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
