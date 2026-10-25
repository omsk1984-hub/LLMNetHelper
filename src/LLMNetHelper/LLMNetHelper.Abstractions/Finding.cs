namespace LLMNetHelper.Abstractions;

/// <summary>
/// Находка LLM.
/// <para>
/// Перенесена из <c>LLMNetHelper.Core.Analysis</c> в Abstractions (план 18,
/// пункт 0.1): тип участвует в контракте <see cref="Diagnosis"/> между
/// оркестратором (Agent) и отчётностью (Running/Core), поэтому должен жить
/// в общем слое абстракций, а не в реализации парсера.
/// </para>
/// </summary>
public sealed class Finding
{
    public string Id { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Title { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Impact { get; set; } = "";
    public string Recommendation { get; set; } = "";
    public string Confidence { get; set; } = "";
}
