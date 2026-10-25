using System.Text;
using LLMNetHelper.Abstractions;
using Microsoft.Extensions.Logging;

namespace LLMNetHelper.Chat;

/// <summary>
/// Приёмник событий диалога чата (<c>--chat</c>).
/// <para>
/// Реализация <see cref="ChatDialogLogger"/> пишет Markdown-транскрипт всего
/// сеанса в один файл (<c>logs/dialogs/chat-{UTC}.md</c>);
/// <see cref="NullChatTranscript"/> — no-op для случая
/// <c>Chat:DialogLog:Enabled=false</c> и для тестов. Код чата пишет в
/// транскрипт безусловно, не проверяя <c>null</c>.
/// </para>
/// </summary>
public interface IChatTranscript
{
    /// <summary>Вопрос пользователя (как введено).</summary>
    void LogUserMessage(string text);

    /// <summary>Служебная команда (<c>/repo</c>, <c>/tools</c>, <c>/clear</c>, …).</summary>
    void LogCommand(string commandLine);

    /// <summary>
    /// Сырой ответ модели, распознанный как запрос инструмента.
    /// <paramref name="inputTokens"/>/<paramref name="outputTokens"/> — usage из
    /// ответа транспорта (может отсутствовать: <c>null</c>).
    /// </summary>
    void LogModelStep(int step, string rawResponseText, int? inputTokens = null, int? outputTokens = null);

    /// <summary>Вызов инструмента: имя + аргументы (JSON).</summary>
    void LogToolCall(int step, string toolName, string argumentsJson);

    /// <summary>Полный текст результата инструмента.</summary>
    void LogToolResult(int step, string toolName, string resultText, bool succeeded);

    /// <summary>Финальный ответ ассистента (текст пользователю).</summary>
    void LogAssistantAnswer(string text, int? inputTokens = null, int? outputTokens = null);

    /// <summary>Заметка: лимиты, сбои, смена области/режима, прочее.</summary>
    void LogNote(string text);

    /// <summary>Режим A (BM25): найденные фрагменты контекста (заголовки и оценки).</summary>
    void LogContextDocuments(IReadOnlyList<ContextDocument> documents);
}

/// <summary>
/// No-op транскрипт: используется когда <c>Chat:DialogLog:Enabled=false</c>
/// и в тестах без проверки файла.
/// </summary>
public sealed class NullChatTranscript : IChatTranscript
{
    /// <summary>Едиственный экземпляр (состояния нет).</summary>
    public static NullChatTranscript Instance { get; } = new();

    private NullChatTranscript()
    {
    }

    public void LogUserMessage(string text) { }
    public void LogCommand(string commandLine) { }
    public void LogModelStep(int step, string rawResponseText, int? inputTokens = null, int? outputTokens = null) { }
    public void LogToolCall(int step, string toolName, string argumentsJson) { }
    public void LogToolResult(int step, string toolName, string resultText, bool succeeded) { }
    public void LogAssistantAnswer(string text, int? inputTokens = null, int? outputTokens = null) { }
    public void LogNote(string text) { }
    public void LogContextDocuments(IReadOnlyList<ContextDocument> documents) { }
}

/// <summary>
/// Markdown-транскрипт сеанса чата: один файл от запуска до <c>/exit</c>/EOF.
/// <para>
/// Фиксирует вопросы пользователя, команды, каждый шаг ReAct-цикла
/// (сырой ответ модели → вызов инструмента → полный результат), финальные
/// ответы, лимиты и сбои. Всё время — <b>UTC</b> (правило проекта).
/// Кодировка файла — UTF-8, flush после каждой записи (файл читаем в процессе
/// сеанса). Любая <see cref="IOException"/> гасится: сбой записи транскрипта
/// не должен рвать чат.
/// </para>
/// </summary>
public sealed class ChatDialogLogger : IChatTranscript, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly ILogger? _logger;
    private readonly object _sync = new();
    private bool _disposed;
    private bool _ioFailed;

    private int _questionNo;
    private int _toolCalls;
    private bool _inQuestion;
    private readonly bool _includeContextFragments;
    private long _inputTokens;
    private long _outputTokens;
    private bool _hasUsage;

    /// <summary>Полный путь созданного файла транскрипта.</summary>
    public string FilePath { get; }

    /// <summary>Время старта сеанса (UTC).</summary>
    public DateTimeOffset StartedUtc { get; }

    private ChatDialogLogger(
        string filePath,
        StreamWriter writer,
        bool includeContextFragments,
        ILogger? logger)
    {
        FilePath = filePath;
        _writer = writer;
        _includeContextFragments = includeContextFragments;
        _logger = logger;
        StartedUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Создаёт логгер сеанса, если <c>options.DialogLog.Enabled</c>;
    /// иначе возвращает <see cref="NullChatTranscript.Instance"/> (нулевой оверхед).
    /// </summary>
    /// <param name="options">Параметры чата (секция <c>Chat</c>).</param>
    /// <param name="headerInfo">
    /// Строки шапки: модель, область, режим на старте (пары «ключ — значение»).
    /// </param>
    /// <param name="logger">Логгер предупреждений IO (опционально).</param>
    public static IChatTranscript TryCreate(
        ChatOptions options,
        IReadOnlyList<(string Key, string Value)> headerInfo,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var dialog = options.DialogLog;
        if (!dialog.Enabled)
        {
            return NullChatTranscript.Instance;
        }

        try
        {
            // План 22, этап 4.7: генерация пути и UTF-8-писателя — в отдельном
            // компоненте DialogFileProvider.
            var writer = DialogFileProvider.CreateWriter(dialog.Directory, out var filePath);

            var instance = new ChatDialogLogger(filePath, writer, dialog.IncludeContextFragments, logger);
            instance.WriteHeader(headerInfo);
            return instance;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Транскрипт — не критичный путь: чат продолжается без него.
            logger?.LogWarning(ex, "Чат: не удалось создать файл транскрипта в {Dir} — логирование диалога пропущено", dialog.Directory);
            return NullChatTranscript.Instance;
        }
    }

    /// <summary>
    /// Имя файла <c>chat-yyyy-MM-dd_HH-mm-ssZ.md</c> (UTC старта сеанса);
    /// при коллизии — суффиксы <c>-2</c>, <c>-3</c>, … (обёртка над
    /// <see cref="DialogFileProvider.UniqueFilePath"/> для совместимости).
    /// </summary>
    internal static string UniqueFilePath(string directory, DateTimeOffset utcNow) =>
        DialogFileProvider.UniqueFilePath(directory, utcNow);

    /// <inheritdoc />
    public void LogUserMessage(string text)
    {
        lock (_sync)
        {
            StartSection();
            WriteLine("### Пользователь");
            WriteLine();
            WriteQuote(text);
            WriteLine();
        }
    }

    /// <inheritdoc />
    public void LogCommand(string commandLine)
    {
        lock (_sync)
        {
            // Команда всегда начинает новую секцию: это самостоятельное
            // действие пользователя между вопросами.
            StartSection();
            WriteLine($"### Команда — {Stamp()}");
            WriteLine();
            WriteQuote(commandLine);
            WriteLine();
        }
    }

    /// <inheritdoc />
    public void LogModelStep(int step, string rawResponseText, int? inputTokens = null, int? outputTokens = null)
    {
        lock (_sync)
        {
            OpenQuestionIfNeeded();
            WriteLine($"### Шаг {step + 1} — ответ модели (запрос инструмента) — {Stamp()}{UsageSuffix(inputTokens, outputTokens)}");
            AccumulateUsage(inputTokens, outputTokens);
            WriteLine();
            WriteFenced(rawResponseText, "json");
            WriteLine();
        }
    }

    /// <inheritdoc />
    public void LogToolCall(int step, string toolName, string argumentsJson)
    {
        lock (_sync)
        {
            _toolCalls++;
            WriteLine($"### Шаг {step + 1} — вызов `{toolName}` — {Stamp()}");
            WriteLine();
            WriteFenced(argumentsJson, "json");
            WriteLine();
        }
    }

    /// <inheritdoc />
    public void LogToolResult(int step, string toolName, string resultText, bool succeeded)
    {
        lock (_sync)
        {
            var mark = succeeded ? "успех" : "ошибка";
            WriteLine($"### Шаг {step + 1} — результат `{toolName}`: {mark}, {resultText.Length} симв. — {Stamp()}");
            WriteLine();
            WriteFenced(resultText, "text");
            WriteLine();
        }
    }

    /// <inheritdoc />
    public void LogAssistantAnswer(string text, int? inputTokens = null, int? outputTokens = null)
    {
        lock (_sync)
        {
            OpenQuestionIfNeeded();
            WriteLine($"### Финальный ответ — {Stamp()}{UsageSuffix(inputTokens, outputTokens)}");
            AccumulateUsage(inputTokens, outputTokens);
            WriteLine();
            WriteLine(text);
            WriteLine();
            WriteLine("---");
            WriteLine();
            // Секция НЕ закрывается: сопутствующие заметки (лимиты, сбои),
            // идущие после ответа, остаются в том же вопросе.
        }
    }

    /// <inheritdoc />
    public void LogNote(string text)
    {
        lock (_sync)
        {
            OpenQuestionIfNeeded();
            WriteLine($"_{text}_");
            WriteLine();
        }
    }

    /// <inheritdoc />
    public void LogContextDocuments(IReadOnlyList<ContextDocument> documents)
    {
        lock (_sync)
        {
            OpenQuestionIfNeeded();
            // Суммарный объём отправленного модели контекста (символов):
            // виден сразу в заголовке секции, без подсчёта по фрагментам.
            var totalChars = 0;
            foreach (var doc in documents)
            {
                totalChars += doc.Length;
            }

            WriteLine($"### Контекст (BM25): {documents.Count} фрагментов, всего {totalChars} симв. — {Stamp()}");
            WriteLine();
            if (documents.Count == 0)
            {
                WriteLine("(ничего не найдено)");
            }
            else if (_includeContextFragments)
            {
                // Полный текст каждого фрагмента — ровно то, что видела модель
                // (Chat:DialogLog:IncludeContextFragments, по умолчанию включено).
                foreach (var doc in documents)
                {
                    WriteLine(
                        $"- `{doc.Title}` (score {doc.Score.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}, {doc.Length} симв.) — `{doc.Id}`");
                    WriteLine();
                    WriteFenced(doc.Content, "text");
                    WriteLine();
                }
            }
            else
            {
                foreach (var doc in documents)
                {
                    WriteLine($"- `{doc.Title}` (score {doc.Score.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}, {doc.Length} симв.) — `{doc.Id}`");
                }
            }

            WriteLine();
        }
    }

    /// <summary>Пишет футер (итоги сеанса) и закрывает файл. Повторный вызов — no-op.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                WriteLine("---");
                WriteLine();
                WriteLine($"## Завершено: {DateTimeOffset.UtcNow:HH:mm:ss} UTC");
                WriteLine();
                WriteLine($"Вопросов/команд: {_questionNo}, вызовов инструментов: {_toolCalls}.");
                if (_hasUsage)
                {
                    WriteLine(
                        $"Токены за сеанс: вход {_inputTokens}, выход {_outputTokens}, всего {_inputTokens + _outputTokens}.");
                }
            }
            catch (IOException ex)
            {
                _logger?.LogWarning(ex, "Чат: не удалось дописать футер транскрипта {Path}", FilePath);
            }

            try
            {
                _writer.Dispose();
            }
            catch (IOException ex)
            {
                _logger?.LogWarning(ex, "Чат: не удалось закрыть транскрипт {Path}", FilePath);
            }
        }
    }

    private void WriteHeader(IReadOnlyList<(string Key, string Value)> headerInfo)
    {
        WriteLine("# Транскрипт диалога LLMNetHelper Chat");
        WriteLine();
        WriteLine($"- Начало: {StartedUtc:yyyy-MM-dd HH:mm:ss} UTC");
        foreach (var (key, value) in headerInfo)
        {
            WriteLine($"- {key}: {value}");
        }

        WriteLine();
        WriteLine("---");
        WriteLine();
    }

    /// <summary>
    /// Начинает новую секцию «Вопрос N» (сквозная нумерация вопросов и команд).
    /// </summary>
    private void StartSection()
    {
        _questionNo++;
        _inQuestion = true;
        WriteLine($"## Вопрос {_questionNo} — {Stamp()}");
        WriteLine();
    }

    /// <summary>
    /// События внутри текущего вопроса (шаги модели, инструменты, контекст,
    /// заметки) дописываются в открытую секцию; вне вопроса — открывают её,
    /// чтобы структура файла оставалась однотипной.
    /// </summary>
    private void OpenQuestionIfNeeded()
    {
        if (_inQuestion)
        {
            return;
        }

        StartSection();
    }

    private string Stamp() => DateTime.UtcNow.ToString("HH:mm:ss") + " UTC";

    /// <summary>
    /// Суффикс заголовка секции с usage ответа модели; пустая строка, если
    /// транспорт токены не вернул.
    /// </summary>
    private static string UsageSuffix(int? inputTokens, int? outputTokens)
    {
        if (inputTokens is null && outputTokens is null)
        {
            return "";
        }

        return $" · токены: вход {inputTokens?.ToString() ?? "?"}, выход {outputTokens?.ToString() ?? "?"}";
    }

    private void AccumulateUsage(int? inputTokens, int? outputTokens)
    {
        if (inputTokens is null && outputTokens is null)
        {
            return;
        }

        _hasUsage = true;
        _inputTokens += inputTokens ?? 0;
        _outputTokens += outputTokens ?? 0;
    }

    private void WriteLine(string text = "")
    {
        if (_ioFailed)
        {
            return;
        }

        try
        {
            _writer.WriteLine(text);
        }
        catch (IOException ex)
        {
            _ioFailed = true;
            _logger?.LogWarning(ex, "Чат: сбой записи транскрипта {Path} — дальнейшие записи пропущены", FilePath);
        }
    }

    /// <summary>Многострочный текст как цитата Markdown (каждая строка через <c>> </c>).</summary>
    private void WriteQuote(string text)
    {
        foreach (var line in SplitLines(text))
        {
            WriteLine(line.Length == 0 ? ">" : "> " + line);
        }
    }

    /// <summary>
    /// Блок кода с оградой: если содержимое содержит <c>```</c>, используется
    /// ограда из четырёх обратных кавычек.
    /// </summary>
    private void WriteFenced(string text, string info)
    {
        var fence = text.Contains("```", StringComparison.Ordinal) ? "````" : "```";
        WriteLine(fence + info);
        foreach (var line in SplitLines(text))
        {
            WriteLine(line);
        }

        WriteLine(fence);
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
}
