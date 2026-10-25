namespace LLMNetHelper.Chat;

/// <summary>
/// Результат обработки одной команды чата.
/// </summary>
/// <param name="Exit">Сеанс завершён (нужно выйти из REPL).</param>
/// <param name="ExitCode">Код возврата при выходе.</param>
public readonly record struct ChatCommandResult(bool Exit, int ExitCode)
{
    public static ChatCommandResult Continue => new(false, 0);

    public static ChatCommandResult ExitWith(int code) => new(true, code);
}

/// <summary>
/// Контекст, необходимый обработчикам команд чата (план 22, этап 4.2).
/// <para>
/// Инкапсулирует изменяемое состояние сеанса, чтобы диспетчер команд не знал
/// о полях <see cref="ChatSession"/>. Порядок вызовов транскрипта сохранён
/// (он проверяется тестами <c>ChatDialogLoggerTests</c>).
/// </para>
/// </summary>
public interface IChatCommandContext
{
    /// <summary>Область поиска (для /repo, /repos).</summary>
    ChatScope Scope { get; }

    /// <summary>Поток вывода.</summary>
    TextWriter Out { get; }

    /// <summary>Транскрипт сеанса.</summary>
    IChatTranscript Transcript { get; }

    /// <summary>Текущий режим /tools (переключается командой /tools).</summary>
    bool ToolsMode { get; set; }

    /// <summary>Очистить историю диалога.</summary>
    void ClearHistory();

    /// <summary>Справка по командам.</summary>
    void PrintHelp();

    /// <summary>Список репозиториев.</summary>
    void PrintRepos();

    /// <summary>Текущий статус (область/модель/режим/история/Roslyn).</summary>
    void PrintStatus();

    /// <summary>Ответ на свободный вопрос (режимы A/B).</summary>
    Task<string> AnswerAsync(string question, CancellationToken ct);
}

/// <summary>
/// Диспетчеризация команд чата (план 22, этап 4.2): вынесена из
/// <see cref="ChatSession.RunAsync"/> — класс отвечает за сеанс, а не за разбор
/// каждой команды.
/// </summary>
public static class ChatCommandDispatcher
{
    /// <summary>
    /// Выполняет команду. Порядок записи в транскрипт соответствует прежнему
    /// коду (см. тесты <c>ChatDialogLoggerTests</c>).
    /// </summary>
    public static async Task<ChatCommandResult> DispatchAsync(
        ChatCommand command,
        string rawLine,
        IChatCommandContext context,
        CancellationToken ct)
    {
        switch (command.Kind)
        {
            case ChatCommandKind.Message:
                if (string.IsNullOrWhiteSpace(command.Argument))
                {
                    return ChatCommandResult.Continue;
                }

                var answer = await context.AnswerAsync(command.Argument, ct).ConfigureAwait(false);
                context.Out.WriteLine(answer);
                context.Out.WriteLine();
                return ChatCommandResult.Continue;

            case ChatCommandKind.Help:
                context.Transcript.LogCommand(rawLine.Trim());
                context.PrintHelp();
                return ChatCommandResult.Continue;

            case ChatCommandKind.Exit:
                context.Transcript.LogCommand(rawLine.Trim());
                context.Out.WriteLine("[chat] До встречи.");
                return ChatCommandResult.ExitWith(0);

            case ChatCommandKind.Repos:
                context.Transcript.LogCommand(rawLine.Trim());
                context.PrintRepos();
                return ChatCommandResult.Continue;

            case ChatCommandKind.Repo:
                context.Scope.TrySetRepo(command.Argument, out var scopeMsg);
                context.Out.WriteLine($"[chat] {scopeMsg}");
                context.Transcript.LogCommand(rawLine.Trim());
                context.Transcript.LogNote($"[область] {scopeMsg}");
                return ChatCommandResult.Continue;

            case ChatCommandKind.Tools:
                context.ToolsMode = !context.ToolsMode;
                context.Out.WriteLine($"[chat] Режим инструментов: {(context.ToolsMode ? "включён (ReAct-цикл)" : "выключен (поиск контекста)")}.");
                context.Transcript.LogCommand(rawLine.Trim());
                context.Transcript.LogNote($"[режим] {(context.ToolsMode ? "/tools (ReAct)" : "поиск контекста (BM25)")}");
                return ChatCommandResult.Continue;

            case ChatCommandKind.Clear:
                context.ClearHistory();
                context.Out.WriteLine("[chat] История очищена.");
                context.Transcript.LogCommand(rawLine.Trim());
                context.Transcript.LogNote("[история] очищена");
                return ChatCommandResult.Continue;

            case ChatCommandKind.Status:
                context.Transcript.LogCommand(rawLine.Trim());
                context.PrintStatus();
                return ChatCommandResult.Continue;

            case ChatCommandKind.Unknown:
            default:
                context.Out.WriteLine($"[chat] Неизвестная команда: {command.Argument}. Введите /help.");
                context.Transcript.LogCommand(rawLine.Trim());
                return ChatCommandResult.Continue;
        }
    }
}