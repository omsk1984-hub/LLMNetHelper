using LLMNetHelper.CodeChange;

namespace LLMNetHelper.Tests.CodeChange;

/// <summary>
/// Интеграционные тесты для FetchAsync / PullHardAsync.
/// Работают на временном git-репозитории.
/// </summary>
public class GitServiceFetchTests : IAsyncLifetime
{
    private string _repoRoot = "";
    private GitService _git = null!;

    public async Task InitializeAsync()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), $"git_fetch_repo_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoRoot);

        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "README.md"),
            "# Test Repo\n");

        await Git("init", "-b", "main");
        await Git("config", "user.email", "test@example.local");
        await Git("config", "user.name", "Test Agent");
        await Git("add", "-A");
        await Git("commit", "-m", "initial");

        var options = new CodeChangeOptions
        {
            RepositoryPath = _repoRoot,
            GitExecutable = "git"
        };

        _git = new GitService(options);
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_repoRoot))
            {
                Directory.Delete(_repoRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return Task.CompletedTask;
    }

    private async Task<ProcessResult> Git(params string[] args)
    {
        return await ProcessRunner.RunAsync("git", args, _repoRoot, 60);
    }

    [Fact]
    public async Task Fetch_NoRemote_ReturnsFalse()
    {
        // Репозиторий без remote — fetch возвращает false, не бросает.
        var result = await _git.FetchAsync();
        Assert.False(result);
    }

    [Fact]
    public async Task Fetch_WithRemote_ReturnsTrue()
    {
        var bareRepo = Path.Combine(Path.GetTempPath(), $"bare_{Guid.NewGuid():N}");
        Directory.CreateDirectory(bareRepo);

        try
        {
            await ProcessRunner.RunAsync("git", ["init", "--bare"], bareRepo, 60);
            await Git("remote", "add", "origin", bareRepo);
            await Git("push", "origin", "main");

            var result = await _git.FetchAsync();
            Assert.True(result);
        }
        finally
        {
            TryDelete(bareRepo);
        }
    }

    [Fact]
    public async Task PullHard_ResetsToOriginMain()
    {
        var bareRepo = Path.Combine(Path.GetTempPath(), $"bare_{Guid.NewGuid():N}");
        Directory.CreateDirectory(bareRepo);

        try
        {
            await ProcessRunner.RunAsync("git", ["init", "--bare"], bareRepo, 60);
            await Git("remote", "add", "origin", bareRepo);
            await Git("push", "origin", "main");

            var fetch = await _git.FetchAsync();
            Assert.True(fetch);

            var pull = await _git.PullHardAsync();
            Assert.True(pull.Succeeded);

            var branch = await _git.GetCurrentBranchAsync();
            Assert.Equal("main", branch);
        }
        finally
        {
            TryDelete(bareRepo);
        }
    }

    [Fact]
    public async Task PullHard_CleansUntracked()
    {
        var bareRepo = Path.Combine(Path.GetTempPath(), $"bare_{Guid.NewGuid():N}");
        Directory.CreateDirectory(bareRepo);

        try
        {
            await ProcessRunner.RunAsync("git", ["init", "--bare"], bareRepo, 60);
            await Git("remote", "add", "origin", bareRepo);
            await Git("push", "origin", "main");

            await File.WriteAllTextAsync(
                Path.Combine(_repoRoot, "untracked.txt"), "dirty");

            var fetch = await _git.FetchAsync();
            Assert.True(fetch);

            var pull = await _git.PullHardAsync();
            Assert.True(pull.Succeeded);

            Assert.False(File.Exists(Path.Combine(_repoRoot, "untracked.txt")));
        }
        finally
        {
            TryDelete(bareRepo);
        }
    }

    [Fact]
    public async Task PullHard_WithoutRemote_ReturnsFailure()
    {
        // Без remote pull вернёт failure (не бросает исключение).
        var pull = await _git.PullHardAsync();
        Assert.False(pull.Succeeded);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
