using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Agent.Tools;
using LLMNetHelper.Chat.Tools;
using LLMNetHelper.Roslyn;
using LLMCore.Abstractions;

namespace LLMNetHelper.Chat;

/// <summary>
/// Консольный REPL-чат по скачанным репозиториям (<c>GitLab:CloneRoot</c>).
/// <para>
/// Режим A (по умолчанию): вопрос → BM25-поиск контекста через
/// <see cref="ChatScope"/> → один ответ <see cref="IChatClient"/>.
/// Режим B (<c>/tools</c>): агентский ReAct-цикл (<see cref="ChatAgentLoop"/>)
/// с инструментами <c>repo_search</c> / <c>repo_read_file</c> / <c>repo_list_files</c>.
/// После <c>/repo <имя></c> и при <c>Roslyn:Enabled=true</c> к ним добавляются
/// Roslyn-инструменты <c>repo_failure_points</c> / <c>repo_call_graph</c> /
/// <c>repo_symbol_search</c> (реестр перестраивается на каждый вопрос).
/// </para>
/// <para>
/// Неинтерактивный прогон: <c>--ask="…"</c> — один вопрос и выход.
/// Ввод/вывод параметризуются (<see cref="TextReader"/>/<see cref="TextWriter"/>) —
/// тесты не требуют консоли.
/// </para>
/// </summary>
public sealed class ChatSession
{
    private readonly IChatClient _chat;
    private readonly ChatScope _scope;
    private readonly ChatOptions _options;
    private readonly LlmOptions _llmOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TextReader _in;
    private readonly TextWriter _out;

    // Roslyn-слой (опционален: null => инструменты не регистрируются).
    // План 22, этап 2.8: одна абстракция IRoslynToolFactory вместо
    // 5 конкретных Roslyn-классов (SolutionLoader, CodeSearchService,
    // CallGraphService, FailurePointAnalyzer, RoslynOptions).
    private readonly IRoslynToolFactory? _roslynFactory;

    private readonly List<ChatMessage> _history = [];
    private bool _toolsMode;
    private IChatTranscript _transcript = NullChatTranscript.Instance;

    public ChatSession(
        IChatClient chat,
        ChatScope scope,
        ChatOptions options,
        LlmOptions llmOptions,
        ILoggerFactory loggerFactory,
        TextReader @in,
        TextWriter @out,
        IRoslynToolFactory? roslynFactory = null)
    {
        _chat = chat;
        _scope = scope;
        _options = options;
        _llmOptions = llmOptions;
        _loggerFactory = loggerFactory;
        _in = @in;
        _out = @out;
        _roslynFactory = roslynFactory;
    }

    /// <summary>
    /// Roslyn-инструменты доступны: слой включён, все зависимости переданы
    /// и текущая область — конкретный репозиторий с файлом решения.
    /// </summary>
    public bool RoslynAvailable => _roslynFactory is not null && _roslynFactory.IsAvailable(_scope);

    /// <summary>Текущий режим /tools (для тестов и диагностики).</summary>
    public bool ToolsMode => _toolsMode;

    /// <summary>
    /// Запускает сеанс: одиночный вопрос (<paramref name="ask"/>) или интерактивный REPL.
    /// </summary>
    /// <returns>0 — успех, 1 — сбой, 4 — репозиторий не найден.</returns>
    public async Task<int> RunAsync(string? ask, string? repo, bool tools, CancellationToken ct)
    {
        _toolsMode = tools;

        // Транскрипт сеанса: один Markdown-файл от старта до /exit/EOF
        // (Chat:DialogLog). Создаётся до первого действия, закрывается в finally.
        _transcript = ChatDialogLogger.TryCreate(
            _options,
            [("Модель", $"{_llmOptions.Model} @ {_llmOptions.Endpoint}"),
             ("Область", DescribeScope()),
             ("Режим на старте", _toolsMode ? "/tools (ReAct)" : "поиск контекста (BM25)")],
            _loggerFactory.CreateLogger<ChatDialogLogger>());

        try
        {
            if (!string.IsNullOrWhiteSpace(repo))
            {
                var ok = _scope.TrySetRepo(repo, out var msg);
                _out.WriteLine($"[chat] {msg}");
                if (!ok)
                {
                    _transcript.LogNote($"[команда] --repo={repo}: {msg}");
                    return 4;
                }

                _transcript.LogNote($"[область] {msg}");
            }

            if (!string.IsNullOrWhiteSpace(ask))
            {
                // Неинтерактивный режим: один вопрос и выход.
                var answer = await AnswerAsync(ask.Trim(), ct).ConfigureAwait(false);
                _out.WriteLine(answer);
                return IsFailure(answer) ? 1 : 0;
            }

            PrintBanner();

            while (!ct.IsCancellationRequested)
            {
                _out.Write("> ");
                _out.Flush();

                string? line;
                try
                {
                    line = _in.ReadLine();
                }
                catch (IOException)
                {
                    // Закрытый stdin (CI/перенаправления) — завершаем сеанс.
                    break;
                }

                if (line is null)
                {
                    // EOF (Ctrl+Z / закрытый поток) — выход.
                    _out.WriteLine();
                    break;
                }

                var command = ChatCommandParser.Parse(line);

                switch (command.Kind)
                {
                    case ChatCommandKind.Message:
                        if (string.IsNullOrWhiteSpace(command.Argument))
                        {
                            continue;
                        }

                        var answer = await AnswerAsync(command.Argument, ct).ConfigureAwait(false);
                        _out.WriteLine(answer);
                        _out.WriteLine();
                        break;

                    case ChatCommandKind.Help:
                        _transcript.LogCommand(line.Trim());
                        PrintHelp();
                        break;

                    case ChatCommandKind.Exit:
                        _transcript.LogCommand(line.Trim());
                        _out.WriteLine("[chat] До встречи.");
                        return 0;

                    case ChatCommandKind.Repos:
                        _transcript.LogCommand(line.Trim());
                        PrintRepos();
                        break;

                    case ChatCommandKind.Repo:
                        _scope.TrySetRepo(command.Argument, out var scopeMsg);
                        _out.WriteLine($"[chat] {scopeMsg}");
                        _transcript.LogCommand(line.Trim());
                        _transcript.LogNote($"[область] {scopeMsg}");
                        break;

                    case ChatCommandKind.Tools:
                        _toolsMode = !_toolsMode;
                        _out.WriteLine($"[chat] Режим инструментов: {(_toolsMode ? "включён (ReAct-цикл)" : "выключен (поиск контекста)")}.");
                        _transcript.LogCommand(line.Trim());
                        _transcript.LogNote($"[режим] {(_toolsMode ? "/tools (ReAct)" : "поиск контекста (BM25)")}");
                        break;

                    case ChatCommandKind.Clear:
                        _history.Clear();
                        _out.WriteLine("[chat] История очищена.");
                        _transcript.LogCommand(line.Trim());
                        _transcript.LogNote("[история] очищена");
                        break;

                    case ChatCommandKind.Status:
                        _transcript.LogCommand(line.Trim());
                        PrintStatus();
                        break;

                    case ChatCommandKind.Unknown:
                        _out.WriteLine($"[chat] Неизвестная команда: {command.Argument}. Введите /help.");
                        _transcript.LogCommand(line.Trim());
                        break;
                }
            }

            return 0;
        }
        finally
        {
            if (_transcript is IDisposable disposable)
            {
                disposable.Dispose();
            }

            _transcript = NullChatTranscript.Instance;
        }
    }

    /// <summary>
    /// Один вопрос: режим A (поиск контекста → ответ) либо режим B (ReAct-цикл).
    /// Возвращает текст ответа; пары вопрос/ответ добавляются в историю.
    /// </summary>
    public async Task<string> AnswerAsync(string question, CancellationToken ct)
    {
        try
        {
            if (_toolsMode)
            {
                var loop = new ChatAgentLoop(
                    _chat,
                    BuildToolRegistry(),
                    _options,
                    _loggerFactory.CreateLogger<ChatAgentLoop>(),
                    _transcript);

                var messages = NewConversation(withTools: true);
                var answer = await loop.RunAsync(messages, question, ct).ConfigureAwait(false);
                AppendHistory(question, answer);
                return answer;
            }

            // Режим A: BM25-поиск по области → промпт → один ответ.
            // Вопрос и контекст в транскрипт пишет здесь (в режиме B это делает цикл).
            _transcript.LogUserMessage(question);
            var search = await _scope.SearchAsync(question, ct).ConfigureAwait(false);
            _transcript.LogContextDocuments(search.Documents);

            var messagesA = NewConversation(withTools: false);
            messagesA.Add(new ChatMessage(ChatRole.User, ChatPromptBuilder.BuildUserPrompt(question, search.Documents)));

            var response = await _chat.GetResponseAsync(messagesA, cancellationToken: ct).ConfigureAwait(false);
            var text = response.Text ?? "[пустой ответ]";

            // Usage из ответа транспорта (может отсутствовать — null).
            int? inputTokens = null;
            int? outputTokens = null;
            if (response.Usage is { } usage)
            {
                inputTokens = usage.InputTokenCount is { } i ? (int)Math.Min(i, int.MaxValue) : null;
                outputTokens = usage.OutputTokenCount is { } o ? (int)Math.Min(o, int.MaxValue) : null;
            }

            _transcript.LogAssistantAnswer(text, inputTokens, outputTokens);
            AppendHistory(question, text);
            return text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<ChatSession>().LogError(ex, "Чат: сбой обработки вопроса");
            _transcript.LogNote($"[Сбой: {ex.Message}]");
            return $"[Сбой: {ex.Message}]";
        }
    }

    /// <summary>Диалог без истории: системный промпт + история (для ReAct-цикла).</summary>
    private List<ChatMessage> NewConversation(bool withTools)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, ChatPromptBuilder.BuildSystemPrompt(
                DescribeScope(),
                withTools,
                withTools ? BuildToolRegistry().DescribeForPrompt() : null))
        };
        messages.AddRange(_history);
        return messages;
    }

    /// <summary>
    /// Реестр инструментов: базовая тройка <c>repo_*</c> всегда; Roslyn-тройка —
    /// только при <c>Roslyn:Enabled</c>, выбранным <c>/repo</c> и найденном
    /// файле решения (plan 21, требование «только /repo»).
    /// </summary>
    private ToolRegistry BuildToolRegistry()
    {
        var tools = new List<IAgentTool>
        {
            new RepoSearchTool(_scope),
            new RepoReadFileTool(_scope, _options),
            new RepoListFilesTool(_scope, _options)
        };

        // План 22, этап 2.8: инструменты создаются фабрикой, null-forgiving убран.
        if (_roslynFactory is not null)
        {
            tools.AddRange(_roslynFactory.CreateTools(_scope));
        }

        return new ToolRegistry(tools);
    }

    private void AppendHistory(string question, string answer)
    {
        _history.Add(new ChatMessage(ChatRole.User, question));
        _history.Add(new ChatMessage(ChatRole.Assistant, answer));

        // Обрезка по HistoryMessages (считаем сообщения, не пары).
        var max = Math.Max(2, _options.HistoryMessages);
        if (_history.Count > max)
        {
            _history.RemoveRange(0, _history.Count - max);
        }
    }

    private static bool IsFailure(string answer) =>
        answer.StartsWith("[Сбой", StringComparison.Ordinal) ||
        answer.StartsWith("[Модель вернула пустой ответ]", StringComparison.Ordinal);

    private string DescribeScope()
    {
        return _scope.CurrentRepo is null
            ? $"все репозитории каталога {_scope.CloneRoot}"
            : $"репозиторий {_scope.CurrentRepo} ({_scope.RootPath})";
    }

    private void PrintBanner()
    {
        _out.WriteLine("=== LLMNetHelper Chat ===");
        _out.WriteLine($"Модель : {_llmOptions.Model} @ {_llmOptions.Endpoint}");
        _out.WriteLine($"Область: {DescribeScope()}");
        _out.WriteLine("Команды: /help — справка, /exit — выход.");
        _out.WriteLine();
    }

    private void PrintHelp()
    {
        _out.WriteLine("/help            — эта справка");
        _out.WriteLine("/exit | /quit    — выход");
        _out.WriteLine("/repos           — список репозиториев в CloneRoot");
        _out.WriteLine("/repo <имя>      — сузить поиск на один репозиторий (без имени — все)");
        _out.WriteLine("/tools           — переключить агентский режим (ReAct: repo_search/repo_read_file/repo_list_files)");
        _out.WriteLine("/clear           — очистить историю диалога");
        _out.WriteLine("/status          — текущая область, модель, режим");
    }

    private void PrintRepos()
    {
        var repos = _scope.ListRepos();
        if (repos.Count == 0)
        {
            _out.WriteLine($"[chat] В каталоге {_scope.CloneRoot} нет репозиториев. Сначала выполните: run_LLMNetHelper.ps1 -CloneAll");
            return;
        }

        _out.WriteLine($"[chat] Репозитории ({repos.Count}) в {_scope.CloneRoot}:");
        foreach (var r in repos)
        {
            var marker = string.Equals(r, _scope.CurrentRepo, StringComparison.OrdinalIgnoreCase) ? "* " : "  ";
            _out.WriteLine($"{marker}{r}");
        }
    }

    private void PrintStatus()
    {
        _out.WriteLine($"[chat] Область : {DescribeScope()}");
        _out.WriteLine($"[chat] Модель  : {_llmOptions.Model}");
        _out.WriteLine($"[chat] Режим   : {(_toolsMode ? "/tools (ReAct)" : "поиск контекста (BM25)")}");
        _out.WriteLine($"[chat] История: {_history.Count} сообщений (лимит {_options.HistoryMessages})");
        _out.WriteLine($"[chat] Roslyn  : {DescribeRoslyn()}");
    }

    private string DescribeRoslyn() =>
        // План 22, этап 2.8: статус отдаёт фабрика (вся логика Roslyn-слоя там).
        _roslynFactory is null
            ? "выключен (слой не зарегистрирован)"
            : _roslynFactory.DescribeStatus(_scope);
}
