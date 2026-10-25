using System.Text;
using LLMNetHelper.Abstractions;

namespace LLMNetHelper.Chat;

/// <summary>
/// Сборка промптов чата (план 22, этап 6.1: экземплярный класс, реализующий
/// <see cref="IChatPromptBuilder"/> — подменяемость в тестах, версионирование).
/// В отличие от ночного агента (<c>AgentPromptBuilder</c>),
/// чат отвечает в свободной форме (Markdown), а не по JSON-схеме диагноза.
/// </summary>
public sealed class ChatPromptBuilder : IChatPromptBuilder
{
    /// <summary>
    /// Системный промпт: роль ассистента по коду репозиториев группы,
    /// ограничения (только предоставленные фрагменты, цитирование путей).
    /// </summary>
    /// <param name="scopeDescription">Описание текущей области (путь, репозиторий).</param>
    /// <param name="withTools">true — включён режим /tools (модель может вызывать инструменты).</param>
    /// <param name="toolDescriptions">Описания инструментов (для режима /tools).</param>
    public string BuildSystemPrompt(
        string scopeDescription,
        bool withTools,
        string? toolDescriptions = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Ты — ассистент по коду и документации группы репозиториев.");
        sb.AppendLine($"Область поиска: {scopeDescription}");
        sb.AppendLine();
        sb.AppendLine("## Правила");
        sb.AppendLine("- Отвечай ТОЛЬКО на основе предоставленных фрагментов файлов. Не додумывай содержимое, которого не видел.");
        sb.AppendLine("- Ссылайся на источник в формате `путь/к/файлу:строка` (если строка известна).");
        sb.AppendLine("- Если фрагментов недостаточно для ответа — так и скажи и предложи уточнить вопрос или сузить область (/repo).");
        sb.AppendLine("- Отвечай на русском языке, кратко и по делу, Markdown допустим.");
        sb.AppendLine();

        if (withTools)
        {
            sb.AppendLine("## Инструменты (режим /tools)");
            sb.AppendLine(toolDescriptions);
            sb.AppendLine();
            sb.AppendLine("Чтобы вызвать инструмент, верни ТОЛЬКО JSON без markdown-обёртки:");
            sb.AppendLine("{\"tool\": \"<имя>\", \"arguments\": { ... }}");
            sb.AppendLine("Когда данных достаточно — верни финальный ответ обычным текстом (без JSON).");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Пользовательский промпт: вопрос + найденные фрагменты контекста.
    /// </summary>
    public string BuildUserPrompt(string question, IReadOnlyList<ContextDocument> documents)
    {
        var sb = new StringBuilder();

        if (documents.Count > 0)
        {
            sb.AppendLine("## Фрагменты из репозиториев");
            sb.AppendLine();
            foreach (var doc in documents)
            {
                sb.AppendLine($"### {doc.Title} (score {doc.Score:F2})");
                sb.AppendLine(doc.Content);
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("## Фрагменты из репозиториев");
            sb.AppendLine("(ничего не найдено по этому вопросу)");
            sb.AppendLine();
        }

        sb.AppendLine("## Вопрос");
        sb.AppendLine(question);

        return sb.ToString();
    }
}
