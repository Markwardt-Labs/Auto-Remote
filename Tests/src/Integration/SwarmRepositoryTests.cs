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
    public async Task ATaskTravelsAsABundleIsDoneInAFolderThatIsNoRepositoryAndComesBackAsABranchThatApplies(bool noPrefix)
    {
        string project = await Project("origin");
        await Git(project, "config", "diff.noprefix", noPrefix ? "true" : "false");
        await File.WriteAllTextAsync(Path.Combine(project, "a.txt"), "one\ntwo\nthree\nlocal\n");
        Guid task = Guid.NewGuid();
        (string snapshot, _) = await repository.Snapshot(project, task, CancellationToken.None);
        ReadOnlyMemory<byte> bundle = await repository.Bundle(project, task, [], CancellationToken.None);
        string cache = temp.Combine("cache.git");
        string work = temp.Combine("work");

        Assert.True(await repository.Receive(cache, bundle, task, snapshot, CancellationToken.None));
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
        await File.WriteAllTextAsync(Path.Combine(project, "a.txt"), "one\ntwo\nthree\nlocal\nlater\n");
        string index = await Git(project, "diff", "--cached", "--name-only");

        SwarmApplyResult applied = await repository.Apply(project, snapshot, commit, CancellationToken.None);

        Assert.Empty(applied.Conflicts);
        Assert.Equal(2, applied.Files);
        Assert.Equal("ONE\ntwo\nthree\nlocal\nlater\n", await File.ReadAllTextAsync(Path.Combine(project, "a.txt")));
        Assert.True(File.Exists(Path.Combine(project, "added.bin")));
        Assert.Equal(index, await Git(project, "diff", "--cached", "--name-only"));

        await repository.Release(project, task, branch, CancellationToken.None);

        Assert.Equal("main", await Git(project, "branch", "--format=%(refname:short)"));
    }

    [Fact]
    public async Task Apply_LeavesConflictMarkersWhereBothChangedTheSameLines()
    {
        string project = await Project("conflict");
        Guid task = Guid.NewGuid();
        (string snapshot, _) = await repository.Snapshot(project, task, CancellationToken.None);
        string cache = temp.Combine("conflict.git");
        string work = temp.Combine("conflict-work");
        await repository.Receive(cache, await repository.Bundle(project, task, [], CancellationToken.None), task, snapshot, CancellationToken.None);
        await repository.Export(cache, work, snapshot, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(work, "a.txt"), "one\ntheirs\nthree\n");
        await repository.Collect(cache, work, task, snapshot, "change", CancellationToken.None);
        (string commit, _) = await repository.Import(project, await repository.BundleResult(cache, task, snapshot, CancellationToken.None), task, snapshot, "swarm/x", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(project, "a.txt"), "one\nmine\nthree\n");

        SwarmApplyResult applied = await repository.Apply(project, snapshot, commit, CancellationToken.None);

        Assert.Equal(["a.txt"], applied.Conflicts);
        string content = await File.ReadAllTextAsync(Path.Combine(project, "a.txt"));
        Assert.Contains("<<<<<<<", content);
        Assert.Contains("theirs", content);
        Assert.Contains("mine", content);
    }

    [Fact]
    public async Task ALaterBundleLeavesOutWhatTheWorkerHasAndAWorkerWithoutItAsksForTheWholeHistory()
    {
        string project = await Project("incremental");
        Guid first = Guid.NewGuid();
        (string known, _) = await repository.Snapshot(project, first, CancellationToken.None);
        string cache = temp.Combine("incremental.git");
        await repository.Receive(cache, await repository.Bundle(project, first, [], CancellationToken.None), first, known, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(project, "b.txt"), "b\n");
        Guid second = Guid.NewGuid();
        (string snapshot, _) = await repository.Snapshot(project, second, CancellationToken.None);

        ReadOnlyMemory<byte> small = await repository.Bundle(project, second, [known], CancellationToken.None);

        Assert.False(await repository.Receive(temp.Combine("other.git"), small, second, snapshot, CancellationToken.None));
        Assert.True(await repository.Receive(cache, small, second, snapshot, CancellationToken.None));
        Guid same = Guid.NewGuid();
        await repository.Pin(project, same, snapshot, CancellationToken.None);
        ReadOnlyMemory<byte> nothing = await repository.Bundle(project, same, [snapshot], CancellationToken.None);
        Assert.True(nothing.IsEmpty);
        Assert.True(await repository.Receive(cache, nothing, same, snapshot, CancellationToken.None));
        Assert.False(await repository.Receive(temp.Combine("third.git"), nothing, same, snapshot, CancellationToken.None));
    }

    [Fact]
    public async Task Import_RefusesAResultThatDoesNotBuildOnTheTasksBase()
    {
        string project = await Project("refuse");
        Guid task = Guid.NewGuid();
        (string snapshot, _) = await repository.Snapshot(project, task, CancellationToken.None);
        string cache = temp.Combine("refuse.git");
        string work = temp.Combine("refuse-work");
        await repository.Receive(cache, await repository.Bundle(project, task, [], CancellationToken.None), task, snapshot, CancellationToken.None);
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
        await repository.Receive(cache, await repository.Bundle(project, task, [], CancellationToken.None), task, snapshot, CancellationToken.None);

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
        await repository.Receive(cache, await repository.Bundle(project, task, [], CancellationToken.None), task, snapshot, CancellationToken.None);
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
        await repository.Receive(cache, await repository.Bundle(project, task, [], CancellationToken.None), task, snapshot, CancellationToken.None);

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

    [Fact]
    public async Task Merge_BuildsTheBaseOfATaskOnTheChangesOfAnotherAndRefusesConflicts()
    {
        string project = await Project("merge");
        Guid task = Guid.NewGuid();
        (string snapshot, _) = await repository.Snapshot(project, task, CancellationToken.None);
        await Git(project, "checkout", "--quiet", "-b", "side");
        await File.WriteAllTextAsync(Path.Combine(project, "a.txt"), "one\ntwo\nTHREE\n");
        await Git(project, "commit", "--quiet", "-am", "side");
        string side = await Git(project, "rev-parse", "HEAD");
        await Git(project, "checkout", "--quiet", "main");

        string merged = await repository.Merge(project, snapshot, snapshot, side, CancellationToken.None);

        Assert.Contains("THREE", await Git(project, "show", $"{merged}:a.txt"));
        await File.WriteAllTextAsync(Path.Combine(project, "a.txt"), "one\ntwo\nother\n");
        await Git(project, "commit", "--quiet", "-am", "other");
        string other = await Git(project, "rev-parse", "HEAD");
        RequestException conflict = await Assert.ThrowsAsync<RequestException>(() => repository.Merge(project, other, snapshot, side, CancellationToken.None));
        Assert.Contains("conflict", conflict.Message);
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
