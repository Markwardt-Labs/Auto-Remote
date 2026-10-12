namespace Markwardt.AutoRemote.Tests.Integration;

[Collection("Git")]
public sealed class WorkspaceVersioningTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly Mock<IPromptService> prompts = new();

    private readonly Mock<IEventHub> events = new();

    private readonly GitService git = new();

    private readonly WorkspaceVersioning versioning;

    private readonly string remote;

    private readonly string work;

    private readonly WorkspaceInfo workspace;

    public WorkspaceVersioningTests()
    {
        Environment.SetEnvironmentVariable("GIT_AUTHOR_NAME", "Tester");
        Environment.SetEnvironmentVariable("GIT_AUTHOR_EMAIL", "tester@example.com");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", "Tester");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", "tester@example.com");
        versioning = new WorkspaceVersioning(git, prompts.Object, events.Object);
        remote = temp.Combine("origin.git");
        work = temp.Combine("work");
        Directory.CreateDirectory(remote);
        G(remote, "init", "--bare", "-b", "main");
        G(temp.Path, "clone", remote, "work");
        File.WriteAllText(Path.Combine(work, "a.txt"), "one\ntwo\nthree\n");
        G(work, "add", "-A");
        G(work, "commit", "-m", "base");
        G(work, "branch", "-M", "main");
        G(work, "push", "-u", "origin", "main");
        workspace = new WorkspaceInfo { Id = Guid.NewGuid(), Folder = work };
    }

    public void Dispose() => temp.Dispose();

    private string G(string folder, params string[] arguments)
    {
        ProcessStartInfo start = new("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output.Trim();
    }

    private bool Fails(string folder, params string[] arguments)
    {
        try
        {
            G(folder, arguments);
            return false;
        }
        catch (Xunit.Sdk.XunitException)
        {
            return true;
        }
    }

    private void Commit(string folder, string file, string text, string message)
    {
        File.WriteAllText(Path.Combine(folder, file), text);
        G(folder, "add", "-A");
        G(folder, "commit", "-m", message);
    }

    private string Branch(string name, string file, string text, string message)
    {
        G(work, "checkout", "-b", name);
        Commit(work, file, text, message);
        return G(work, "rev-parse", "HEAD");
    }

    private string Current => G(work, "rev-parse", "--abbrev-ref", "HEAD");

    private string Remembered(string branch) => G(remote, "rev-parse", branch);

    private void FakeAi(Action resolve)
    {
        prompts.Setup(x => x.IsBusy(workspace.Id)).Returns(false);
        prompts.Setup(x => x.Prompt(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            resolve();
            return Task.CompletedTask;
        });
    }

    private void Resolve(string file, string text)
    {
        File.WriteAllText(Path.Combine(work, file), text);
        G(work, "add", file);
    }

    private async Task<RequestException> Refused(Task action) => await Assert.ThrowsAsync<RequestException>(() => action);

    [Fact]
    public async Task Checkout_SwitchesToABranchDetachesAtATagOrCommitAndTracksARemoteOnlyBranch()
    {
        string featureTip = Branch("feature", "f.txt", "f", "feature work");
        G(work, "checkout", "main");
        G(work, "tag", "v1");
        G(work, "push", "origin", "feature");
        G(work, "branch", "-D", "feature");
        G(work, "fetch", "origin");

        await versioning.Checkout(workspace, "v1", false, CancellationToken.None);
        Assert.Equal("HEAD", Current);

        await versioning.Checkout(workspace, "feature", false, CancellationToken.None);
        Assert.Equal("feature", Current);
        Assert.Equal(featureTip, G(work, "rev-parse", "HEAD"));
        Assert.Equal("origin/feature", G(work, "rev-parse", "--abbrev-ref", "feature@{upstream}"));

        await versioning.Checkout(workspace, featureTip[..8], false, CancellationToken.None);
        Assert.Equal("HEAD", Current);
        await versioning.Checkout(workspace, "main", false, CancellationToken.None);
        Assert.Equal("main", Current);
    }

    [Fact]
    public async Task Checkout_RefusesToDiscardChangesUnlessToldTo()
    {
        Branch("feature", "f.txt", "f", "feature work");
        G(work, "checkout", "main");
        File.WriteAllText(Path.Combine(work, "a.txt"), "dirty");
        File.WriteAllText(Path.Combine(work, "new.txt"), "new");

        RequestException refused = await Refused(versioning.Checkout(workspace, "feature", false, CancellationToken.None));

        Assert.Contains("discard", refused.Message);
        Assert.Equal("main", Current);
        Assert.Equal("dirty", File.ReadAllText(Path.Combine(work, "a.txt")));

        await versioning.Checkout(workspace, "feature", true, CancellationToken.None);

        Assert.Equal("feature", Current);
        Assert.False(File.Exists(Path.Combine(work, "new.txt")));
        G(work, "checkout", "main");
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(Path.Combine(work, "a.txt")).Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData("nothing-here")]
    [InlineData("-b")]
    [InlineData("two words")]
    public async Task Checkout_RejectsUnknownOrDangerousNames(string name)
    {
        await Refused(versioning.Checkout(workspace, name, false, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteBranch_RemovesItLocallyAndOnTheRemoteButNotTheCheckedOutOne()
    {
        Branch("feature", "f.txt", "f", "feature work");
        G(work, "push", "origin", "feature");
        G(work, "checkout", "main");

        await versioning.DeleteBranch(workspace, "feature", CancellationToken.None);

        Assert.True(Fails(work, "rev-parse", "--verify", "refs/heads/feature"));
        Assert.True(Fails(remote, "rev-parse", "--verify", "refs/heads/feature"));
        await Refused(versioning.DeleteBranch(workspace, "main", CancellationToken.None));
        await Refused(versioning.DeleteBranch(workspace, "ghost", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteTag_RemovesItLocallyAndOnTheRemote()
    {
        G(work, "tag", "-a", "v1", "-m", "v1");
        G(work, "push", "origin", "v1");

        await versioning.DeleteTag(workspace, "v1", CancellationToken.None);

        Assert.True(Fails(work, "rev-parse", "--verify", "refs/tags/v1"));
        Assert.True(Fails(remote, "rev-parse", "--verify", "refs/tags/v1"));
        await Refused(versioning.DeleteTag(workspace, "v1", CancellationToken.None));
    }

    [Fact]
    public async Task BranchState_CountsCommitsAndReportsWhetherTheBranchIsBasedOnTheOther()
    {
        Branch("feature", "f.txt", "f", "first");
        Commit(work, "g.txt", "g", "second");
        G(work, "checkout", "main");

        GitBranchState based = await versioning.BranchState(workspace, "feature", "main", CancellationToken.None);
        Commit(work, "m.txt", "m", "on main");
        GitBranchState behind = await versioning.BranchState(workspace, "feature", "main", CancellationToken.None);
        GitBranchState nothing = await versioning.BranchState(workspace, "main", "feature", CancellationToken.None);

        Assert.Equal(2, based.Unique);
        Assert.True(based.IsBased);
        Assert.Equal("second", based.Subject);
        Assert.False(behind.IsBased);
        Assert.Equal(1, nothing.Unique);
        await Refused(versioning.BranchState(workspace, "ghost", "main", CancellationToken.None));
    }

    [Fact]
    public async Task Squash_CollapsesTheBranchIntoOneCommitAndForcePushesWithoutTouchingTheWorkingTree()
    {
        Branch("feature", "f.txt", "f", "first");
        Commit(work, "g.txt", "g", "second");
        G(work, "push", "origin", "feature");
        G(work, "checkout", "main");
        File.WriteAllText(Path.Combine(work, "a.txt"), "dirty");

        await versioning.Squash(workspace, "feature", "main", "both", CancellationToken.None);

        Assert.Equal("1", G(work, "rev-list", "--count", "main..feature"));
        Assert.Equal("both", G(work, "log", "-1", "--format=%s", "feature"));
        Assert.Equal(G(work, "rev-parse", "feature"), Remembered("feature"));
        Assert.Equal("f.txt\ng.txt", G(work, "diff", "--name-only", "main", "feature").Replace("\r", string.Empty));
        Assert.Equal("dirty", File.ReadAllText(Path.Combine(work, "a.txt")));
        Assert.Equal("main", Current);
    }

    [Fact]
    public async Task Squash_RefusesWhenThereIsNothingToSquashOrNoMessage()
    {
        Branch("feature", "f.txt", "f", "first");

        await Refused(versioning.Squash(workspace, "main", "feature", "msg", CancellationToken.None));
        await Refused(versioning.Squash(workspace, "feature", "main", " ", CancellationToken.None));
    }

    [Fact]
    public async Task Rebase_ReplaysTheBranchOntoAnotherAndLeavesTheOriginalBranchCheckedOut()
    {
        Branch("feature", "f.txt", "f", "feature work");
        G(work, "checkout", "main");
        Commit(work, "m.txt", "m", "main work");
        G(work, "push", "origin", "main");

        await versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None);

        Assert.Equal("main", Current);
        Assert.Equal("0", G(work, "rev-list", "--count", "feature..main"));
        Assert.Equal("1", G(work, "rev-list", "--count", "main..feature"));
        Assert.True(File.Exists(Path.Combine(work, "m.txt")) || G(work, "ls-tree", "--name-only", "feature").Contains("m.txt"));
    }

    [Fact]
    public async Task Rebase_SquashesFirstWhenAskedAndForcePushesTheBranch()
    {
        Branch("feature", "f.txt", "f", "first");
        Commit(work, "g.txt", "g", "second");
        G(work, "push", "origin", "feature");
        G(work, "checkout", "main");
        Commit(work, "m.txt", "m", "main work");

        await versioning.Rebase(workspace, "feature", "main", "squashed", CancellationToken.None);

        Assert.Equal("1", G(work, "rev-list", "--count", "main..feature"));
        Assert.Equal("squashed", G(work, "log", "-1", "--format=%s", "feature"));
        Assert.Equal(G(work, "rev-parse", "feature"), Remembered("feature"));
    }

    [Fact]
    public async Task Rebase_RefusesWithPendingChangesOrWhenAlreadyUpToDate()
    {
        Branch("feature", "f.txt", "f", "feature work");

        G(work, "checkout", "main");
        File.WriteAllText(Path.Combine(work, "a.txt"), "dirty");
        RequestException dirty = await Refused(versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None));
        Assert.Contains("clean working tree", dirty.Message);
        File.WriteAllText(Path.Combine(work, "a.txt"), "one\ntwo\nthree\n");
        G(work, "checkout", "--", "a.txt");
        G(work, "branch", "same");
        RequestException current = await Refused(versioning.Rebase(workspace, "same", "main", null, CancellationToken.None));
        Assert.Contains("up to date", current.Message);
    }

    [Fact]
    public async Task Rebase_HasTheAiResolveConflictsAndContinues()
    {
        Branch("feature", "a.txt", "one\nFEATURE\nthree\n", "feature edit");
        G(work, "checkout", "main");
        Commit(work, "a.txt", "one\nMAIN\nthree\n", "main edit");
        string instruction = string.Empty;
        FakeAi(() => Resolve("a.txt", "one\nBOTH\nthree\n"));
        prompts.Setup(x => x.Prompt(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((Guid _, string text, CancellationToken _) =>
        {
            instruction = text;
            Resolve("a.txt", "one\nBOTH\nthree\n");
            return Task.CompletedTask;
        });

        await versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None);

        Assert.Contains("merge conflicts in: a.txt", instruction);
        Assert.Equal("main", Current);
        Assert.Equal("one\nBOTH\nthree", G(work, "show", "feature:a.txt").Replace("\r", string.Empty));
        Assert.Equal("0", G(work, "rev-list", "--count", "feature..main"));
        Assert.Equal(string.Empty, G(work, "status", "--porcelain"));
    }

    [Fact]
    public async Task Rebase_AbortsAndRestoresEverythingWhenTheAiCannotResolveTheConflicts()
    {
        string featureTip = Branch("feature", "a.txt", "one\nFEATURE\nthree\n", "feature edit");
        G(work, "checkout", "main");
        Commit(work, "a.txt", "one\nMAIN\nthree\n", "main edit");
        FakeAi(() => { });

        RequestException failed = await Refused(versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None));

        Assert.Contains("Could not automatically resolve", failed.Message);
        Assert.Equal("main", Current);
        Assert.Equal(featureTip, G(work, "rev-parse", "feature"));
        Assert.Equal(string.Empty, G(work, "status", "--porcelain"));
        prompts.Verify(x => x.Prompt(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task Rebase_AbortsWhenTheAiCannotBeStarted()
    {
        string featureTip = Branch("feature", "a.txt", "one\nFEATURE\nthree\n", "feature edit");
        G(work, "checkout", "main");
        Commit(work, "a.txt", "one\nMAIN\nthree\n", "main edit");
        prompts.Setup(x => x.Prompt(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("Claude Code could not be started"));

        await Refused(versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None));

        Assert.Equal("main", Current);
        Assert.Equal(featureTip, G(work, "rev-parse", "feature"));
        Assert.Equal(string.Empty, G(work, "status", "--porcelain"));
    }

    [Fact]
    public async Task Merge_FastForwardsTheTargetPushesItAndDeletesTheMergedBranchEverywhere()
    {
        string tip = Branch("feature", "f.txt", "f", "feature work");
        G(work, "push", "origin", "feature");
        G(work, "checkout", "main");

        await versioning.Merge(workspace, "feature", "main", null, CancellationToken.None);

        Assert.Equal("main", Current);
        Assert.Equal(tip, G(work, "rev-parse", "main"));
        Assert.Equal(tip, Remembered("main"));
        Assert.True(Fails(work, "rev-parse", "--verify", "refs/heads/feature"));
        Assert.True(Fails(remote, "rev-parse", "--verify", "refs/heads/feature"));
    }

    [Fact]
    public async Task Merge_RebasesASourceThatFellBehindAndSquashesSeveralCommits()
    {
        Branch("feature", "f.txt", "f", "first");
        Commit(work, "g.txt", "g", "second");
        G(work, "checkout", "main");
        Commit(work, "m.txt", "m", "main work");

        await versioning.Merge(workspace, "feature", "main", "one commit", CancellationToken.None);

        Assert.Equal("one commit", G(work, "log", "-1", "--format=%s", "main"));
        Assert.Equal("main work", G(work, "log", "-2", "--format=%s", "main").Split('\n')[1].Trim());
        Assert.Equal("main", Current);
        Assert.Equal(G(work, "rev-parse", "main"), Remembered("main"));
    }

    [Fact]
    public async Task Merge_FromTheSourceBranchLeavesTheTargetCheckedOut()
    {
        string tip = Branch("feature", "f.txt", "f", "feature work");

        await versioning.Merge(workspace, "feature", "main", null, CancellationToken.None);

        Assert.Equal("main", Current);
        Assert.Equal(tip, G(work, "rev-parse", "main"));
    }

    [Fact]
    public async Task Merge_ResolvesConflictsWithTheAiAndRefusesNothingToMergeOrPendingChanges()
    {
        Branch("feature", "a.txt", "one\nFEATURE\nthree\n", "feature edit");
        G(work, "checkout", "main");
        Commit(work, "a.txt", "one\nMAIN\nthree\n", "main edit");
        FakeAi(() => Resolve("a.txt", "one\nBOTH\nthree\n"));

        await versioning.Merge(workspace, "feature", "main", null, CancellationToken.None);

        Assert.Equal("one\nBOTH\nthree", G(work, "show", "main:a.txt").Replace("\r", string.Empty));
        Assert.True(Fails(work, "rev-parse", "--verify", "refs/heads/feature"));
        await Refused(versioning.Merge(workspace, "main", "main", null, CancellationToken.None));
        File.WriteAllText(Path.Combine(work, "a.txt"), "dirty");
        await Refused(versioning.Merge(workspace, "main", "main", null, CancellationToken.None));
    }

    [Fact]
    public async Task Fetch_MovesLocalBranchesWithNothingOfTheirOwnToMatchTheRemote()
    {
        Branch("feature", "f.txt", "f", "feature work");
        G(work, "push", "origin", "feature");
        G(work, "checkout", "main");
        string other = temp.Combine("other");
        G(temp.Path, "clone", remote, "other");
        G(other, "checkout", "feature");
        Commit(other, "h.txt", "h", "from elsewhere");
        G(other, "push", "origin", "feature");

        await versioning.Fetch(workspace, CancellationToken.None);

        Assert.Equal(Remembered("feature"), G(work, "rev-parse", "feature"));
        Assert.Equal("main", Current);
    }

    [Fact]
    public async Task Fetch_KeepsALocalBranchThatHasUnpushedCommits()
    {
        Branch("feature", "f.txt", "f", "feature work");
        G(work, "push", "origin", "feature");
        Commit(work, "g.txt", "g", "local only");
        string local = G(work, "rev-parse", "HEAD");
        G(work, "checkout", "main");
        string other = temp.Combine("other");
        G(temp.Path, "clone", remote, "other");
        G(other, "checkout", "feature");
        Commit(other, "h.txt", "h", "from elsewhere");
        G(other, "push", "origin", "feature");

        await versioning.Fetch(workspace, CancellationToken.None);

        Assert.Equal(local, G(work, "rev-parse", "feature"));
    }

    [Fact]
    public async Task Fetch_DetachesAndDeletesTheCheckedOutBranchWhenItsRemoteCounterpartWasDeleted()
    {
        string tip = Branch("feature", "f.txt", "f", "feature work");
        G(work, "push", "-u", "origin", "feature");
        G(remote, "branch", "-D", "feature");

        await versioning.Fetch(workspace, CancellationToken.None);

        Assert.Equal("HEAD", Current);
        Assert.Equal(tip, G(work, "rev-parse", "HEAD"));
        Assert.True(Fails(work, "rev-parse", "--verify", "refs/heads/feature"));
    }

    [Fact]
    public async Task Sync_PullsNewRemoteCommitsIntoACleanCheckedOutBranch()
    {
        string other = temp.Combine("other");
        G(temp.Path, "clone", remote, "other");
        Commit(other, "n.txt", "n", "from elsewhere");
        G(other, "push", "origin", "main");

        await versioning.Sync(workspace, CancellationToken.None);

        Assert.Equal(G(other, "rev-parse", "HEAD"), G(work, "rev-parse", "HEAD"));
        Assert.True(File.Exists(Path.Combine(work, "n.txt")));
    }

    [Fact]
    public async Task Sync_StashesAndRestoresPendingChangesAroundThePull()
    {
        string other = temp.Combine("other");
        G(temp.Path, "clone", remote, "other");
        Commit(other, "n.txt", "n", "from elsewhere");
        G(other, "push", "origin", "main");
        File.WriteAllText(Path.Combine(work, "a.txt"), "one\ntwo\nthree\nmine\n");
        File.WriteAllText(Path.Combine(work, "untracked.txt"), "keep me");

        await versioning.Sync(workspace, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(work, "n.txt")));
        Assert.Equal("one\ntwo\nthree\nmine\n", File.ReadAllText(Path.Combine(work, "a.txt")).Replace("\r\n", "\n"));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(work, "untracked.txt")));
        Assert.Equal(string.Empty, G(work, "stash", "list"));
    }

    [Fact]
    public async Task Sync_HasTheAiResolveAStashConflictAndDropsTheStash()
    {
        string other = temp.Combine("other");
        G(temp.Path, "clone", remote, "other");
        Commit(other, "a.txt", "one\nREMOTE\nthree\n", "from elsewhere");
        G(other, "push", "origin", "main");
        File.WriteAllText(Path.Combine(work, "a.txt"), "one\nMINE\nthree\n");
        string instruction = string.Empty;
        prompts.Setup(x => x.IsBusy(workspace.Id)).Returns(false);
        prompts.Setup(x => x.Prompt(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((Guid _, string text, CancellationToken _) =>
        {
            instruction = text;
            Resolve("a.txt", "one\nREMOTE and MINE\nthree\n");
            return Task.CompletedTask;
        });

        await versioning.Sync(workspace, CancellationToken.None);

        Assert.Contains("stashed pending changes", instruction);
        Assert.Equal("one\nREMOTE and MINE\nthree\n", File.ReadAllText(Path.Combine(work, "a.txt")).Replace("\r\n", "\n"));
        Assert.Equal(string.Empty, G(work, "stash", "list"));
    }

    [Fact]
    public async Task Sync_KeepsTheStashWhenTheConflictCannotBeResolved()
    {
        string other = temp.Combine("other");
        G(temp.Path, "clone", remote, "other");
        Commit(other, "a.txt", "one\nREMOTE\nthree\n", "from elsewhere");
        G(other, "push", "origin", "main");
        File.WriteAllText(Path.Combine(work, "a.txt"), "one\nMINE\nthree\n");
        FakeAi(() => { });

        RequestException failed = await Refused(versioning.Sync(workspace, CancellationToken.None));

        Assert.Contains("stash", failed.Message);
        Assert.NotEqual(string.Empty, G(work, "stash", "list"));
    }

    [Fact]
    public async Task Sync_DoesNothingWhileTheAiWorksOrWithoutARemoteOrWhenDiverged()
    {
        string other = temp.Combine("other");
        G(temp.Path, "clone", remote, "other");
        Commit(other, "n.txt", "n", "from elsewhere");
        G(other, "push", "origin", "main");
        prompts.Setup(x => x.IsBusy(workspace.Id)).Returns(true);

        await versioning.Sync(workspace, CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(work, "n.txt")));

        prompts.Setup(x => x.IsBusy(workspace.Id)).Returns(false);
        Commit(work, "local.txt", "l", "local commit");
        await versioning.Sync(workspace, CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(work, "n.txt")));
        Assert.True(File.Exists(Path.Combine(work, "local.txt")));

        G(work, "remote", "remove", "origin");
        await versioning.Sync(workspace, CancellationToken.None);
        await versioning.Fetch(workspace, CancellationToken.None);
    }

    [Fact]
    public async Task Sync_OnlyTellsClientsWhenItChangedSomething_AndIsNotReportedAsBusyWhileRunning()
    {
        prompts.Setup(x => x.IsBusy(workspace.Id)).Returns(false);
        await versioning.Sync(workspace, CancellationToken.None);
        events.Verify(x => x.Publish(It.IsAny<RemoteEvent>()), Times.Never);

        string other = temp.Combine("other");
        G(temp.Path, "clone", remote, "other");
        Commit(other, "n.txt", "n", "from elsewhere");
        G(other, "push", "origin", "main");
        await versioning.Sync(workspace, CancellationToken.None);
        events.Verify(x => x.Publish(It.Is<RemoteEvent>(e => e.Kind == RemoteEventKind.GitChanged)), Times.Once);

        TaskCompletionSource release = new();
        TaskCompletionSource started = new();
        Task background = versioning.Perform(workspace, false, async _ =>
        {
            started.SetResult();
            await release.Task;
        }, CancellationToken.None, true);
        await started.Task;
        Assert.False(versioning.IsBusy(workspace.Id));
        await Refused(versioning.Perform(workspace, false, _ => Task.CompletedTask, CancellationToken.None));
        release.SetResult();
        await background;
    }

    [Fact]
    public async Task Perform_RunsOneActionAtATimeAndTellsClientsWhenItStartsAndEnds()
    {
        TaskCompletionSource release = new();
        TaskCompletionSource started = new();

        Task first = versioning.Perform(workspace, false, async _ =>
        {
            started.SetResult();
            await release.Task;
        }, CancellationToken.None);
        await started.Task;

        Assert.True(versioning.IsBusy(workspace.Id));
        RequestException second = await Refused(versioning.Perform(workspace, false, _ => Task.CompletedTask, CancellationToken.None));
        Assert.Contains("already running", second.Message);
        release.SetResult();
        await first;

        Assert.False(versioning.IsBusy(workspace.Id));
        events.Verify(x => x.Publish(It.Is<RemoteEvent>(e => e.Kind == RemoteEventKind.GitChanged && e.WorkspaceId == workspace.Id)), Times.Exactly(2));
    }

    [Fact]
    public async Task Cancel_UndoesWhatTheActionHadDoneAndReportsIt()
    {
        string tip = G(work, "rev-parse", "HEAD");
        TaskCompletionSource started = new();

        Task action = versioning.Perform(workspace, true, async token =>
        {
            Commit(work, "x.txt", "x", "half done");
            G(work, "checkout", "-b", "other");
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        }, CancellationToken.None);
        await started.Task;
        versioning.Cancel(workspace.Id);

        RequestException cancelled = await Refused(action);

        Assert.Contains("cancelled", cancelled.Message);
        Assert.Equal("main", Current);
        Assert.Equal(tip, G(work, "rev-parse", "HEAD"));
        Assert.False(File.Exists(Path.Combine(work, "x.txt")));
        Assert.False(versioning.IsBusy(workspace.Id));
    }

    [Fact]
    public async Task Cancel_KeepsPendingChangesThatExistedBeforeTheAction()
    {
        File.WriteAllText(Path.Combine(work, "a.txt"), "mine");
        TaskCompletionSource started = new();

        Task action = versioning.Perform(workspace, true, async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        }, CancellationToken.None);
        await started.Task;
        versioning.Cancel(workspace.Id);
        await Refused(action);

        Assert.Equal("mine", File.ReadAllText(Path.Combine(work, "a.txt")));
    }

    [Fact]
    public async Task Perform_UndoesAnUnexpectedFailureButNotARefusal()
    {
        string tip = G(work, "rev-parse", "HEAD");

        await Assert.ThrowsAsync<InvalidOperationException>(() => versioning.Perform(workspace, true, _ =>
        {
            Commit(work, "x.txt", "x", "half done");
            throw new InvalidOperationException("boom");
        }, CancellationToken.None));
        Assert.Equal(tip, G(work, "rev-parse", "HEAD"));

        await Refused(versioning.Perform(workspace, true, _ =>
        {
            Commit(work, "y.txt", "y", "kept");
            throw new RequestException("refused");
        }, CancellationToken.None));
        Assert.NotEqual(tip, G(work, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task Identity_IsReportedMissingUntilSetInTheGlobalConfiguration()
    {
        string globalConfig = temp.Combine("global.gitconfig");
        Dictionary<string, string?> saved = new[] { "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM", "GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0" }.ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name));

        try
        {
            foreach (string name in new[] { "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL" })
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", globalConfig);
            Environment.SetEnvironmentVariable("GIT_CONFIG_SYSTEM", "/dev/null");
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "user.useConfigOnly");
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "true");
            Assert.False((await git.Status(work, CancellationToken.None)).HasIdentity);

            await Assert.ThrowsAsync<RequestException>(() => git.SetIdentity(work, " ", "a@b.c", CancellationToken.None));
            await git.SetIdentity(work, "Ann Lee", "ann@example.com", CancellationToken.None);

            Assert.True((await git.Status(work, CancellationToken.None)).HasIdentity);
            Assert.Contains("Ann Lee", File.ReadAllText(globalConfig));
        }
        finally
        {
            foreach ((string name, string? value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [Fact]
    public async Task CreatingABranchOrTag_PushesItWhenThereIsARemote()
    {
        await git.CreateBranch(work, "feature", CancellationToken.None);
        await git.CreateTag(work, "v1", CancellationToken.None);

        Assert.Equal(G(work, "rev-parse", "HEAD"), Remembered("feature"));
        Assert.Equal("v1", G(remote, "tag", "--list", "v1"));
        Assert.Equal("origin/feature", G(work, "rev-parse", "--abbrev-ref", "feature@{upstream}"));
    }
}
