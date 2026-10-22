using System.Diagnostics;
using ArtasTools.MonitorAgent.Abstractions;

namespace ArtasTools.MonitorAgent.Agent.Tools;

/// <summary>
/// Реестр инструментов — <b>белый список</b>.
/// <para>
/// Агент физически не может вызвать инструмент, которого нет в реестре:
/// <see cref="InvokeAsync"/> проверяет имя до выполнения и возвращает отказ.
/// Это ключевой барьер: даже если модель «придумает» имя инструмента,
/// ничего не произойдёт.
/// </para>
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools;
    private readonly List<ToolCallRecord> _calls = [];
    private readonly object _lock = new();

    public ToolRegistry(IEnumerable<IAgentTool> tools)
    {
        _tools = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            // Одноимённые инструменты — ошибка конфигурации, а не «последний победил».
            if (!_tools.TryAdd(tool.Name, tool))
            {
                throw new InvalidOperationException($"Дублирующееся имя инструмента: {tool.Name}");
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IAgentTool> Tools => _tools.Values.ToArray();

    /// <summary>
    /// Журнал всех вызовов за прогон.
    /// </summary>
    public IReadOnlyList<ToolCallRecord> Calls
    {
        get
        {
            lock (_lock)
            {
                return _calls.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public IAgentTool? Find(string name)
    {
        return name is not null && _tools.TryGetValue(name, out var tool) ? tool : null;
    }

    /// <inheritdoc />
    public async Task<ToolCallRecord> InvokeAsync(
        string toolName,
        string argumentsJson,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (Find(toolName) is not { } tool)
        {
            var rejection = new ToolCallRecord(
                toolName ?? "",
                argumentsJson ?? "",
                0,
                (int)stopwatch.ElapsedMilliseconds,
                Succeeded: false);

            Record(rejection);
            return rejection;
        }

        string result;
        var succeeded = true;

        try
        {
            result = await tool.InvokeAsync(argumentsJson ?? "{}", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = $"Ошибка инструмента {toolName}: {ex.Message}";
            succeeded = false;
        }

        stopwatch.Stop();

        var record = new ToolCallRecord(
            tool.Name,
            argumentsJson ?? "",
            result.Length,
            (int)stopwatch.ElapsedMilliseconds,
            succeeded);

        Record(record);
        return record;
    }

    /// <summary>
    /// Формирует текст-описание доступных инструментов для системного промпта.
    /// </summary>
    public string DescribeForPrompt()
    {
        var sb = new System.Text.StringBuilder();

        foreach (var tool in _tools.Values)
        {
            sb.AppendLine($"- `{tool.Name}`: {tool.Description}");
        }

        return sb.ToString();
    }

    private void Record(ToolCallRecord record)
    {
        lock (_lock)
        {
            _calls.Add(record);
        }
    }
}
