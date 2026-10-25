namespace LLMNetHelper.Abstractions;

/// <summary>
/// Контракт сборки промптов ночного агента (план 22, этап 6.1). Извлечён из
/// статического <c>LLMNetHelper.Agent.Orchestration.AgentPromptBuilder</c>,
/// чтобы оркестратор зависел от абстракции: промпты становятся подменяемыми
/// в тестах и верционируемыми без правки вызывающего кода.
/// <para>
/// Размещён в Abstractions: сигнатура использует только доменные типы
/// этого слоя (<see cref="AgentContext"/>, <see cref="Diagnosis"/>).
/// </para>
/// </summary>
public interface IAgentPromptBuilder
{
    /// <summary>
    /// Системный промпт: роль, доменный контекст, правила проекта, формат ответа.
    /// </summary>
    /// <param name="toolDescriptions">Описания доступных инструментов (белый список).</param>
    /// <param name="domainContext">Предметная картина мира из конфигурации;
    /// пустое значение — нейтральная формулировка.</param>
    string BuildSystemPrompt(string toolDescriptions, string? domainContext = null);

    /// <summary>Пользовательский промпт: контекст прогона.</summary>
    string BuildUserPrompt(AgentContext context);

    /// <summary>Промпт для этапа планирования правки.</summary>
    string BuildPlanPrompt(Diagnosis diagnosis, AgentContext context);
}
