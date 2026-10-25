using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Analysis;

namespace LLMNetHelper.Agent.AgentLoop;

/// <summary>
/// Стратегия ReAct-цикла для диагноза (план 22, этапы 3.4/3.5): оркестратор.
/// <para>
/// Воспроизводит прежнее поведение <c>NativeAgentOrchestrator.DiagnoseAsync</c>:
/// финализация — JSON-диагноз через <see cref="DiagnosisParser"/> (иначе tool-call,
/// иначе Fallback-диагноз свободным текстом); исполнение инструмента — через
/// <see cref="IToolInvoker"/> с подписью «успешно, N символов»; сбой/пусто/лимиты —
/// Verdict.Unknown с прежними формулировками.
/// </para>
/// </summary>
public sealed class DiagnosisReActStrategy : IReActStrategy
{
    private readonly IToolInvoker _invoker;
    private readonly string _orchestrator = "native";

    public DiagnosisReActStrategy(IToolInvoker invoker)
    {
        _invoker = invoker;
    }

    public bool TryFinalize(
        string responseText,
        int toolCalls,
        int? promptTokens,
        int? completionTokens,
        out ReActOutcome outcome)
    {
        var diagnosis = DiagnosisParser.TryParse(
            responseText, _orchestrator, toolCalls, promptTokens, completionTokens);

        if (diagnosis is not null)
        {
            outcome = new ReActOutcome(null, diagnosis, toolCalls, promptTokens, completionTokens);
            return true;
        }

        if (ToolCallParser.TryParse(responseText, out _, out _))
        {
            // Запрос инструмента — цикл продолжается.
            outcome = null!;
            return false;
        }

        // Свободный текст — Fallback-диагноз.
        var summary = responseText.Length > 500 ? responseText[..500] + "…" : responseText;
        outcome = new ReActOutcome(
            null,
            new Diagnosis(
                Verdict.Unknown,
                summary.Trim(),
                [],
                [],
                ["Модель вернула ответ в свободной форме — требуется ручной разбор."],
                toolCalls,
                _orchestrator,
                promptTokens,
                completionTokens),
            toolCalls,
            promptTokens,
            completionTokens);
        return true;
    }

    public async Task<(string ResultText, bool Succeeded)> InvokeToolAsync(
        string toolName,
        string argumentsJson,
        CancellationToken ct)
    {
        var record = await _invoker.InvokeAsync(toolName, argumentsJson, ct).ConfigureAwait(false);
        if (!record.Succeeded)
        {
            return ($"Инструмент `{toolName}` не зарегистрирован в белом списке. " +
                    "Используй только инструменты из системного промпта.", false);
        }

        return ($"успешно, {record.ResultChars} символов", true);
    }

    public string FormatToolResult(string toolName, string resultText, bool succeeded) =>
        $"Результат инструмента `{toolName}`:\n{resultText}";

    public ReActOutcome OnLlmFailure(string message, int toolCalls, int? promptTokens, int? completionTokens) =>
        Unknown($"Сбой LLM: {message}", toolCalls, promptTokens, completionTokens);

    public ReActOutcome OnEmptyResponse(int toolCalls, int? promptTokens, int? completionTokens) =>
        Unknown("LLM вернул пустой ответ", toolCalls, promptTokens, completionTokens);

    public ReActOutcome OnToolCallLimitReached(string responseText, int toolCalls, int? promptTokens, int? completionTokens) =>
        Unknown("Достигнут лимит вызовов инструментов", toolCalls, promptTokens, completionTokens);

    public ReActOutcome OnStepLimitReached(int toolCalls, int? promptTokens, int? completionTokens) =>
        Unknown("Достигнут лимит шагов", toolCalls, promptTokens, completionTokens);

    private ReActOutcome Unknown(string reason, int toolCalls, int? promptTokens, int? completionTokens) =>
        new(null,
            new Diagnosis(Verdict.Unknown, reason, [], [], [], toolCalls, _orchestrator, promptTokens, completionTokens),
            toolCalls,
            promptTokens,
            completionTokens);
}