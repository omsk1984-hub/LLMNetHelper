using LLMNetHelper.Abstractions;
using LLMNetHelper.CodeChange;
using LLMNetHelper.Roslyn;

namespace LLMNetHelper.Tests.CodeChange;

/// <summary>
/// Интеграционные тесты транзакции правок.
/// Работают на временном git-репозитории; сборка и тесты заменены фейком.
/// </summary>
public class ChangeTransactionTests : IAsyncLifetime
{
    private string _repoRoot = "";

    /// <summary>
    /// Фейковый прогон сборки/тестов: результат задаётся тестом.
    /// </summary>
    private sealed class FakeBuildRunner(bool buildOk, bool testsOk) : IBuildRunner
    {
        public int BuildCalls { get; private set; }

        public int TestCalls { get; private set; }

        public Task<ProcessResult> BuildAsync(CancellationToken ct = default)
        {
            BuildCalls++;
            return Task.FromResult(new ProcessResult(
                buildOk ? 0 : 1, "", buildOk ? "" : "error CS0001", TimedOut: false));
        }

        public Task<ProcessResult> TestAsync(CancellationToken ct = default)
        {
            TestCalls++;
            return Task.FromResult(new ProcessResult(
                testsOk ? 0 : 1, "", testsOk ? "" : "1 failed", TimedOut: false));
        }
    }

    public async Task InitializeAsync()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), $"cc_repo_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoRoot);

        // Минимальный «репозиторий»: файл проекта + исходник.
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "Sample.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <NuGetAudit>false</NuGetAudit>
              </PropertyGroup>
            </Project>
            """);

        Directory.CreateDirectory(Path.Combine(_repoRoot, "src"));

        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "src", "Sample.cs"),
            """
            namespace Sample;

            public static class Worker
            {
                public static int Compute(int value)
                {
                    return value + 1;
                }
            }
            """);

        await Git("init", "-b", "main");
        await Git("config", "user.email", "test@example.local");
        await Git("config", "user.name", "Test Agent");
        await Git("add", "-A");
        await Git("commit", "-m", "initial");
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_repoRoot))
            {
                // .git может содержать read-only файлы — чистим директорию целиком.
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

    private static FixPlan PlanWithChange()
    {
        return new FixPlan(
            "добавляем проверку на null",
            [
                new ProposedChange(
                    FilePath: "src/Sample.cs",
                    SymbolName: "Compute",
                    ChangeKind: "ReplaceMethodBody",
                    NewCode: "if (value < 0) { return 0; }\nreturn value + 1;",
                    Explanation: "защита от отрицательных значений")
            ],
            ["поведение для отрицательных значений меняется"],
            RequiresManualReview: true);
    }

    private ChangeTransaction CreateTransaction(FakeBuildRunner build, bool enabled = true)
    {
        var options = new CodeChangeOptions
        {
            Enabled = enabled,
            RepositoryPath = _repoRoot,
            BaseBranch = "main",
            RequireCleanWorktree = true,
            SolutionPath = "Sample.csproj",
            BuildTimeoutSeconds = 60,
            TestTimeoutSeconds = 60
        };

        var roslynOptions = new RoslynOptions
        {
            Enabled = true,
            SolutionPath = Path.Combine(_repoRoot, "Sample.csproj"),
            CompilationCacheSeconds = 0
        };

        return new ChangeTransaction(
            options,
            new GitService(options),
            build,
            new AstPatcher(),
            new CompilationValidator(),
            new SolutionLoader(roslynOptions));
    }

    [Fact]
    public async Task Apply_Disabled_SkipsWithoutTouchingRepo()
    {
        var build = new FakeBuildRunner(true, true);
        var tx = CreateTransaction(build, enabled: false);

        var outcome = await tx.ApplyAsync(1, PlanWithChange());

        Assert.Equal(ChangeFailure.Disabled, outcome.Failure);
        Assert.Equal(0, build.BuildCalls);
    }

    [Fact]
    public async Task Apply_NoChanges_Skips()
    {
        var build = new FakeBuildRunner(true, true);
        var tx = CreateTransaction(build);

        var outcome = await tx.ApplyAsync(1, FixPlan.Empty);

        Assert.Equal(ChangeFailure.NoChanges, outcome.Failure);
    }

    [Fact]
    public async Task Apply_NotRepository_Fails()
    {
        var build = new FakeBuildRunner(true, true);
        var options = new CodeChangeOptions
        {
            Enabled = true,
            RepositoryPath = Path.Combine(Path.GetTempPath(), "no-such-repo-" + Guid.NewGuid().ToString("N")),
            RequireCleanWorktree = false
        };

        var roslynOptions = new RoslynOptions { Enabled = false };
        using var loader = new SolutionLoader(roslynOptions);

        var tx = new ChangeTransaction(
            options,
            new GitService(options),
            build,
            new AstPatcher(),
            new CompilationValidator(),
            loader);

        var outcome = await tx.ApplyAsync(1, PlanWithChange());

        Assert.Equal(ChangeFailure.NoRepository, outcome.Failure);
    }

    [Fact]
    public async Task Apply_DirtyWorktree_Refuses()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, "untracked.txt"), "dirty");

        var build = new FakeBuildRunner(true, true);
        var tx = CreateTransaction(build);

        var outcome = await tx.ApplyAsync(1, PlanWithChange());

        Assert.Equal(ChangeFailure.DirtyWorktree, outcome.Failure);
        Assert.Equal(0, build.BuildCalls);

        // Чистим, чтобы Dispose смог удалить каталог.
        File.Delete(Path.Combine(_repoRoot, "untracked.txt"));
    }

    [Fact]
    public async Task Apply_BuildFails_RollsBackBranch()
    {
        var build = new FakeBuildRunner(buildOk: false, testsOk: false);
        var tx = CreateTransaction(build);

        var outcome = await tx.ApplyAsync(2, PlanWithChange());

        Assert.Equal(ChangeFailure.BuildFailed, outcome.Failure);
        Assert.Null(outcome.BranchName);
        Assert.Equal(1, build.BuildCalls);
        Assert.Equal(0, build.TestCalls);

        // Ветка удалена, мы на main.
        var branch = await Git("rev-parse", "--abbrev-ref", "HEAD");
        Assert.Equal("main", branch.StdOut.Trim());

        var branches = await Git("branch", "--list", "fix/ai-*");
        Assert.True(string.IsNullOrWhiteSpace(branches.StdOut));
    }

    [Fact]
    public async Task Apply_TestsFail_RollsBackBranch()
    {
        var build = new FakeBuildRunner(buildOk: true, testsOk: false);
        var tx = CreateTransaction(build);

        var outcome = await tx.ApplyAsync(3, PlanWithChange());

        Assert.Equal(ChangeFailure.TestsFailed, outcome.Failure);
        Assert.Equal(1, build.BuildCalls);
        Assert.Equal(1, build.TestCalls);

        var branch = await Git("rev-parse", "--abbrev-ref", "HEAD");
        Assert.Equal("main", branch.StdOut.Trim());
    }

    [Fact]
    public async Task Apply_UnknownSymbol_Fails()
    {
        var build = new FakeBuildRunner(true, true);
        var tx = CreateTransaction(build);

        var plan = new FixPlan(
            "правка несуществующего метода",
            [
                new ProposedChange("src/Sample.cs", "NoSuchMethod", "ReplaceMethodBody", "return 0;", "")
            ],
            [],
            false);

        var outcome = await tx.ApplyAsync(4, plan);

        Assert.Equal(ChangeFailure.ApplyFailed, outcome.Failure);
        Assert.Contains("NoSuchMethod", outcome.Message, StringComparison.OrdinalIgnoreCase);

        // Ветка должна быть удалена.
        var branch = await Git("rev-parse", "--abbrev-ref", "HEAD");
        Assert.Equal("main", branch.StdOut.Trim());
    }

    [Fact]
    public async Task Apply_HappyPath_CreatesBranchAndCommit()
    {
        var build = new FakeBuildRunner(true, true);
        var tx = CreateTransaction(build);

        var outcome = await tx.ApplyAsync(5, PlanWithChange());

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.NotNull(outcome.BranchName);
        Assert.StartsWith("fix/ai-5", outcome.BranchName);
        Assert.NotNull(outcome.CommitSha);
        Assert.Equal(1, outcome.AppliedChanges);
        Assert.Contains("if (value < 0)", outcome.Diff);

        // Мы на ветке исправления.
        var branch = await Git("rev-parse", "--abbrev-ref", "HEAD");
        Assert.Equal(outcome.BranchName, branch.StdOut.Trim());

        // Коммит существует и содержит наше сообщение.
        var log = await Git("log", "-1", "--pretty=%B");
        Assert.Contains("инциденту #5", log.StdOut);

        // Изменение попало в файл.
        var content = await File.ReadAllTextAsync(Path.Combine(_repoRoot, "src", "Sample.cs"));
        Assert.Contains("if (value < 0)", content);
    }

    [Fact]
    public async Task Apply_HappyPath_DoesNotPush()
    {
        var build = new FakeBuildRunner(true, true);
        var tx = CreateTransaction(build);

        var outcome = await tx.ApplyAsync(6, PlanWithChange());

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Contains("push не выполнялся", outcome.Message);

        // Нет remote — значит и push невозможен; проверяем, что не создан.
        var remotes = await Git("remote");
        Assert.True(string.IsNullOrWhiteSpace(remotes.StdOut));
    }
}
