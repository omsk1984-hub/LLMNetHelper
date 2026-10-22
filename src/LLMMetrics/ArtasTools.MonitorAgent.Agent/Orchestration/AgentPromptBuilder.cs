using System.Text;
using ArtasTools.MonitorAgent.Abstractions;

namespace ArtasTools.MonitorAgent.Agent.Orchestration;

/// <summary>
/// Сборка промптов агента.
/// Промпты детерминированы и версионируются вместе с кодом — это требование
/// воспроизводимости ночного прогона.
/// </summary>
public static class AgentPromptBuilder
{
    /// <summary>
    /// Системный промпт: роль, правила проекта, формат ответа.
    /// </summary>
    public static string BuildSystemPrompt(string toolDescriptions)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Ты — инженер по надёжности (SRE), анализирующий ночной отчёт о состоянии системы Artas2026.");
        sb.AppendLine("Система собирает данные с финансовых рынков (поток тиков Finam gRPC → PostgreSQL),");
        sb.AppendLine("считает статистику фракталов и пишет структурированные логи в Loki.");
        sb.AppendLine();
        sb.AppendLine("## Твоя задача");
        sb.AppendLine("1. Определить, есть ли реальная проблема, и насколько она критична.");
        sb.AppendLine("2. Сформулировать гипотезы о корневой причине, опираясь ТОЛЬКО на факты из контекста.");
        sb.AppendLine("3. Если причина в коде — найти соответствующий код и предложить точечную правку.");
        sb.AppendLine();
        sb.AppendLine("## Доступные инструменты");
        sb.AppendLine(toolDescriptions);
        sb.AppendLine();
        sb.AppendLine("Вызывай инструменты только по имени из списка выше. Других инструментов нет.");
        sb.AppendLine();
        sb.AppendLine("## Правила проекта (обязательны)");
        sb.AppendLine("- Вся логика по UTC. МСК (UTC+3) — только для отображения. Не предлагай менять это.");
        sb.AppendLine("- Внедрение зависимостей через Microsoft.Extensions.DependencyInjection.");
        sb.AppendLine("- Логирование структурированное, через ILogger.");
        sb.AppendLine("- Тесты — xUnit.");
        sb.AppendLine("- Не предлагай правок, меняющих бизнес-логику расчёта фракталов без явных доказательств ошибки.");
        sb.AppendLine();
        sb.AppendLine("## Ограничения правок");
        sb.AppendLine("Разрешены только две операции:");
        sb.AppendLine("- ReplaceMethodBody — заменить тело метода (сигнатуру менять нельзя);");
        sb.AppendLine("- InsertNullCheck — добавить проверку в начало тела метода.");
        sb.AppendLine("Не предлагай новые файлы, переименования и изменения публичных сигнатур.");
        sb.AppendLine();
        sb.AppendLine("## Формат ответа");
        sb.AppendLine("Когда фактов достаточно, верни ТОЛЬКО JSON без markdown-обёртки:");
        sb.AppendLine("{");
        sb.AppendLine("  \"verdict\": \"OK | WARNING | CRITICAL\",");
        sb.AppendLine("  \"summary\": \"1-2 предложения\",");
        sb.AppendLine("  \"findings\": [{ \"id\", \"severity\", \"title\", \"evidence\", \"impact\", \"recommendation\", \"confidence\" }],");
        sb.AppendLine("  \"hypotheses\": [{ \"root_cause\", \"confidence\", \"evidence\", \"references\" }],");
        sb.AppendLine("  \"questions_for_owner\": [\"...\"]");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Вердикт CRITICAL ставь только при наличии находки severity=critical.");

        return sb.ToString();
    }

    /// <summary>
    /// Пользовательский промпт: контекст прогона.
    /// </summary>
    public static string BuildUserPrompt(AgentContext context)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# Прогон #{context.RunId}");
        sb.AppendLine($"Окно анализа: {context.WindowFrom:yyyy-MM-dd HH:mm} — {context.WindowTo:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();

        sb.AppendLine("## Сводка фактов");
        sb.AppendLine(context.SnapshotText);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(context.CodeStructureSummary))
        {
            sb.AppendLine("## Структура кода");
            sb.AppendLine(context.CodeStructureSummary);
            sb.AppendLine();
        }

        if (context.ContextDocuments.Count > 0)
        {
            sb.AppendLine("## Документация проекта");
            foreach (var doc in context.ContextDocuments)
            {
                sb.AppendLine($"### {doc.Title}");
                sb.AppendLine(doc.Content);
                sb.AppendLine();
            }
        }

        sb.AppendLine("Исследуй проблему при необходимости и верни JSON по схеме.");

        return sb.ToString();
    }

    /// <summary>
    /// Промпт для этапа планирования правки.
    /// </summary>
    public static string BuildPlanPrompt(Diagnosis diagnosis, AgentContext context)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Диагноз");
        sb.AppendLine($"Вердикт: {diagnosis.Verdict}");
        sb.AppendLine($"Сводка: {diagnosis.Summary}");
        sb.AppendLine();

        if (diagnosis.Hypotheses.Count > 0)
        {
            sb.AppendLine("## Гипотезы");
            foreach (var h in diagnosis.Hypotheses)
            {
                sb.AppendLine($"- [{h.Confidence}] {h.RootCause}");
                sb.AppendLine($"  Доказательства: {h.Evidence}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Задача");
        sb.AppendLine("Предложи минимальную правку, устраняющую корневую причину.");
        sb.AppendLine("Верни JSON:");
        sb.AppendLine("{");
        sb.AppendLine("  \"rationale\": \"почему эта правка\",");
        sb.AppendLine("  \"changes\": [{ \"file_path\", \"symbol_name\", \"change_kind\", \"new_code\", \"explanation\" }],");
        sb.AppendLine("  \"risks\": [\"...\"],");
        sb.AppendLine("  \"requires_manual_review\": true");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Если правка не нужна или недостаточно данных — верни пустой массив changes.");

        return sb.ToString();
    }
}
