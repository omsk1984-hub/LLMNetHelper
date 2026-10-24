using LLMNetHelper.CodeChange;
using LLMNetHelper.GitLab;

namespace LLMNetHelper.Tests.GitLab;

/// <summary>
/// Офлайн-тесты автовосстановления клонов в <see cref="GitLabService"/>:
/// распознавание неполных/повреждённых локальных каталогов и их
/// переклонирование, очистка осиротевших lock-файлов, удаление остатков
/// после неудачного клонирования.
/// <para>
/// Все операции идут против локального bare-репозитория (git clone по
/// файловому пути) — сеть не требуется, что соответствует правилам проекта
/// (.roo/rules-code/01-test.md).
/// </para>
/// </summary>
public class GitLabServiceRepairTests : IAsyncLifetime
{
    private string _root = "";
    private string _bareRepo = "";
    private GitLabOptions _options = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"gitlab_repair_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        // «Удалённый» репозиторий — локальный bare-каталог с одним коммитом.
        _bareRepo = Path.Combine(_root, "origin.git");
        Directory.CreateDirectory(_bareRepo);

        await Git("init", "--bare", "-b", "main", _bareRepo);

        var seed = Path.Combine(_root, "seed");
        Directory.CreateDirectory(seed);
        await Git("init", "-b", "main", seed);
        await Git("-C", seed, "config", "user.email", "test@example.local");
        await Git("-C", seed, "config", "user.name", "Test Agent");
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "# Seed\n");
        await Git("-C", seed, "add", "-A");
        await Git("-C", seed, "commit", "-m", "initial");

        // У seed-репозитория нет remote — пушим в bare напрямую по пути.
        var push = await Git("-C", seed, "push", _bareRepo, "main");
        Assert.True(push.Succeeded, push.Describe());

        _options = new GitLabOptions
        {
            Enabled = true,
            CloneRoot = Path.Combine(_root, "clones"),
            CloneTimeoutSeconds = 60,
            UpdateTimeoutSeconds = 60,
            CloneDepth = 1,
            GitExecutable = "git"
        };

        Directory.CreateDirectory(_options.CloneRoot);
    }

    public Task DisposeAsync()
    {
        TryDelete(_root);
        return Task.CompletedTask;
    }

    private GitLabProject MakeProject(string name = "demo.repo") =>
        new(
            Id: 1,
            Name: name,
            PathWithNamespace: $"GPM/backend/{name}",
            HttpUrlToRepo: _bareRepo,
            SshUrlToRepo: null,
            DefaultBranch: "main",
            LastActivityAt: null,
            OpenIssuesCount: 0,
            StarsCount: 0);

    private GitLabService MakeService() => new(null!, _options);

    private Task<ProcessResult> Git(params string[] args) =>
        ProcessRunner.RunAsync("git", args, _root, 60);

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

    // --- InspectLocalRepoAsync -------------------------------------------------

    [Fact]
    public async Task Inspect_MissingDirectory_ReturnsMissing()
    {
        var svc = MakeService();
        var path = Path.Combine(_options.CloneRoot, "does-not-exist");

        Assert.Equal(GitLabService.LocalRepoState.Missing, await svc.InspectLocalRepoAsync(path, default));
    }

    [Fact]
    public async Task InspectDirectoryWithoutGit_ReturnsNotARepo()
    {
        var svc = MakeService();
        var path = Path.Combine(_options.CloneRoot, "plain-dir");
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "file.txt"), "junk");

        Assert.Equal(GitLabService.LocalRepoState.NotARepo, await svc.InspectLocalRepoAsync(path, default));
    }

    [Fact]
    public async Task InspectInterruptedClone_ReturnsIncomplete()
    {
        // Имитация прерванного клона: git init без коммитов (HEAD не резолвится),
        // как после Kill() посреди приёма pack-файла.
        var svc = MakeService();
        var path = Path.Combine(_options.CloneRoot, "half.clone");
        Directory.CreateDirectory(path);
        await Git("init", "-b", "master", path);

        Assert.Equal(GitLabService.LocalRepoState.Incomplete, await svc.InspectLocalRepoAsync(path, default));
    }

    [Fact]
    public async Task InspectHealthyClone_ReturnsHealthy()
    {
        var svc = MakeService();
        var path = Path.Combine(_options.CloneRoot, "good.clone");

        var clone = await Git("clone", "--branch", "main", _bareRepo, path);
        Assert.True(clone.Succeeded, clone.Describe());

        Assert.Equal(GitLabService.LocalRepoState.Healthy, await svc.InspectLocalRepoAsync(path, default));
    }

    // --- ProcessRepoAsync: автовосстановление ----------------------------------

    [Fact]
    public async Task ProcessRepo_IncompleteClone_ReclonesAutomatically()
    {
        var svc = MakeService();
        var path = Path.Combine(_options.CloneRoot, "broken.clone");
        Directory.CreateDirectory(path);
        await Git("init", "-b", "master", path);
        await File.WriteAllTextAsync(
            Path.Combine(path, ".git", "objects", "tmp_pack_fake"),
            "остатки прерванного приёма");

        var result = await svc.ProcessRepoAsync(MakeProject(), path, forceClone: false, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("cloned", result.Action);
        Assert.NotNull(result.Note);
        Assert.Contains("неполон", result.Note);
        Assert.True(File.Exists(Path.Combine(path, "README.md")), "рабочие файлы должны появиться после переклонирования");
        Assert.False(File.Exists(Path.Combine(path, ".git", "objects", "tmp_pack_fake")),
            "осиротевший tmp_pack должен быть удалён вместе с огрызком");
    }

    [Fact]
    public async Task ProcessRepo_DirectoryWithoutGit_ReclonesAutomatically()
    {
        var svc = MakeService();
        var path = Path.Combine(_options.CloneRoot, "notrepo.clone");
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "junk.txt"), "не репозиторий");

        var result = await svc.ProcessRepoAsync(MakeProject(), path, forceClone: false, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("cloned", result.Action);
        Assert.NotNull(result.Note);
        Assert.Contains("не является git-репозиторием", result.Note);
        Assert.True(File.Exists(Path.Combine(path, "README.md")));
    }

    [Fact]
    public async Task ProcessRepo_MissingDirectory_Clones()
    {
        var svc = MakeService();
        var path = Path.Combine(_options.CloneRoot, "fresh.clone");

        var result = await svc.ProcessRepoAsync(MakeProject(), path, forceClone: false, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("cloned", result.Action);
        Assert.Null(result.Note);
        Assert.True(File.Exists(Path.Combine(path, "README.md")));
    }

    [Fact]
    public async Task ProcessRepo_HealthyClone_UpdatesWithoutDeletion()
    {
        var svc = MakeService();
        var path = Path.Combine(_options.CloneRoot, "healthy.clone");

        var clone = await Git("clone", "--branch", "main", _bareRepo, path);
        Assert.True(clone.Succeeded, clone.Describe());

        var marker = Path.Combine(path, "marker.txt");
        await File.WriteAllTextAsync(marker, "я должен остаться");

        var result = await svc.ProcessRepoAsync(MakeProject(), path, forceClone: false, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("updated", result.Action);
        Assert.True(File.Exists(marker), "здоровый репозиторий не должен удаляться");
    }

    [Fact]
    public async Task ProcessRepo_FailedClone_RemovesLeftovers()
    {
        var svc = MakeService();
        var path = Path.Combine(_options.CloneRoot, "fail.clone");

        var badProject = new GitLabProject(
            Id: 2,
            Name: "fail.repo",
            PathWithNamespace: "GPM/backend/fail.repo",
            HttpUrlToRepo: Path.Combine(_root, "no-such-remote.git"),
            SshUrlToRepo: null,
            DefaultBranch: "main",
            LastActivityAt: null,
            OpenIssuesCount: 0,
            StarsCount: 0);

        var result = await svc.ProcessRepoAsync(badProject, path, forceClone: false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.False(Directory.Exists(path), "огрызок каталога после неудачного клона должен быть удалён");
    }

    // --- CleanStaleLockFiles -----------------------------------------------------

    [Fact]
    public void CleanStaleLockFiles_RemovesLocksKeepsOthers()
    {
        var path = Path.Combine(_options.CloneRoot, "locks.clone");
        var gitDir = Path.Combine(path, ".git");
        Directory.CreateDirectory(Path.Combine(gitDir, "refs", "heads"));
        Directory.CreateDirectory(Path.Combine(gitDir, "objects"));

        File.WriteAllText(Path.Combine(gitDir, "index.lock"), "");
        File.WriteAllText(Path.Combine(gitDir, "shallow.lock"), "");
        File.WriteAllText(Path.Combine(gitDir, "refs", "heads", "master.lock"), "");
        File.WriteAllText(Path.Combine(gitDir, "config"), "[core]\n");
        File.WriteAllText(Path.Combine(gitDir, "objects", "keep.txt"), "не lock");

        var removed = GitLabService.CleanStaleLockFiles(path);

        Assert.Equal(3, removed);
        Assert.False(File.Exists(Path.Combine(gitDir, "index.lock")));
        Assert.False(File.Exists(Path.Combine(gitDir, "shallow.lock")));
        Assert.False(File.Exists(Path.Combine(gitDir, "refs", "heads", "master.lock")));
        Assert.True(File.Exists(Path.Combine(gitDir, "config")), "содержимое .git не должно страдать");
        Assert.True(File.Exists(Path.Combine(gitDir, "objects", "keep.txt")));
    }

    [Fact]
    public void CleanStaleLockFiles_NoGitDir_ReturnsZero()
    {
        var path = Path.Combine(_options.CloneRoot, "no-git-dir");
        Directory.CreateDirectory(path);

        Assert.Equal(0, GitLabService.CleanStaleLockFiles(path));
    }
}
