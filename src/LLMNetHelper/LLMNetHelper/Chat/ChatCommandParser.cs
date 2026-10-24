namespace LLMNetHelper.Chat;

/// <summary>Вид команды чата.</summary>
public enum ChatCommandKind
{
    /// <summary>Обычное сообщение (вопрос) — не начинается с <c>/</c>.</summary>
    Message,

    /// <summary><c>/help</c> — справка по командам.</summary>
    Help,

    /// <summary><c>/exit</c> или <c>/quit</c> — выход.</summary>
    Exit,

    /// <summary><c>/repos</c> — список репозиториев в CloneRoot.</summary>
    Repos,

    /// <summary><c>/repo <имя></c> — сузить область; без аргумента — все.</summary>
    Repo,

    /// <summary><c>/tools</c> — переключить агентский ReAct-цикл.</summary>
    Tools,

    /// <summary><c>/clear</c> — очистить историю диалога.</summary>
    Clear,

    /// <summary><c>/status</c> — текущая область, модель, режим.</summary>
    Status,

    /// <summary>Неизвестная команда (начинается с <c>/</c>, но не распознана).</summary>
    Unknown
}

/// <summary>
/// Разобранная команда чата: вид + аргумент (текст после первого пробела).
/// </summary>
/// <param name="Kind">Вид команды.</param>
/// <param name="Argument">Аргумент команды или текст сообщения (для <see cref="ChatCommandKind.Message"/>).</param>
public sealed record ChatCommand(ChatCommandKind Kind, string Argument);

/// <summary>
/// Разбор ввода пользователя на команды REPL. Чистая функция — тестируется без консоли.
/// </summary>
public static class ChatCommandParser
{
    /// <summary>
    /// Разбирает строку ввода. Пустая строка — <see cref="ChatCommandKind.Message"/>
    /// с пустым аргументом (REPL её пропускает).
    /// </summary>
    public static ChatCommand Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new ChatCommand(ChatCommandKind.Message, string.Empty);
        }

        var trimmed = input.Trim();
        if (trimmed[0] != '/')
        {
            return new ChatCommand(ChatCommandKind.Message, trimmed);
        }

        var space = trimmed.IndexOf(' ');
        var name = (space < 0 ? trimmed[1..] : trimmed[1..space]).Trim().ToLowerInvariant();
        var argument = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();

        var kind = name switch
        {
            "help" or "?" => ChatCommandKind.Help,
            "exit" or "quit" => ChatCommandKind.Exit,
            "repos" => ChatCommandKind.Repos,
            "repo" => ChatCommandKind.Repo,
            "tools" => ChatCommandKind.Tools,
            "clear" => ChatCommandKind.Clear,
            "status" => ChatCommandKind.Status,
            _ => ChatCommandKind.Unknown
        };

        // Для неизвестной команды аргументом считается вся строка ввода —
        // REPL покажет её пользователю в сообщении об ошибке.
        return new ChatCommand(kind, kind == ChatCommandKind.Unknown ? trimmed : argument);
    }
}
