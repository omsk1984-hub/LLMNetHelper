using LLMNetHelper.Abstractions;
using LLMNetHelper.Agent.AgentLoop;
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
        // План 22, этап 3.5: оркестратор — тонкий адаптер над общим ReAct-движком.
        // Собственной логики цикла/парсинга здесь больше нет.
        var toolDescriptions = _catalog.DescribeForPrompt();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptBuilder.BuildSystemPrompt(toolDescriptions, _options.DomainContext)),
            new(ChatRole.User, _promptBuilder.BuildUserPrompt(context))
        };

        var engine = new ReActLoopEngine();
        var strategy = new DiagnosisReActStrategy(_invoker);

        var outcome = await engine.RunAsync(
            _chat,
            messages,
            strategy,
            new ReActOptions(_options.MaxSteps, _options.ToolCallLimit),
            new LoggingObserver(_logger),
            ct).ConfigureAwait(false);

        return outcome.Diagnosis
            ?? new Diagnosis(
                Verdict.Unknown,
                "Движок не вернул диагноза.",
                [],
                [],
                [],
                outcome.ToolCalls,
                ProviderName,
                outcome.PromptTokens,
                outcome.CompletionTokens);
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
    /// Наблюдатель событий цикла: сохраняет диагностику вызовов инструмента,
    /// которая раньше была в теле цикла (план 22, этап 3.5).
    /// </summary>
    private sealed class LoggingObserver(ILogger<NativeAgentOrchestrator> logger) : IReActObserver
    {
        public void OnModelResponse(int? inputTokens, int? outputTokens)
        {
        }

        public void OnToolCall(int step, string toolName, string argumentsJson)
        {
        }

        public void OnToolResult(int step, string toolName, string resultText, bool succeeded)
        {
            logger.LogInformation(
                "Инструмент {Tool}: {Chars} символов, успех={Ok}",
                toolName, resultText.Length, succeeded);
        }
    }
}
