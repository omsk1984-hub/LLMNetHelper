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
            var diagnosis = DiagnosisParser.TryParse(
                responseText, ProviderName, toolCalls, promptTokens, completionTokens);
            if (diagnosis is not null)
            {
                return diagnosis;
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
