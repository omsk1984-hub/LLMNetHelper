using LLMNetHelper.Abstractions;
using LLMNetHelper.Agent.Orchestration;
using LLMNetHelper.Agent.Tools;
using Microsoft.Extensions.AI;

namespace LLMNetHelper.Tests.Agent;

/// <summary>
/// Тесты нативного оркестратора на фейковом <see cref="IChatClient"/>.
/// Сеть не используется.
/// </summary>
public class NativeAgentOrchestratorTests
{
    /// <summary>
    /// Фейковый чат-клиент: отдаёт заранее заданную очередь ответов.
    /// Реализует контракт MEAI 10.5.0 (GetResponseAsync / ChatResponse).
    /// </summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<string> _responses;

        public ScriptedChatClient(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        public int CallCount { get; private set; }

        public List<IList<ChatMessage>> ReceivedMessages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ReceivedMessages.Add(messages.ToList());

            var text = _responses.Count > 0 ? _responses.Dequeue() : "";
            var message = new ChatMessage(ChatRole.Assistant, text);

            var response = new ChatResponse(message)
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = "scripted-model"
            };

            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Инструмент-заглушка, который считает вызовы.
    /// </summary>
    private sealed class CountingTool(string name, string result) : IAgentTool
    {
        public int Calls { get; private set; }

        public string Name => name;

        public string Description => "тестовый инструмент";

        public string ParametersJsonSchema => """{"type":"object"}""";

        public Task<string> InvokeAsync(string parametersJson, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// Фейковый клиент: отдаёт один ответ с заполненным <c>Usage</c>
    /// (дефект N4, план 22 — токены терялись между транспортом и отчётом).
    /// </summary>
    private sealed class UsageChatClient(string text, long inputTokens, long outputTokens) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var message = new ChatMessage(ChatRole.Assistant, text);
            var response = new ChatResponse(message)
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = "usage-model",
                Usage = new UsageDetails
                {
                    InputTokenCount = inputTokens,
                    OutputTokenCount = outputTokens
                }
            };

            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Дефект N4 (план 22): usage модели обязан доходить до <see cref="Diagnosis"/>,
    /// а не теряться по дороге в отчёт (раньше <c>GenerateReportStep</c>
    /// передавал в рендереры <c>null</c>).
    /// </summary>
    [Fact]
    public async Task Diagnose_UsageInResponse_CarriedIntoDiagnosis()
    {
        var chat = new UsageChatClient("""{"verdict":"OK","summary":"с"}""", 10, 5);
        var orchestrator = Create(chat, []);

        var diagnosis = await orchestrator.DiagnoseAsync(Context());

        Assert.Equal(Verdict.Ok, diagnosis.Verdict);
        Assert.Equal(10, diagnosis.PromptTokens);
        Assert.Equal(5, diagnosis.CompletionTokens);
    }

    private static AgentContext Context()
    {
        return new AgentContext(
            RunId: 1,
            WindowFrom: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            WindowTo: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            SnapshotText: "# Сводка\nошибок: 0",
            ContextDocuments: [],
            CodeStructureSummary: null);
    }

    private static NativeAgentOrchestrator Create(
        IChatClient chat,
        IEnumerable<IAgentTool> tools,
        AgentOrchestrationOptions? options = null)
    {
        var registry = new ToolRegistry(tools);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<NativeAgentOrchestrator>.Instance;

        return new NativeAgentOrchestrator(
            chat, registry, options ?? new AgentOrchestrationOptions(), logger);
    }

    [Fact]
    public async Task Diagnose_ValidJson_ReturnsDiagnosis()
    {
        var chat = new ScriptedChatClient("""
            {
              "verdict": "WARNING",
              "summary": "рост ошибок в Writer",
              "findings": [{ "id": "F1", "severity": "warning", "title": "t" }],
              "hypotheses": [
                { "root_cause": "таймаут Finam", "confidence": "high", "evidence": "лог", "references": ["log:1"] }
              ],
              "questions_for_owner": ["проверить ключ?"]
            }
            """);

        var orchestrator = Create(chat, []);
        var diagnosis = await orchestrator.DiagnoseAsync(Context());

        Assert.Equal(Verdict.Warning, diagnosis.Verdict);
        Assert.Single(diagnosis.Hypotheses);
        Assert.Equal("таймаут Finam", diagnosis.Hypotheses[0].RootCause);
        Assert.Equal("native", diagnosis.Orchestrator);
        Assert.Equal(0, diagnosis.ToolCallCount);
    }

    [Fact]
    public async Task Diagnose_JsonInMarkdownFence_IsParsed()
    {
        var chat = new ScriptedChatClient("""
            Вот результат:
            ```json
            { "verdict": "OK", "summary": "всё хорошо" }
            ```
            """);

        var orchestrator = Create(chat, []);
        var diagnosis = await orchestrator.DiagnoseAsync(Context());

        Assert.Equal(Verdict.Ok, diagnosis.Verdict);
    }

    [Fact]
    public async Task Diagnose_ToolCallThenAnswer_InvokesTool()
    {
        var tool = new CountingTool("docs_search", "найдено: runbook");
        var chat = new ScriptedChatClient(
            """{"tool":"docs_search","arguments":{"query":"timeout"}}""",
            """{"verdict":"OK","summary":"после чтения документации всё ясно"}""");

        var orchestrator = Create(chat, [tool]);
        var diagnosis = await orchestrator.DiagnoseAsync(Context());

        Assert.Equal(1, tool.Calls);
        Assert.Equal(Verdict.Ok, diagnosis.Verdict);
        Assert.Equal(1, diagnosis.ToolCallCount);
        Assert.Equal(2, chat.CallCount);
    }

    [Fact]
    public async Task Diagnose_UnknownTool_IsRejectedButLoopContinues()
    {
        var chat = new ScriptedChatClient(
            """{"tool":"rm_rf","arguments":{}}""",
            """{"verdict":"WARNING","summary":"инструмент недоступен"}""");

        var registry = new ToolRegistry([]);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<NativeAgentOrchestrator>.Instance;
        var orchestrator = new NativeAgentOrchestrator(chat, registry, new AgentOrchestrationOptions(), logger);

        var diagnosis = await orchestrator.DiagnoseAsync(Context());

        // Инструмент не в белом списке — но прогон не упал.
        Assert.Equal(Verdict.Warning, diagnosis.Verdict);
        Assert.Contains(registry.Calls, static c => !c.Succeeded);
    }

    [Fact]
    public async Task Diagnose_ToolCallLimit_StopsLoop()
    {
        // Модель бесконечно просит инструмент.
        var responses = Enumerable.Range(0, 20)
            .Select(static _ => """{"tool":"docs_search","arguments":{"query":"x"}}""")
            .ToArray();

        var tool = new CountingTool("docs_search", "результат");
        var chat = new ScriptedChatClient(responses);
        var options = new AgentOrchestrationOptions { MaxSteps = 20, ToolCallLimit = 3 };

        var orchestrator = Create(chat, [tool], options);
        var diagnosis = await orchestrator.DiagnoseAsync(Context());

        Assert.Equal(Verdict.Unknown, diagnosis.Verdict);
        Assert.Equal(3, tool.Calls);
    }

    [Fact]
    public async Task Diagnose_MaxStepsExhausted_ReturnsUnknown()
    {
        var responses = Enumerable.Range(0, 10)
            .Select(static _ => "просто текст без json")
            .ToArray();

        var chat = new ScriptedChatClient(responses);
        var options = new AgentOrchestrationOptions { MaxSteps = 2 };

        var orchestrator = Create(chat, [], options);
        var diagnosis = await orchestrator.DiagnoseAsync(Context());

        // Первый ответ без JSON — фолбэк (UNKNOWN), цикл завершается сразу.
        Assert.Equal(Verdict.Unknown, diagnosis.Verdict);
    }

    [Fact]
    public async Task Diagnose_FreeTextResponse_ReturnsFallback()
    {
        var chat = new ScriptedChatClient("Похоже, проблема в таймауте.");
        var orchestrator = Create(chat, []);

        var diagnosis = await orchestrator.DiagnoseAsync(Context());

        Assert.Equal(Verdict.Unknown, diagnosis.Verdict);
        Assert.Contains("таймаут", diagnosis.Summary);
        Assert.NotEmpty(diagnosis.QuestionsForOwner);
    }

    [Fact]
    public async Task Diagnose_EmptyResponse_ReturnsUnknown()
    {
        var chat = new ScriptedChatClient("");
        var orchestrator = Create(chat, []);

        var diagnosis = await orchestrator.DiagnoseAsync(Context());

        Assert.Equal(Verdict.Unknown, diagnosis.Verdict);
    }

    [Fact]
    public async Task PlanFix_ValidJson_ReturnsPlan()
    {
        var chat = new ScriptedChatClient("""
            {
              "rationale": "добавляем null-check",
              "changes": [
                { "file_path": "src/A.cs", "symbol_name": "Run", "change_kind": "InsertNullCheck",
                  "new_code": "if (x is null) return;", "explanation": "защита" }
              ],
              "risks": ["небольшой"],
              "requires_manual_review": true
            }
            """);

        var orchestrator = Create(chat, []);
        var diagnosis = new Diagnosis(Verdict.Warning, "s", [], [], [], 0, "native");

        var plan = await orchestrator.PlanFixAsync(diagnosis, Context());

        Assert.True(plan.HasChanges);
        Assert.Single(plan.Changes);
        Assert.Equal("Run", plan.Changes[0].SymbolName);
        Assert.True(plan.RequiresManualReview);
    }

    [Fact]
    public async Task PlanFix_EmptyChanges_ReturnsEmptyPlan()
    {
        var chat = new ScriptedChatClient("""{"rationale":"","changes":[],"risks":[]}""");
        var orchestrator = Create(chat, []);
        var diagnosis = new Diagnosis(Verdict.Ok, "s", [], [], [], 0, "native");

        var plan = await orchestrator.PlanFixAsync(diagnosis, Context());

        Assert.False(plan.HasChanges);
    }

    [Fact]
    public async Task PlanFix_Disabled_ReturnsEmptyWithoutCallingLlm()
    {
        var chat = new ScriptedChatClient("""{"rationale":"x","changes":[]}""");
        var options = new AgentOrchestrationOptions { EnableFixPlanning = false };
        var orchestrator = Create(chat, [], options);
        var diagnosis = new Diagnosis(Verdict.Ok, "s", [], [], [], 0, "native");

        var plan = await orchestrator.PlanFixAsync(diagnosis, Context());

        Assert.False(plan.HasChanges);
        Assert.Equal(0, chat.CallCount);
    }

    [Fact]
    public async Task Diagnose_WithFindings_PreservesTypedFindings()
    {
        // Дефект D1 (план 18): оркестратор складывал в Diagnosis.Findings сырые
        // JsonElement (f.Clone()), а GenerateReportStep.BuildAnalysis фильтровал
        // `item is Finding` — все находки молча терялись в отчёте.
        // После исправления находки обязаны быть типизированы как Finding.
        var chat = new ScriptedChatClient("""
            {
              "verdict": "WARNING",
              "summary": "s",
              "findings": [
                { "id": "F1", "severity": "warning", "title": "t1", "evidence": "e1",
                  "impact": "i1", "recommendation": "r1", "confidence": "high" },
                { "id": "F2", "severity": "critical", "title": "t2", "evidence": "e2",
                  "impact": "i2", "recommendation": "r2", "confidence": "low" }
              ]
            }
            """);

        var orchestrator = Create(chat, []);
        var diagnosis = await orchestrator.DiagnoseAsync(Context());

        Assert.Equal(2, diagnosis.Findings.Count);

        // Каждое находка должна быть типизирована как Finding (не JsonElement).
        // Тест устойчив к смене типа коллекции (IReadOnlyList<object> → IReadOnlyList<Finding>).
        foreach (var f in diagnosis.Findings)
        {
            Assert.IsType<Finding>(f);
        }

        var first = diagnosis.Findings[0];
        Assert.Equal("F1", first.Id);
        // План 22, этап 1.4: severity/confidence — перечисления.
        Assert.Equal(FindingSeverity.Warning, first.Severity);
        Assert.Equal("t1", first.Title);
        Assert.Equal("e1", first.Evidence);
        Assert.Equal("i1", first.Impact);
        Assert.Equal("r1", first.Recommendation);
        Assert.Equal(Confidence.High, first.Confidence);
    }

    [Fact]
    public async Task Diagnose_VerdictCorrectedByFindings()
    {
        // Дефект D5 (план 18): неагентский путь применял ValidateVerdict
        // (вердикт коррелирует с severity находок), агентский — принимал
        // вердикт модели как есть. Одна и та же комбинация давала разный
        // вердикт. Теперь коррекция обязательна на обоих путях.
        var chat = new ScriptedChatClient("""
            {
              "verdict": "OK",
              "summary": "s",
              "findings": [
                { "id": "F1", "severity": "critical", "title": "t" }
              ]
            }
            """);

        var orchestrator = Create(chat, []);
        var diagnosis = await orchestrator.DiagnoseAsync(Context());

        Assert.Equal(Verdict.Critical, diagnosis.Verdict);
    }

    [Fact]
    public async Task Diagnose_CriticalVerdictWithoutCriticalFinding_IsDowngraded()
    {
        // Обратная сторона коррекции: CRITICAL без critical-находки → WARNING
        // (та же семантика, что в ResponseParser.ValidateVerdict).
        var chat = new ScriptedChatClient("""
            {
              "verdict": "CRITICAL",
              "summary": "s",
              "findings": [
                { "id": "F1", "severity": "warning", "title": "t" }
              ]
            }
            """);

        var orchestrator = Create(chat, []);
        var diagnosis = await orchestrator.DiagnoseAsync(Context());

        Assert.Equal(Verdict.Warning, diagnosis.Verdict);
    }
}

public class ToolRegistryTests
{
    private sealed class Tool(string name) : IAgentTool
    {
        public string Name => name;

        public string Description => "d";

        public string ParametersJsonSchema => """{"type":"object"}""";

        public Task<string> InvokeAsync(string parametersJson, CancellationToken ct = default) =>
            Task.FromResult("ok");
    }

    [Fact]
    public void Constructor_DuplicateNames_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ToolRegistry([new Tool("a"), new Tool("a")]));
    }

    [Fact]
    public void Find_Unknown_ReturnsNull()
    {
        var registry = new ToolRegistry([new Tool("a")]);
        Assert.Null(registry.Find("b"));
    }

    [Fact]
    public async Task Invoke_Unknown_IsRejectedAndRecorded()
    {
        var registry = new ToolRegistry([new Tool("a")]);
        var record = await registry.InvokeAsync("nope", "{}");

        Assert.False(record.Succeeded);
        Assert.Single(registry.Calls);
    }

    [Fact]
    public async Task Invoke_Known_ExecutesAndRecords()
    {
        var registry = new ToolRegistry([new Tool("a")]);
        var record = await registry.InvokeAsync("a", "{}");

        Assert.True(record.Succeeded);
        Assert.Equal("a", record.ToolName);
        Assert.Single(registry.Calls);
    }

    [Fact]
    public void DescribeForPrompt_ListsAllTools()
    {
        var registry = new ToolRegistry([new Tool("alpha"), new Tool("beta")]);
        var text = registry.DescribeForPrompt();

        Assert.Contains("alpha", text);
        Assert.Contains("beta", text);
    }

    [Fact]
    public async Task Reset_ClearsCallJournal()
    {
        // Дефект D4 (план 18): ToolRegistry зарегистрирован singleton и накапливает
        // _calls между прогонами — журнал одного прогона подмешивается в следующий.
        var registry = new ToolRegistry([new Tool("a")]);

        await registry.InvokeAsync("a", "{}");
        Assert.Single(registry.Calls);

        registry.Reset();
        Assert.Empty(registry.Calls);
    }
}

/// <summary>
/// Тесты системного промпта: доменный контекст — из конфигурации, а не из кода.
/// </summary>
public class AgentPromptBuilderTests
{
    // План 22, этап 6.1: AgentPromptBuilder — экземплярный класс (IAgentPromptBuilder).
    private static readonly AgentPromptBuilder Builder = new();

    [Fact]
    public void SystemPrompt_NoDomainContext_UsesNeutralText()
    {
        var prompt = Builder.BuildSystemPrompt("- tool: desc");

        Assert.Contains(AgentPromptBuilder.NeutralDomainContext, prompt);
        // Ни одного упоминания домена конкретного развёртывания.
        Assert.DoesNotContain("Artas2026", prompt);
        Assert.DoesNotContain("Finam", prompt);
        Assert.DoesNotContain("фрактал", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SystemPrompt_WithDomainContext_InsertsItVerbatim()
    {
        const string domain = "Система — торговый терминал: поток котировок → PostgreSQL, логи в Loki.";

        var prompt = Builder.BuildSystemPrompt("- tool: desc", domain);

        Assert.Contains("## Анализируемая система", prompt);
        Assert.Contains(domain, prompt);
        Assert.DoesNotContain(AgentPromptBuilder.NeutralDomainContext, prompt);
    }

    [Fact]
    public void SystemPrompt_WhitespaceDomainContext_TreatedAsUnset()
    {
        var prompt = Builder.BuildSystemPrompt("- tool: desc", "   \n  ");

        Assert.Contains(AgentPromptBuilder.NeutralDomainContext, prompt);
    }
}
