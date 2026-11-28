namespace Markwardt.AutoRemote.Tests.Integration;

[Collection("Git")]
public sealed class SwarmRepositoryTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly GitService git = new();

    private readonly SwarmRepository repository;

    public SwarmRepositoryTests() => repository = new SwarmRepository(git);

    public void Dispose() => temp.Dispose();

    private async Task<string> Git(string folder, params string[] arguments)
    {
        GitOutput result = await git.Run(folder, ["-c", "user.name=t", "-c", "user.email=t@t", .. arguments], CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        return result.Output.Trim();
    }

    private async Task<string> Project(string name)
    {
        string folder = temp.Combine(name);
        Directory.CreateDirectory(folder);
        await Git(folder, "init", "--quiet", "--initial-branch=main");
        await File.WriteAllTextAsync(Path.Combine(folder, ".gitignore"), "secret.txt\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "one\ntwo\nthree\n");
        await Git(folder, "add", "-A");
        await Git(folder, "commit", "--quiet", "-m", "first");
        return folder;
    }

    [Fact]
    public async Task Snapshot_CommitsUncommittedAndUntrackedFilesButNeverIgnoredOnesAndMovesNothing()
    {
        string project = await Project("project");
        string head = await Git(project, "rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(project, "a.txt"), "one\nchanged\nthree\n");
        await File.WriteAllTextAsync(Path.Combine(project, "new.txt"), "new\n");
        await File.WriteAllTextAsync(Path.Combine(project, "secret.txt"), "key\n");
        string before = await Git(project, "status", "--porcelain");

        (string commit, string key) = await repository.Snapshot(project, Guid.NewGuid(), CancellationToken.None);

        Assert.NotEqual(head, commit);
        Assert.Equal(head, key);
        Assert.Equal(head, await Git(project, "rev-parse", $"{commit}^"));
        Assert.Equal([".gitignore", "a.txt", "new.txt"], (await Git(project, "ls-tree", "--name-only", commit)).Split('\n'));
        Assert.Equal(before, await Git(project, "status", "--porcelain"));
        Assert.Equal(head, await Git(project, "rev-parse", "HEAD"));
        Assert.Equal("main", await Git(project, "branch", "--format=%(refname:short)"));
    }

    [Fact]
    public async Task Snapshot_IsTheCurrentCommitWhenNothingIsUncommittedAndNeedsARepositoryWithACommit()
    {
        string project = await Project("clean");
        string plain = temp.Combine("plain");
        string empty = temp.Combine("empty");
        Directory.CreateDirectory(plain);
        Directory.CreateDirectory(empty);
        await Git(empty, "init", "--quiet");

        (string commit, _) = await repository.Snapshot(project, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(await Git(project, "rev-parse", "HEAD"), commit);
        await Assert.ThrowsAsync<RequestException>(() => repository.Snapshot(plain, Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => repository.Snapshot(empty, Guid.NewGuid(), CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATaskTravelsAsABundleIsDoneInAFolderThatIsNoRepositoryAndItsResultIsWrittenOutWithoutTouchingTheProject(bool noPrefix)
    {
        string project = await Project("origin");
        await Git(project, "config", "diff.noprefix", noPrefix ? "true" : "false");
        await File.WriteAllTextAsync(Path.Combine(project, "a.txt"), "one\ntwo\nthree\nlocal\n");
        Guid task = Guid.NewGuid();
        (string snapshot, _) = await repository.Snapshot(project, task, CancellationToken.None);
        ReadOnlyMemory<byte> bundle = await repository.Bundle(project, task, [], CancellationToken.None);
        string cache = temp.Combine("cache.git");
        string work = temp.Combine("work");

        Assert.True(await repository.Receive(cache, bundle, task, snapshot, [], CancellationToken.None));
        await repository.Export(cache, work, snapshot, CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(work, ".git")));
        Assert.Equal("one\ntwo\nthree\nlocal\n", await File.ReadAllTextAsync(Path.Combine(work, "a.txt")));
        Assert.Null(await repository.Collect(cache, work, task, snapshot, "nothing", CancellationToken.None));

        await File.WriteAllTextAsync(Path.Combine(work, "a.txt"), "ONE\ntwo\nthree\nlocal\n");
        await File.WriteAllTextAsync(Path.Combine(work, "added.bin"), "\0\u0001binary");
        await File.WriteAllTextAsync(Path.Combine(work, "secret.txt"), "worker secret\n");
        string result = (await repository.Collect(cache, work, task, snapshot, "Delegated: change", CancellationToken.None))!;
        ReadOnlyMemory<byte> back = await repository.BundleResult(cache, task, snapshot, CancellationToken.None);

        (string commit, string branch) = await repository.Import(project, back, task, snapshot, "swarm/abc-change", CancellationToken.None);

        Assert.Equal((result, "swarm/abc-change"), (commit, branch));
        string changes = await repository.Changes(project, snapshot, commit, null, 10000, CancellationToken.None);
        Assert.Contains("added.bin", changes);
        Assert.DoesNotContain("secret.txt", changes);
        string status = await Git(project, "status", "--porcelain");
        string copy = temp.Combine("copy");

        IReadOnlyList<string> changed = await repository.Materialize(project, copy, snapshot, commit, "The report.", CancellationToken.None);

        Assert.Equal(["M\ta.txt", "A\tadded.bin"], changed.OrderBy(line => line[2..], StringComparer.Ordinal));
        Assert.Equal("ONE\ntwo\nthree\nlocal\n", await File.ReadAllTextAsync(Path.Combine(copy, "files", "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(copy, "files", ".git")));
        Assert.Contains("diff --git a/added.bin b/added.bin", await File.ReadAllTextAsync(Path.Combine(copy, "changes.diff")));
        Assert.Contains("+ONE", await File.ReadAllTextAsync(Path.Combine(copy, "changes.diff")));
        Assert.Equal("The report.\n", await File.ReadAllTextAsync(Path.Combine(copy, "report.md")));
        Assert.Equal(status, await Git(project, "status", "--porcelain"));
        Assert.Equal("one\ntwo\nthree\nlocal\n", await File.ReadAllTextAsync(Path.Combine(project, "a.txt")));

        await repository.Release(project, task, branch, CancellationToken.None);

        Assert.Equal("main", await Git(project, "branch", "--format=%(refname:short)"));
    }

    [Fact]
    public async Task Materialize_NeverMakesLinksFromAResultInARepositoryThatWouldAndLeavesAToolsConfigurationOut()
    {
        string project = await Project("copy-links");
        File.CreateSymbolicLink(Path.Combine(project, "link"), "/etc/hostname");
        await File.WriteAllTextAsync(Path.Combine(project, "opencode.json"), "{}\n");
        await Git(project, "add", "-A");
        await Git(project, "commit", "--quiet", "-m", "link");
        string head = await Git(project, "rev-parse", "HEAD");
        string copy = temp.Combine("links-copy");

        await repository.Materialize(project, copy, head, head, "r", CancellationToken.None);

        Assert.Null(new FileInfo(Path.Combine(copy, "files", "link")).LinkTarget);
        Assert.Equal("/etc/hostname", await File.ReadAllTextAsync(Path.Combine(copy, "files", "link")));
        Assert.False(File.Exists(Path.Combine(copy, "files", "opencode.json")));
        Assert.Equal(string.Empty, await Git(project, "status", "--porcelain"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheResultsATaskBuildsOnTravelBesideItsBaseAlsoWhenTheWorkerHasTheBaseAndLeaveWithTheTask(bool knowsBase)
    {
        string project = await Project("dependencies");
        Guid first = Guid.NewGuid();
        (string snapshot, _) = await repository.Snapshot(project, first, CancellationToken.None);
        string cache = temp.Combine("dependencies.git");
        string work = temp.Combine("dependencies-work");
        await repository.Receive(cache, await repository.Bundle(project, first, [], CancellationToken.None), first, snapshot, [], CancellationToken.None);
        await repository.Export(cache, work, snapshot, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(work, "first.txt"), "first\n");
        string collected = (await repository.Collect(cache, work, first, snapshot, "first", CancellationToken.None))!;
        (string result, _) = await repository.Import(project, await repository.BundleResult(cache, first, snapshot, CancellationToken.None), first, snapshot, "swarm/first", CancellationToken.None);
        Guid second = Guid.NewGuid();
        await repository.Pin(project, second, snapshot, CancellationToken.None);
        await repository.PinDependency(project, second, first, result, CancellationToken.None);
        SwarmDependency dependency = new() { TaskId = first, Title = "first", BaseCommit = snapshot, ResultCommit = result };

        ReadOnlyMemory<byte> bundle = await repository.Bundle(project, second, knowsBase ? [snapshot] : [], CancellationToken.None);

        string other = temp.Combine("dependencies-other.git");
        Assert.Equal(!knowsBase, await repository.Receive(other, bundle, second, snapshot, [dependency], CancellationToken.None));
        string receiving = knowsBase ? cache : other;
        await repository.Forget(cache, first, CancellationToken.None);
        Assert.True(await repository.Receive(receiving, bundle, second, snapshot, [dependency], CancellationToken.None));
        Assert.Equal(collected, await Git(receiving, "rev-parse", $"refs/task-dependencies/{second:N}/{first:N}"));
        await repository.Materialize(receiving, temp.Combine("dependency-copy"), snapshot, result, "r", CancellationToken.None);
        Assert.Equal("first\n", await File.ReadAllTextAsync(Path.Combine(temp.Combine("dependency-copy"), "files", "first.txt")));

        await repository.Forget(receiving, second, CancellationToken.None);
        await repository.Release(project, second, null, CancellationToken.None);

        Assert.Equal(string.Empty, await Git(receiving, "for-each-ref", "refs/task-dependencies"));
        Assert.Equal(string.Empty, await Git(project, "for-each-ref", "refs/auto-remote/dependencies"));
    }

    [Fact]
    public async Task ALaterBundleLeavesOutWhatTheWorkerHasAndAWorkerWithoutItAsksForTheWholeHistory()
    {
        string project = await Project("incremental");
        Guid first = Guid.NewGuid();
        (string known, _) = await repository.Snapshot(project, first, CancellationToken.None);
        string cache = temp.Combine("incremental.git");
        await repository.Receive(cache, await repository.Bundle(project, first, [], CancellationToken.None), first, known, [], CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(project, "b.txt"), "b\n");
        Guid second = Guid.NewGuid();
        (string snapshot, _) = await repository.Snapshot(project, second, CancellationToken.None);

        ReadOnlyMemory<byte> small = await repository.Bundle(project, second, [known], CancellationToken.None);

        Assert.False(await repository.Receive(temp.Combine("other.git"), small, second, snapshot, [], CancellationToken.None));
        Assert.True(await repository.Receive(cache, small, second, snapshot, [], CancellationToken.None));
        Guid same = Guid.NewGuid();
        await repository.Pin(project, same, snapshot, CancellationToken.None);
        ReadOnlyMemory<byte> nothing = await repository.Bundle(project, same, [snapshot], CancellationToken.None);
        Assert.True(nothing.IsEmpty);
        Assert.True(await repository.Receive(cache, nothing, same, snapshot, [], CancellationToken.None));
        Assert.False(await repository.Receive(temp.Combine("third.git"), nothing, same, snapshot, [], CancellationToken.None));
    }

    [Fact]
    public async Task Import_RefusesAResultThatDoesNotBuildOnTheTasksBase()
    {
        string project = await Project("refuse");
        Guid task = Guid.NewGuid();
        (string snapshot, _) = await repository.Snapshot(project, task, CancellationToken.None);
        string cache = temp.Combine("refuse.git");
        string work = temp.Combine("refuse-work");
        await repository.Receive(cache, await repository.Bundle(project, task, [], CancellationToken.None), task, snapshot, [], CancellationToken.None);
        await repository.Export(cache, work, snapshot, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(work, "a.txt"), "changed\n");
        await repository.Collect(cache, work, task, snapshot, "change", CancellationToken.None);
        ReadOnlyMemory<byte> result = await repository.BundleResult(cache, task, snapshot, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(project, "c.txt"), "c\n");
        await Git(project, "add", "-A");
        await Git(project, "commit", "--quiet", "-m", "second");
        string other = await Git(project, "rev-parse", "HEAD");

        await Assert.ThrowsAsync<RequestException>(() => repository.Import(project, result, task, other, "swarm/bad", CancellationToken.None));

        Assert.Equal("main", await Git(project, "branch", "--format=%(refname:short)"));
    }

    [Fact]
    public async Task Export_WritesSymbolicLinksAsPlainFiles()
    {
        string project = await Project("links");
        File.CreateSymbolicLink(Path.Combine(project, "link"), "/etc/hostname");
        Guid task = Guid.NewGuid();
        (string snapshot, _) = await repository.Snapshot(project, task, CancellationToken.None);
        string cache = temp.Combine("links.git");
        string work = temp.Combine("links-work");
        await repository.Receive(cache, await repository.Bundle(project, task, [], CancellationToken.None), task, snapshot, [], CancellationToken.None);

        await repository.Export(cache, work, snapshot, CancellationToken.None);

        FileInfo link = new(Path.Combine(work, "link"));
        Assert.Null(link.LinkTarget);
        Assert.Equal("/etc/hostname", await File.ReadAllTextAsync(link.FullName));
    }

    [Fact]
    public async Task Collect_NeverRunsGitInARepositoryTheAiMadeInsideTheTaskAndKeepsSubmodulesAsTheyWere()
    {
        string project = await Project("nested");
        string pinned = await Git(project, "rev-parse", "HEAD");
        await Git(project, "update-index", "--add", "--cacheinfo", $"160000,{pinned},sub");
        await Git(project, "commit", "--quiet", "-m", "submodule");
        Directory.CreateDirectory(Path.Combine(project, "sub"));
        Guid task = Guid.NewGuid();
        (string snapshot, _) = await repository.Snapshot(project, task, CancellationToken.None);
        string cache = temp.Combine("nested.git");
        string work = temp.Combine("nested-work");
        await repository.Receive(cache, await repository.Bundle(project, task, [], CancellationToken.None), task, snapshot, [], CancellationToken.None);
        await repository.Export(cache, work, snapshot, CancellationToken.None);
        string marker = temp.Combine("PWNED");

        foreach (string inside in (string[])["sub", "other", ""])
        {
            string nested = Path.Combine(work, inside, ".git");
            Directory.CreateDirectory(Path.Combine(nested, "objects"));
            Directory.CreateDirectory(Path.Combine(nested, "refs"));
            await File.WriteAllTextAsync(Path.Combine(nested, "HEAD"), $"{pinned}\n");
            await File.WriteAllTextAsync(Path.Combine(nested, "config"), $"[core]\n\trepositoryformatversion = 0\n\tfsmonitor = touch {marker}\n");
        }

        await File.WriteAllTextAsync(Path.Combine(work, "other", "kept.txt"), "kept\n");

        string result = (await repository.Collect(cache, work, task, snapshot, "change", CancellationToken.None))!;

        Assert.False(File.Exists(marker));
        Assert.Equal("other/kept.txt", await Git(cache, "diff", "--name-only", snapshot, result));
        Assert.StartsWith($"160000 commit {pinned}", await Git(cache, "ls-tree", result, "sub"));
    }

    [Fact]
    public async Task TheConfigurationOfAiToolsNeverTravelsIntoATaskOrBackFromOne()
    {
        string project = await Project("tools");
        Directory.CreateDirectory(Path.Combine(project, ".opencode", "plugins"));
        Directory.CreateDirectory(Path.Combine(project, "deep", ".claude"));
        await File.WriteAllTextAsync(Path.Combine(project, ".opencode", "plugins", "p.js"), "p\n");
        await File.WriteAllTextAsync(Path.Combine(project, "opencode.json"), "{}\n");
        await File.WriteAllTextAsync(Path.Combine(project, "deep", ".claude", "settings.json"), "{}\n");
        Guid task = Guid.NewGuid();
        (string snapshot, _) = await repository.Snapshot(project, task, CancellationToken.None);
        string cache = temp.Combine("tools.git");
        string work = temp.Combine("tools-work");
        await repository.Receive(cache, await repository.Bundle(project, task, [], CancellationToken.None), task, snapshot, [], CancellationToken.None);

        await repository.Export(cache, work, snapshot, CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(work, ".opencode")));
        Assert.False(File.Exists(Path.Combine(work, "opencode.json")));
        Assert.False(Directory.Exists(Path.Combine(work, "deep", ".claude")));

        Directory.CreateDirectory(Path.Combine(work, ".opencode", "plugins"));
        await File.WriteAllTextAsync(Path.Combine(work, ".opencode", "plugins", "evil.js"), "evil\n");
        await File.WriteAllTextAsync(Path.Combine(work, ".MCP.json"), "{}\n");
        await File.WriteAllTextAsync(Path.Combine(work, "a.txt"), "changed\n");
        string result = (await repository.Collect(cache, work, task, snapshot, "change", CancellationToken.None))!;

        Assert.Equal("a.txt", await Git(cache, "diff", "--name-only", snapshot, result));
    }

    [Fact]
    public async Task Import_RefusesAResultThatChangesTheConfigurationOfAnAiTool()
    {
        string project = await Project("hostile");
        Guid task = Guid.NewGuid();
        (string snapshot, _) = await repository.Snapshot(project, task, CancellationToken.None);
        string hostile = temp.Combine("hostile-worker");
        await Git(project, "worktree", "add", "--quiet", "--detach", hostile, snapshot);
        Directory.CreateDirectory(Path.Combine(hostile, "x", ".opencode"));
        await File.WriteAllTextAsync(Path.Combine(hostile, "x", ".opencode", "evil.js"), "evil\n");
        await Git(hostile, "add", "-A");
        await Git(hostile, "commit", "--quiet", "-m", "evil");
        await Git(hostile, "update-ref", $"refs/results/{task:N}", "HEAD");
        string file = temp.Combine("hostile.bundle");
        await Git(hostile, "bundle", "create", file, $"refs/results/{task:N}", $"^{snapshot}");
        await Git(project, "worktree", "remove", "--force", hostile);

        RequestException refused = await Assert.ThrowsAsync<RequestException>(async () => await repository.Import(project, await File.ReadAllBytesAsync(file), task, snapshot, "swarm/evil", CancellationToken.None));

        Assert.Contains("configuration of an AI tool", refused.Message);
        Assert.Equal("main", await Git(project, "branch", "--format=%(refname:short)"));
    }

    [Theory]
    [InlineData("/home/me/other-repo")]
    [InlineData("file:///home/me/other-repo")]
    [InlineData("ext::sh -c 'touch /tmp/owned'")]
    [InlineData("-uploadpack=touch")]
    [InlineData("../elsewhere")]
    public async Task AWorkerFetchesOnlyFromNetworkAddresses(string url)
    {
        RequestException refused = await Assert.ThrowsAsync<RequestException>(() => repository.FetchBase(temp.Combine("c.git"), url, "swarm/tasks/x", Guid.NewGuid(), new string('a', 40), CancellationToken.None));

        Assert.Contains("not a network address", refused.Message);
    }

    [Fact]
    public async Task InTheGitRemoteModeTheCodeTravelsThroughBranchesOfTheOriginThatAreDeletedAfterwards()
    {
        string origin = temp.Combine("origin.git");
        Directory.CreateDirectory(origin);
        await Git(origin, "init", "--quiet", "--bare");
        string project = await Project("remote-project");
        await Git(project, "remote", "add", "origin", origin);
        SwarmRepository local = new(git) { AllowLocalRemotes = true };
        Guid task = Guid.NewGuid();
        (string snapshot, _) = await local.Snapshot(project, task, CancellationToken.None);
        string url = await local.Remote(project, CancellationToken.None);
        await local.Push(project, "origin", snapshot, $"swarm/tasks/{task:N}", CancellationToken.None);
        string cache = temp.Combine("remote-cache.git");
        string work = temp.Combine("remote-work");

        await local.FetchBase(cache, url, $"swarm/tasks/{task:N}", task, snapshot, CancellationToken.None);
        await local.Export(cache, work, snapshot, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(work, "a.txt"), "remote\n");
        string result = (await local.Collect(cache, work, task, snapshot, "change", CancellationToken.None))!;
        await local.PushResult(cache, url, task, $"swarm/results/{task:N}", CancellationToken.None);
        (string commit, string branch) = await local.Fetch(project, $"swarm/results/{task:N}", task, snapshot, "swarm/r", CancellationToken.None);
        await local.DeleteRemote(project, [$"swarm/tasks/{task:N}", $"swarm/results/{task:N}"], CancellationToken.None);

        Assert.Equal((result, "swarm/r"), (commit, branch));
        Assert.Equal(string.Empty, await Git(origin, "for-each-ref", "refs/heads/swarm"));
    }

    [Fact]
    public async Task Remote_NeverCarriesAPasswordAndSaysWhenThereIsNone()
    {
        string project = await Project("auth");

        await Assert.ThrowsAsync<RequestException>(() => repository.Remote(project, CancellationToken.None));

        await Git(project, "remote", "add", "origin", "https://user:token@example.com/repo.git");

        Assert.Equal("https://example.com/repo.git", await repository.Remote(project, CancellationToken.None));
    }
}
