namespace LLMNetHelper.CodeChange;

/// <summary>
/// Абстракция прогона сборки и тестов — позволяет подменять в тестах.
/// </summary>
public interface IBuildRunner
{
    /// <summary>Собирает решение. Возвращает результат.</summary>
    Task<ProcessResult> BuildAsync(CancellationToken ct = default);

    /// <summary>Прогоняет тесты. Возвращает результат.</summary>
    Task<ProcessResult> TestAsync(CancellationToken ct = default);
}

/// <summary>
/// Реальный прогон <c>dotnet build</c> и <c>dotnet test</c>.
/// </summary>
public sealed class BuildTestRunner : IBuildRunner
{
    private readonly CodeChangeOptions _options;

    public BuildTestRunner(CodeChangeOptions options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public Task<ProcessResult> BuildAsync(CancellationToken ct = default)
    {
        return RunAsync(
            ["build", _options.SolutionPath, "--nologo", "-v", "minimal"],
            _options.BuildTimeoutSeconds,
            ct);
    }

    /// <inheritdoc />
    public Task<ProcessResult> TestAsync(CancellationToken ct = default)
    {
        if (_options.SkipTests)
        {
            return Task.FromResult(new ProcessResult(0, "тесты пропущены (SkipTests)", "", false));
        }

        return RunAsync(
            ["test", _options.SolutionPath, "--nologo", "-v", "minimal"],
            _options.TestTimeoutSeconds,
            ct);
    }

    private Task<ProcessResult> RunAsync(string[] args, int timeoutSeconds, CancellationToken ct)
    {
        return ProcessRunner.RunAsync(
            _options.DotnetExecutable,
            args,
            _options.RepositoryPath,
            timeoutSeconds,
            ct);
    }
}
