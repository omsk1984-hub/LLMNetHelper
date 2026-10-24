using LLMNetHelper.Abstractions;
using LLMNetHelper.Chat;
using LLMNetHelper.GitLab;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
// Алиас: LLMNetHelper.Chat.ChatOptions конфликтует с Microsoft.Extensions.AI.ChatOptions.
using ChatOpts = LLMNetHelper.Chat.ChatOptions;
using MeaiChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace LLMNetHelper.Tests.Chat;

/// <summary>
/// Тесты Markdown-транскрипта диалога чата (план 20): именование файла,
/// структура сеанса, отключение, отказоустойчивость, интеграция с
/// <see cref="ChatAgentLoop"/> и <see cref="ChatSession"/>. Всё офлайн,
/// файлы — во временном каталоге.
/// </summary>
public sealed class ChatDialogLoggerTests : IDisposable
{
    private readonly string _root;

    public ChatDialogLoggerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"chat_dialog_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        GC.SuppressFinalize(this);
    }

    private ChatOpts Options(bool enabled = true) => new()
    {
        DialogLog = { Enabled = enabled, Directory = _root }
    };

    private static IReadOnlyList<(string Key, string Value)> Header() =>
        [("Модель", "test-model @ http://localhost"), ("Область", "все репозитории"), ("Режим на старте", "/tools (ReAct)")];

    // ---------- Именование файла ----------

    [Fact]
    public void UniqueFilePath_Format_UtcStampedMd()
    {
        var utc = new DateTimeOffset(2026, 10, 24, 18, 15, 3, TimeSpan.Zero);

        var path = ChatDialogLogger.UniqueFilePath(_root, utc);

        Assert.Equal(Path.Combine(_root, "chat-2026-10-24_18-15-03Z.md"), path);
    }

    [Fact]
    public void UniqueFilePath_Collision_Suffix()
    {
        var utc = new DateTimeOffset(2026, 10, 24, 18, 15, 3, TimeSpan.Zero);
        File.WriteAllText(ChatDialogLogger.UniqueFilePath(_root, utc), "x");

        var second = ChatDialogLogger.UniqueFilePath(_root, utc);

        Assert.EndsWith("chat-2026-10-24_18-15-03Z-2.md", second, StringComparison.Ordinal);
    }

    // ---------- Создание / отключение ----------

    [Fact]
    public void TryCreate_Disabled_NullTranscript_NoFile()
    {
        var transcript = ChatDialogLogger.TryCreate(Options(enabled: false), Header());

        Assert.Same(NullChatTranscript.Instance, transcript);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public void TryCreate_Enabled_HeaderWithModelAndScope()
    {
        var transcript = ChatDialogLogger.TryCreate(Options(), Header());
        var logger = Assert.IsType<ChatDialogLogger>(transcript);

        Assert.True(File.Exists(logger.FilePath));
        logger.Dispose();

        var text = File.ReadAllText(logger.FilePath);
        Assert.Contains("# Транскрипт диалога LLMNetHelper Chat", text, StringComparison.Ordinal);
        Assert.Contains("- Модель: test-model @ http://localhost", text, StringComparison.Ordinal);
        Assert.Contains("- Режим на старте: /tools (ReAct)", text, StringComparison.Ordinal);
        Assert.Contains("Начало:", text, StringComparison.Ordinal);
        Assert.Contains("UTC", text, StringComparison.Ordinal);
    }

    // ---------- Структура сеанса ----------

    [Fact]
    public void Session_Sections_InOrder_AndFooter()
    {
        var transcript = (ChatDialogLogger)ChatDialogLogger.TryCreate(Options(), Header());

        transcript.LogUserMessage("Где App?");
        transcript.LogModelStep(0, """{"tool":"repo_search","arguments":{"query":"App"}}""");
        transcript.LogToolCall(0, "repo_search", """{"query":"App"}""");
        transcript.LogToolResult(0, "repo_search", "Найдено: 1 фрагмент", succeeded: true);
        transcript.LogAssistantAnswer("Класс App делает X.");
        transcript.LogNote("[Достигнут лимит шагов]");
        transcript.Dispose();

        var text = File.ReadAllText(transcript.FilePath);

        // Порядок секций.
        var iQuestion = text.IndexOf("## Вопрос 1 — ", StringComparison.Ordinal);
        var iUser = text.IndexOf("### Пользователь", StringComparison.Ordinal);
        var iModel = text.IndexOf("### Шаг 1 — ответ модели", StringComparison.Ordinal);
        var iCall = text.IndexOf("### Шаг 1 — вызов `repo_search`", StringComparison.Ordinal);
        var iResult = text.IndexOf("### Шаг 1 — результат `repo_search`: успех, 19 симв.", StringComparison.Ordinal);
        var iAnswer = text.IndexOf("### Финальный ответ — ", StringComparison.Ordinal);
        Assert.True(iQuestion < iUser && iUser < iModel && iModel < iCall && iCall < iResult && iResult < iAnswer,
            $"секции вне порядка:{Environment.NewLine}{text}");

        // Цитата пользователя и полные тексты.
        Assert.Contains("> Где App?", text, StringComparison.Ordinal);
        Assert.Contains("Найдено: 1 фрагмент", text, StringComparison.Ordinal);
        Assert.Contains("Класс App делает X.", text, StringComparison.Ordinal);
        Assert.Contains("_[Достигнут лимит шагов]_", text, StringComparison.Ordinal);

        // Футер с итогами.
        Assert.Contains("## Завершено:", text, StringComparison.Ordinal);
        Assert.Contains("Вопросов/команд: 1, вызовов инструментов: 1.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_MultipleQuestions_NumberedSequentially()
    {
        var transcript = (ChatDialogLogger)ChatDialogLogger.TryCreate(Options(), Header());

        transcript.LogUserMessage("первый");
        transcript.LogAssistantAnswer("ответ 1");
        transcript.LogUserMessage("второй");
        transcript.LogAssistantAnswer("ответ 2");
        transcript.Dispose();

        var text = File.ReadAllText(transcript.FilePath);

        Assert.Contains("## Вопрос 1 — ", text, StringComparison.Ordinal);
        Assert.Contains("## Вопрос 2 — ", text, StringComparison.Ordinal);
        Assert.Contains("Вопросов/команд: 2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Command_OutsideQuestion_OwnsNumbering()
    {
        var transcript = (ChatDialogLogger)ChatDialogLogger.TryCreate(Options(), Header());

        transcript.LogCommand("/tools");
        transcript.LogNote("[режим] /tools (ReAct)");
        transcript.LogUserMessage("вопрос");
        transcript.LogAssistantAnswer("ответ");
        transcript.Dispose();

        var text = File.ReadAllText(transcript.FilePath);

        Assert.Contains("### Команда — ", text, StringComparison.Ordinal);
        Assert.Contains("/tools", text, StringComparison.Ordinal);
        // Команда открыла секцию «Вопрос 1», сообщение — вторую.
        Assert.Contains("## Вопрос 1 — ", text, StringComparison.Ordinal);
        Assert.Contains("## Вопрос 2 — ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextDocuments_Default_FullFragmentContent()
    {
        var transcript = (ChatDialogLogger)ChatDialogLogger.TryCreate(Options(), Header());
        var docs = new List<ContextDocument>
        {
            new("local-folder", "demo/README.md", "demo/README.md", "полное содержимое фрагмента", 12.34),
            new("local-folder", "demo/src/Writer.cs", "demo/src/Writer.cs", "Writer", 9.87)
        };

        transcript.LogUserMessage("вопрос");
        transcript.LogContextDocuments(docs);
        transcript.LogAssistantAnswer("ответ");
        transcript.Dispose();

        var text = File.ReadAllText(transcript.FilePath);

        // Заголовок секции: число фрагментов + суммарный объём отправленного контекста.
        Assert.Contains("### Контекст (BM25): 2 фрагментов, всего 33 симв. — ", text, StringComparison.Ordinal);
        Assert.Contains("- `demo/README.md` (score 12.34, 27 симв.) — `demo/README.md`", text, StringComparison.Ordinal);
        // По умолчанию IncludeContextFragments=true: полный текст фрагмента
        // пишется в блоке ```text (ровно то, что видела модель).
        Assert.Contains("```text", text, StringComparison.Ordinal);
        Assert.Contains("полное содержимое фрагмента", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextDocuments_IncludeContextFragmentsFalse_AnnotationsOnly()
    {
        var options = Options();
        options.DialogLog.IncludeContextFragments = false;
        var transcript = (ChatDialogLogger)ChatDialogLogger.TryCreate(options, Header());
        var docs = new List<ContextDocument>
        {
            new("local-folder", "demo/README.md", "demo/README.md", "полное содержимое фрагмента", 12.34)
        };

        transcript.LogUserMessage("вопрос");
        transcript.LogContextDocuments(docs);
        transcript.LogAssistantAnswer("ответ");
        transcript.Dispose();

        var text = File.ReadAllText(transcript.FilePath);

        Assert.Contains("- `demo/README.md` (score 12.34, 27 симв.) — `demo/README.md`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("полное содержимое фрагмента", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FencedBlock_BacktickContent_EscalatesToFence4()
    {
        var transcript = (ChatDialogLogger)ChatDialogLogger.TryCreate(Options(), Header());

        transcript.LogUserMessage("вопрос");
        transcript.LogToolResult(0, "repo_read_file", "```cs\nvar x = 1;\n```", succeeded: true);
        transcript.LogAssistantAnswer("ответ");
        transcript.Dispose();

        var text = File.ReadAllText(transcript.FilePath);

        Assert.Contains("````text", text, StringComparison.Ordinal);
        Assert.Contains("```cs", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Dispose_Twice_NoSecondFooter()
    {
        var transcript = (ChatDialogLogger)ChatDialogLogger.TryCreate(Options(), Header());
        transcript.LogUserMessage("вопрос");
        transcript.LogAssistantAnswer("ответ");

        transcript.Dispose();
        transcript.Dispose();

        var text = File.ReadAllText(transcript.FilePath);
        Assert.Equal(1, text.Split(new[] { "## Завершено:" }, StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void UsageTokens_SectionSuffix_AndSessionFooter()
    {
        var transcript = (ChatDialogLogger)ChatDialogLogger.TryCreate(Options(), Header());

        transcript.LogUserMessage("вопрос");
        transcript.LogModelStep(0, "{\"tool\":\"repo_search\"}", inputTokens: 100, outputTokens: 20);
        transcript.LogToolCall(0, "repo_search", "{}");
        transcript.LogToolResult(0, "repo_search", "ок", succeeded: true);
        transcript.LogAssistantAnswer("ответ", inputTokens: 150, outputTokens: 40);
        transcript.Dispose();

        var text = File.ReadAllText(transcript.FilePath);

        // Суффикс usage в заголовках секций ответа модели.
        Assert.Contains("ответ модели (запрос инструмента) — ", text, StringComparison.Ordinal);
        Assert.Contains("· токены: вход 100, выход 20", text, StringComparison.Ordinal);
        Assert.Contains("· токены: вход 150, выход 40", text, StringComparison.Ordinal);
        // Итог по сеансу в футере.
        Assert.Contains("Токены за сеанс: вход 250, выход 60, всего 310.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageTokens_Absent_NoFooterLine()
    {
        var transcript = (ChatDialogLogger)ChatDialogLogger.TryCreate(Options(), Header());

        transcript.LogUserMessage("вопрос");
        transcript.LogAssistantAnswer("ответ");
        transcript.Dispose();

        var text = File.ReadAllText(transcript.FilePath);

        Assert.DoesNotContain("токены:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Токены за сеанс:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Utf8_CyrillicRoundTrip()
    {
        var transcript = (ChatDialogLogger)ChatDialogLogger.TryCreate(Options(), Header());

        transcript.LogUserMessage("Кириллица — проверка кодировки Ёё");
        transcript.LogAssistantAnswer("Ответ: Ёж");
        transcript.Dispose();

        var bytes = File.ReadAllBytes(transcript.FilePath);
        // UTF-8 без BOM.
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        var text = File.ReadAllText(transcript.FilePath, System.Text.Encoding.UTF8);
        Assert.Contains("Кириллица — проверка кодировки Ёё", text, StringComparison.Ordinal);
        Assert.Contains("Ответ: Ёж", text, StringComparison.Ordinal);
    }

    // ---------- Интеграция: ChatAgentLoop ----------

    /// <summary>Фейковый транскрипт: собирает события в список (без файла).</summary>
    private sealed class RecordingTranscript : IChatTranscript
    {
        public List<string> Events { get; } = [];

        public void LogUserMessage(string text) => Events.Add($"user:{text}");
        public void LogCommand(string commandLine) => Events.Add($"cmd:{commandLine}");
        public void LogModelStep(int step, string raw, int? inputTokens = null, int? outputTokens = null) =>
            Events.Add($"model:{step}:{raw}:{inputTokens}/{outputTokens}");
        public void LogToolCall(int step, string tool, string args) => Events.Add($"call:{step}:{tool}:{args}");
        public void LogToolResult(int step, string tool, string result, bool ok) => Events.Add($"result:{step}:{tool}:{(ok ? "ok" : "fail")}:{result}");
        public void LogAssistantAnswer(string text, int? inputTokens = null, int? outputTokens = null) =>
            Events.Add($"answer:{text}:{inputTokens}/{outputTokens}");
        public void LogNote(string text) => Events.Add($"note:{text}");
        public void LogContextDocuments(IReadOnlyList<ContextDocument> documents) => Events.Add($"ctx:{documents.Count}");
    }

    /// <summary>
    /// Фейковый клиент: очередь ответов (паттерн ChatAgentLoopTests);
    /// <paramref name="usage"/> — одинаковый usage на каждый ответ (или null).
    /// </summary>
    private sealed class ScriptedChatClient(params string[] responses) : IChatClient
    {
        private readonly Queue<string> _responses = new(responses);

        public UsageDetails? Usage { get; init; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            MeaiChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var text = _responses.Count > 0 ? _responses.Dequeue() : "";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
            {
                Usage = Usage
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            MeaiChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FixedTool(string name, string result) : LLMNetHelper.Abstractions.IAgentTool
    {
        public string Name => name;
        public string Description => "тестовый инструмент";
        public string ParametersJsonSchema => """{"type":"object"}""";
        public Task<string> InvokeAsync(string parametersJson, CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    [Fact]
    public async Task AgentLoop_TranscriptCapturesFullToolCycle()
    {
        var chat = new ScriptedChatClient(
            """{"tool":"repo_search","arguments":{"query":"App"}}""",
            "Итоговый ответ.");
        var transcript = new RecordingTranscript();
        var loop = new ChatAgentLoop(
            chat,
            new LLMNetHelper.Agent.Tools.ToolRegistry([new FixedTool("repo_search", "Найдено: 1 фрагмент")]),
            new ChatOpts(),
            NullLogger<ChatAgentLoop>.Instance,
            transcript);

        var answer = await loop.RunAsync([], "Где App?", CancellationToken.None);

        Assert.Equal("Итоговый ответ.", answer);
        Assert.Contains("user:Где App?", transcript.Events);
        // Без usage суффикс токенов — пустой ("/").
        Assert.Contains("model:0:{\"tool\":\"repo_search\",\"arguments\":{\"query\":\"App\"}}:/", transcript.Events);
        Assert.Contains("call:0:repo_search:{\"query\":\"App\"}", transcript.Events);
        // Полный текст результата инструмента (не число символов).
        Assert.Contains("result:0:repo_search:ok:Найдено: 1 фрагмент", transcript.Events);
        Assert.Contains("answer:Итоговый ответ.:/", transcript.Events);
    }

    [Fact]
    public async Task AgentLoop_UsageTokens_PropagatedToTranscript()
    {
        var chat = new ScriptedChatClient(
            """{"tool":"repo_search","arguments":{"query":"App"}}""",
            "Итоговый ответ.")
        {
            Usage = new UsageDetails { InputTokenCount = 120, OutputTokenCount = 30 }
        };
        var transcript = new RecordingTranscript();
        var loop = new ChatAgentLoop(
            chat,
            new LLMNetHelper.Agent.Tools.ToolRegistry([new FixedTool("repo_search", "ок")]),
            new ChatOpts(),
            NullLogger<ChatAgentLoop>.Instance,
            transcript);

        await loop.RunAsync([], "Где App?", CancellationToken.None);

        // Шаг с вызовом инструмента и финальный ответ несут usage своего запроса.
        Assert.Contains("model:0:{\"tool\":\"repo_search\",\"arguments\":{\"query\":\"App\"}}:120/30", transcript.Events);
        Assert.Contains("answer:Итоговый ответ.:120/30", transcript.Events);
    }

    [Fact]
    public async Task AgentLoop_NoUsageTokens_TranscriptGetsNulls()
    {
        var chat = new ScriptedChatClient("Финал.");
        var transcript = new RecordingTranscript();
        var loop = new ChatAgentLoop(
            chat,
            new LLMNetHelper.Agent.Tools.ToolRegistry([]),
            new ChatOpts(),
            NullLogger<ChatAgentLoop>.Instance,
            transcript);

        await loop.RunAsync([], "вопрос", CancellationToken.None);

        // Usage нет -> суффикс "/": оба токена null.
        Assert.Contains("answer:Финал.:/", transcript.Events);
    }

    [Fact]
    public async Task AgentLoop_StepLimit_NotesInTranscript()
    {
        var toolCall = """{"tool":"repo_search","arguments":{"query":"x"}}""";
        var chat = new ScriptedChatClient(Enumerable.Repeat(toolCall, 10).ToArray());
        var transcript = new RecordingTranscript();
        var loop = new ChatAgentLoop(
            chat,
            new LLMNetHelper.Agent.Tools.ToolRegistry([new FixedTool("repo_search", "ok")]),
            new ChatOpts { MaxSteps = 2 },
            NullLogger<ChatAgentLoop>.Instance,
            transcript);

        var answer = await loop.RunAsync([], "вопрос", CancellationToken.None);

        Assert.Contains("лимит шагов", answer, StringComparison.Ordinal);
        Assert.Contains(transcript.Events, e => e.StartsWith("note:", StringComparison.Ordinal) && e.Contains("лимит шагов", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentLoop_LlmFailure_NotesInTranscript()
    {
        var chat = new ScriptedChatClient(); // пустая очередь -> "" -> пустой ответ
        var transcript = new RecordingTranscript();
        var loop = new ChatAgentLoop(
            chat,
            new LLMNetHelper.Agent.Tools.ToolRegistry([]),
            new ChatOpts(),
            NullLogger<ChatAgentLoop>.Instance,
            transcript);

        var answer = await loop.RunAsync([], "вопрос", CancellationToken.None);

        Assert.Contains("пустой ответ", answer, StringComparison.Ordinal);
        Assert.Contains(transcript.Events, e => e.StartsWith("note:", StringComparison.Ordinal) && e.Contains("пустой ответ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentLoop_WithoutTranscript_StillWorks()
    {
        // Обратная совместимость: старый 4-аргументный конструктор (тесты 18-го плана).
        var chat = new ScriptedChatClient("Ответ.");
        var loop = new ChatAgentLoop(
            chat,
            new LLMNetHelper.Agent.Tools.ToolRegistry([]),
            new ChatOpts(),
            NullLogger<ChatAgentLoop>.Instance);

        var answer = await loop.RunAsync([], "вопрос", CancellationToken.None);

        Assert.Equal("Ответ.", answer);
    }

    // ---------- Интеграция: ChatSession ----------

    [Fact]
    public async Task ChatSession_ModeA_TranscriptCapturesQuestionContextAnswer()
    {
        var repoRoot = Path.Combine(_root, "repos");
        Directory.CreateDirectory(Path.Combine(repoRoot, "demo"));
        File.WriteAllText(
            Path.Combine(repoRoot, "demo", "README.md"),
            "# demo\n\nУникальный маркер: ZEBRA-42. Класс Writer пишет отчёты.");

        var chat = new ScriptedChatClient("Ответ модели про ZEBRA-42.");
        var options = Options();
        var scope = new ChatScope(new GitLabOptions { CloneRoot = repoRoot }, options);
        var input = new StringReader("Что делает Writer?\n/exit\n");
        var output = new StringWriter();

        var session = new ChatSession(
            chat,
            scope,
            options,
            new LLMCore.Abstractions.LlmOptions { Model = "test-model", Endpoint = "http://localhost" },
            NullLoggerFactory.Instance,
            input,
            output);

        var code = await session.RunAsync(ask: null, repo: null, tools: false, CancellationToken.None);

        Assert.Equal(0, code);

        var file = Assert.Single(Directory.GetFiles(_root, "chat-*.md"));
        var text = await File.ReadAllTextAsync(file, System.Text.Encoding.UTF8);

        Assert.Contains("## Вопрос 1 — ", text, StringComparison.Ordinal);
        Assert.Contains("> Что делает Writer?", text, StringComparison.Ordinal);
        Assert.Contains("### Контекст (BM25):", text, StringComparison.Ordinal);
        Assert.Contains("demo/README.md", text, StringComparison.Ordinal);
        Assert.Contains("### Финальный ответ — ", text, StringComparison.Ordinal);
        Assert.Contains("Ответ модели про ZEBRA-42.", text, StringComparison.Ordinal);
        Assert.Contains("### Команда — ", text, StringComparison.Ordinal);
        Assert.Contains("/exit", text, StringComparison.Ordinal);
        Assert.Contains("## Завершено:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatSession_AskMode_TranscriptCreatedAndClosed()
    {
        var repoRoot = Path.Combine(_root, "repos");
        Directory.CreateDirectory(Path.Combine(repoRoot, "demo"));
        File.WriteAllText(Path.Combine(repoRoot, "demo", "README.md"), "# demo");

        var chat = new ScriptedChatClient("Ответ.");
        var options = Options();
        var scope = new ChatScope(new GitLabOptions { CloneRoot = repoRoot }, options);

        var session = new ChatSession(
            chat,
            scope,
            options,
            new LLMCore.Abstractions.LlmOptions { Model = "test-model", Endpoint = "http://localhost" },
            NullLoggerFactory.Instance,
            new StringReader(""),
            new StringWriter());

        var code = await session.RunAsync(ask: "Один вопрос", repo: null, tools: false, CancellationToken.None);

        Assert.Equal(0, code);
        var file = Assert.Single(Directory.GetFiles(_root, "chat-*.md"));
        var text = await File.ReadAllTextAsync(file, System.Text.Encoding.UTF8);
        Assert.Contains("> Один вопрос", text, StringComparison.Ordinal);
        Assert.Contains("Ответ.", text, StringComparison.Ordinal);
        // Файл закрыт (футер записан) — сеанс завершён.
        Assert.Contains("## Завершено:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatSession_DisabledDialogLog_NoFile()
    {
        var repoRoot = Path.Combine(_root, "repos");
        Directory.CreateDirectory(repoRoot);

        var chat = new ScriptedChatClient("Ответ.");
        var options = Options(enabled: false);
        var scope = new ChatScope(new GitLabOptions { CloneRoot = repoRoot }, options);

        var session = new ChatSession(
            chat,
            scope,
            options,
            new LLMCore.Abstractions.LlmOptions { Model = "test-model", Endpoint = "http://localhost" },
            NullLoggerFactory.Instance,
            new StringReader(""),
            new StringWriter());

        var code = await session.RunAsync(ask: "вопрос", repo: null, tools: false, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Empty(Directory.GetFiles(_root, "chat-*.md"));
    }
}
