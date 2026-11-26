namespace Markwardt.AutoRemote.Tests.Integration;

// Each of these is a way a git workflow once destroyed work that was not the action's to destroy.
[Collection("Git")]
public sealed class WorkspaceVersioningSafetyTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly Mock<IPromptService> prompts = new();

    private readonly GitService git = new();

    private readonly EventHub events = new();

    private readonly WorkspaceActivity activity;

    private readonly WorkspaceVersioning versioning;

    private readonly string remote;

    private readonly string work;

    private readonly string other;

    private readonly WorkspaceInfo workspace;

    public WorkspaceVersioningSafetyTests()
    {
        Environment.SetEnvironmentVariable("GIT_AUTHOR_NAME", "Tester");
        Environment.SetEnvironmentVariable("GIT_AUTHOR_EMAIL", "tester@example.com");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", "Tester");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", "tester@example.com");
        activity = new WorkspaceActivity(events);
        versioning = new WorkspaceVersioning(git, prompts.Object, events, activity);
        remote = temp.Combine("origin.git");
        work = temp.Combine("work");
        other = temp.Combine("other");
        Directory.CreateDirectory(remote);
        G(remote, "init", "--bare", "-b", "main");
        G(temp.Path, "clone", remote, "work");
        Commit(work, "a.txt", "one\n", "base");
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

    private int Try(string folder, params string[] arguments)
    {
        ProcessStartInfo start = new("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    private void Write(string folder, string file, string text) => File.WriteAllText(Path.Combine(folder, file), text);

    private string Read(string file) => File.ReadAllText(Path.Combine(work, file));

    private string Commit(string folder, string file, string text, string message)
    {
        Write(folder, file, text);
        G(folder, "add", "-A");
        G(folder, "commit", "-m", message);
        return G(folder, "rev-parse", "HEAD");
    }

    private string Tip(string folder, string name) => G(folder, "rev-parse", name);

    private string Current() => G(work, "rev-parse", "--abbrev-ref", "HEAD");

    // A branch that conflicts with main in a.txt, pushed, with main checked out.
    private string Conflicting(string name)
    {
        G(work, "checkout", "-b", name);
        string tip = Commit(work, "a.txt", "from the branch\n", "branch work");
        G(work, "push", "origin", name);
        G(work, "checkout", "main");
        Commit(work, "a.txt", "from main\n", "main work");
        return tip;
    }

    // Someone else pushes a commit to a branch, which this clone has not seen.
    private string Teammate(string branch)
    {
        G(temp.Path, "clone", remote, "other");
        G(other, "checkout", branch);
        string tip = Commit(other, "theirs.txt", "theirs\n", "teammate work");
        G(other, "push", "origin", branch);
        return tip;
    }

    // The AI never resolves anything and stays busy until the action is cancelled.
    private TaskCompletionSource Hanging()
    {
        TaskCompletionSource asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool busy = false;
        prompts.Setup(x => x.IsBusy(workspace.Id)).Returns(() => busy);
        prompts.Setup(x => x.Prompt(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            busy = true;
            asked.TrySetResult();
            return Task.CompletedTask;
        });
        return asked;
    }

    private async Task CancelDuringTheConflict(Func<Task> action, Action? meanwhile = null)
    {
        TaskCompletionSource asked = Hanging();
        Task running = action();
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(20));
        meanwhile?.Invoke();
        await versioning.Cancel(workspace.Id);
        await Assert.ThrowsAnyAsync<Exception>(() => running);
    }

    [Fact]
    public async Task CancellingARebaseStartedOnADetachedHeadMovesNoBranch()
    {
        G(work, "tag", "v1");
        string feature = Conflicting("feature");
        G(work, "checkout", "--detach", "v1");
        string head = Tip(work, "HEAD");

        await CancelDuringTheConflict(() => versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None));

        Assert.Equal(feature, Tip(work, "feature"));
        Assert.Equal(("HEAD", head), (Current(), Tip(work, "HEAD")));
    }

    [Fact]
    public async Task CancellingKeepsIgnoredFilesAndFilesMadeMeanwhile()
    {
        G(work, "checkout", "-b", "old");
        Commit(work, "a.txt", "from the branch\n", "old work");
        G(work, "checkout", "main");
        Commit(work, ".gitignore", ".env\n", "ignore secrets");
        Commit(work, "a.txt", "from main\n", "main work");
        Write(work, ".env", "SECRET");

        await CancelDuringTheConflict(() => versioning.Rebase(workspace, "old", "main", null, CancellationToken.None), () => Write(work, "made-meanwhile.txt", "keep me"));

        Assert.Equal("main", Current());
        Assert.Equal(("SECRET", "keep me", "from main\n"), (Read(".env"), Read("made-meanwhile.txt"), Read("a.txt")));
    }

    [Fact]
    public async Task CancellingLeavesBranchesTheActionDidNotRewriteWhereOthersMovedThem()
    {
        Conflicting("feature");
        G(work, "branch", "unrelated");
        string moved = string.Empty;

        await CancelDuringTheConflict(() => versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None), () =>
        {
            moved = G(work, "commit-tree", "main^{tree}", "-p", "main", "-m", "someone else");
            G(work, "update-ref", "refs/heads/unrelated", moved);
        });

        Assert.Equal(moved, Tip(work, "unrelated"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RewritingABranchNeverErasesCommitsSomeoneElsePushedToIt(bool rebase)
    {
        G(work, "checkout", "-b", "feature");
        Commit(work, "f.txt", "1\n", "one");
        string local = Commit(work, "f.txt", "2\n", "two");
        G(work, "push", "origin", "feature");
        G(work, "checkout", "main");
        Commit(work, "m.txt", "m\n", "main work");
        string theirs = Teammate("feature");

        RequestException refused = await Assert.ThrowsAsync<RequestException>(() => rebase
            ? versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None)
            : versioning.Squash(workspace, "feature", "main", "squashed", CancellationToken.None));

        Assert.Contains("would erase", refused.Message);
        Assert.Equal(theirs, Tip(remote, "feature"));
        Assert.Equal(local, Tip(work, "feature"));
    }

    [Fact]
    public async Task ARewriteWhosePushFailedCanBeRewrittenAndPushedAgain()
    {
        G(work, "checkout", "-b", "feature");
        Commit(work, "f.txt", "1\n", "one");
        Commit(work, "f.txt", "2\n", "two");
        G(work, "push", "origin", "feature");
        G(work, "checkout", "main");
        string hook = Path.Combine(remote, "hooks", "pre-receive");
        File.WriteAllText(hook, "#!/bin/sh\nexit 1\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await Assert.ThrowsAsync<RequestException>(() => versioning.Squash(workspace, "feature", "main", "squashed", CancellationToken.None));

        File.Delete(hook);
        G(work, "checkout", "feature");
        Commit(work, "f.txt", "3\n", "three");
        G(work, "checkout", "main");
        await versioning.Squash(workspace, "feature", "main", "squashed again", CancellationToken.None);

        Assert.Equal(Tip(work, "feature"), Tip(remote, "feature"));
        Assert.Equal(string.Empty, G(work, "for-each-ref", "refs/auto-remote/unpushed"));
    }

    [Fact]
    public async Task ACommitPushedFromHereAndAmendedSinceMayBeReplaced()
    {
        G(work, "checkout", "-b", "feature");
        Commit(work, "f.txt", "1\n", "one");
        Commit(work, "f.txt", "2\n", "two");
        G(work, "push", "origin", "feature");
        G(work, "commit", "--amend", "-m", "two, amended");
        G(work, "checkout", "main");

        await versioning.Squash(workspace, "feature", "main", "squashed", CancellationToken.None);

        Assert.Equal(Tip(work, "feature"), Tip(remote, "feature"));
    }

    [Fact]
    public async Task ACommitTheBranchOnceHadAndDroppedAgainIsStillSomeoneElsesToErase()
    {
        G(work, "checkout", "-b", "feature");
        Commit(work, "f.txt", "1\n", "one");
        Commit(work, "f.txt", "2\n", "two");
        G(work, "push", "origin", "feature");
        string theirs = Teammate("feature");
        G(work, "pull", "--ff-only", "origin", "feature");
        G(work, "reset", "--hard", "HEAD~1");
        G(work, "checkout", "main");

        await Assert.ThrowsAsync<RequestException>(() => versioning.Squash(workspace, "feature", "main", "squashed", CancellationToken.None));

        Assert.Equal(theirs, Tip(remote, "feature"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABranchWithTheNameOfATagIsTheOneThatIsMerged(bool squash)
    {
        G(work, "tag", "v1");
        Commit(work, "m.txt", "main\n", "main moved on");
        G(work, "push", "origin", "main");
        G(work, "checkout", "-b", "v1");
        Commit(work, "f.txt", "1\n", "one");
        Commit(work, "g.txt", "2\n", "two");
        G(work, "push", "origin", "refs/heads/v1:refs/heads/v1");
        G(work, "checkout", "main");

        await versioning.Merge(workspace, "v1", "main", squash ? "merged v1" : null, CancellationToken.None);

        Assert.Equal(("1\n", "2\n", "main\n"), (Read("f.txt"), Read("g.txt"), Read("m.txt")));
        Assert.Equal(Tip(work, "refs/heads/main"), Tip(remote, "refs/heads/main"));
    }

    [Theory]
    [InlineData("squash")]
    [InlineData("rebase")]
    [InlineData("merge")]
    [InlineData("checkout")]
    public async Task NothingIsDoneWhileAGitOperationOfTheUserIsPaused(string action)
    {
        string feature = Conflicting("feature");
        Assert.NotEqual(0, Try(work, "merge", "feature"));
        Write(work, "a.txt", "half resolved by hand\n");
        G(work, "add", "a.txt");

        RequestException refused = await Assert.ThrowsAsync<RequestException>(() => action switch
        {
            "squash" => versioning.Squash(workspace, "feature", "main", "m", CancellationToken.None),
            "rebase" => versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None),
            "merge" => versioning.Merge(workspace, "feature", "main", null, CancellationToken.None),
            _ => versioning.Checkout(workspace, "feature", true, CancellationToken.None)
        });

        Assert.Contains("merge is in progress", refused.Message);
        Assert.True(File.Exists(Path.Combine(work, ".git", "MERGE_HEAD")));
        Assert.Equal(("half resolved by hand\n", feature), (Read("a.txt"), Tip(work, "feature")));

        await versioning.Sync(workspace, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(work, ".git", "MERGE_HEAD")));
    }

    [Fact]
    public async Task AResolutionThatEmptiesACommitChangesNothing()
    {
        string feature = Conflicting("feature");
        string main = Tip(work, "main");
        prompts.Setup(x => x.IsBusy(workspace.Id)).Returns(false);
        prompts.Setup(x => x.Prompt(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            // During a rebase, "ours" is the branch rebased onto, so this throws the work away.
            G(work, "checkout", "--ours", "a.txt");
            G(work, "add", "a.txt");
            return Task.CompletedTask;
        });

        RequestException refused = await Assert.ThrowsAsync<RequestException>(() => versioning.Merge(workspace, "feature", "main", null, CancellationToken.None));

        Assert.Contains("dropped a commit", refused.Message);
        Assert.Equal((feature, feature, main), (Tip(work, "feature"), Tip(remote, "feature"), Tip(work, "main")));
    }

    [Fact]
    public async Task AbortingSavesWhatWasChangedSinceGitStoppedAtTheConflict()
    {
        Commit(work, "b.txt", "bee\n", "add b");
        Conflicting("feature");

        await CancelDuringTheConflict(() => versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None), () =>
        {
            Write(work, "b.txt", "edited meanwhile\n");
            Write(work, "new.txt", "staged meanwhile\n");
            G(work, "add", "new.txt");
        });

        Assert.Equal(("main", "bee\n"), (Current(), Read("b.txt")));
        Assert.Equal("edited meanwhile", G(work, "show", "stash@{0}:b.txt"));
        Assert.Equal("staged meanwhile", G(work, "show", "stash@{0}:new.txt"));
    }

    [Fact]
    public async Task ACancelWithNothingChangedSinceTheConflictLeavesNoStash()
    {
        Conflicting("feature");

        await CancelDuringTheConflict(() => versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None));

        Assert.Equal(string.Empty, G(work, "stash", "list"));
    }

    [Fact]
    public async Task AnAbortThatAFileIsInTheWayOfLeavesTheRebaseInProgressRatherThanDroppingItsState()
    {
        G(work, "checkout", "-b", "feature");
        Commit(work, "a.txt", "from the branch\n", "one");
        Commit(work, "later.txt", "added later\n", "two");
        G(work, "checkout", "main");
        Commit(work, "a.txt", "from main\n", "main work");

        TaskCompletionSource asked = Hanging();
        Task running = versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Write(work, "later.txt", "made by a script\n");
        await versioning.Cancel(workspace.Id);
        RequestException cancelled = await Assert.ThrowsAsync<RequestException>(() => running);

        bool inProgress = Directory.Exists(Path.Combine(work, ".git", "rebase-merge"));
        Assert.Equal(inProgress, cancelled.Message.Contains("could not be undone", StringComparison.Ordinal));
        Assert.True(inProgress ? Read("later.txt") == "made by a script\n" : G(work, "stash", "list").Length > 0);
    }

    [Fact]
    public async Task UndoingLeavesATargetBranchSomeoneElseMovedBeforeTheMergeReachedIt()
    {
        Conflicting("feature");
        G(work, "branch", "release", "main");
        G(work, "push", "origin", "release");
        string moved = string.Empty;

        await CancelDuringTheConflict(() => versioning.Merge(workspace, "feature", "release", null, CancellationToken.None), () =>
        {
            moved = G(work, "commit-tree", "release^{tree}", "-p", "release", "-m", "someone else");
            G(work, "update-ref", "refs/heads/release", moved);
        });

        Assert.Equal(moved, Tip(work, "release"));
    }

    [Fact]
    public async Task ARebaseFromADetachedHeadReturnsThereAndAMergeRefusesToLeaveCommitsBehind()
    {
        G(work, "checkout", "-b", "feature");
        Commit(work, "f.txt", "1\n", "one");
        G(work, "checkout", "main");
        Commit(work, "m.txt", "m\n", "main work");
        G(work, "checkout", "--detach");
        string detached = Commit(work, "d.txt", "d\n", "made on a detached head");

        await Assert.ThrowsAsync<RequestException>(() => versioning.Merge(workspace, "feature", "main", null, CancellationToken.None));
        await versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None);

        Assert.Equal(("HEAD", detached), (Current(), Tip(work, "HEAD")));
    }

    [Fact]
    public async Task ARebaseNeverMovesOtherBranchesWhateverTheUsersGitSettingsSay()
    {
        G(work, "config", "rebase.updateRefs", "true");
        G(work, "checkout", "-b", "part1");
        string first = Commit(work, "f.txt", "1\n", "one");
        G(work, "checkout", "-b", "part2");
        Commit(work, "g.txt", "2\n", "two");
        G(work, "checkout", "main");
        Commit(work, "m.txt", "m\n", "main work");

        await versioning.Rebase(workspace, "part2", "main", null, CancellationToken.None);

        Assert.Equal(first, Tip(work, "part1"));
    }

    [Theory]
    [InlineData("file under an ignored folder name")]
    [InlineData("ignored file under a file name")]
    [InlineData("file only a replayed commit has")]
    public async Task ARebaseThatWouldReplaceIgnoredFilesInAnyWayIsRefused(string kind)
    {
        G(work, "checkout", "-b", "feature");

        if (kind == "file only a replayed commit has")
        {
            Commit(work, "local.env", "committed by mistake\n", "add env");
            G(work, "rm", "local.env");
            G(work, "commit", "-m", "remove env");
        }
        else
        {
            Commit(work, "f.txt", "1\n", "one");
        }

        G(work, "checkout", "main");

        if (kind == "file under an ignored folder name")
        {
            Directory.CreateDirectory(Path.Combine(work, "data"));
            Commit(work, "data/x.txt", "x\n", "track data folder");
        }
        else if (kind == "ignored file under a file name")
        {
            Commit(work, "data", "now a file\n", "track data file");
        }
        else
        {
            Commit(work, "m.txt", "m\n", "main work");
        }

        G(work, "checkout", "feature");
        string ignored = kind switch { "file under an ignored folder name" => "data", "ignored file under a file name" => "data/secret", _ => "local.env" };
        File.WriteAllText(Path.Combine(work, ".git", "info", "exclude"), "data\nlocal.env\n");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(work, ignored))!);
        Write(work, ignored, "MINE\n");
        string tip = Tip(work, "feature");

        await Assert.ThrowsAsync<RequestException>(() => versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None));

        Assert.Equal(("MINE\n", tip), (Read(ignored), Tip(work, "feature")));
    }

    [Fact]
    public async Task SyncLeavesTheTreeAloneWhenStashingWouldReplaceAnIgnoredFile()
    {
        Directory.CreateDirectory(Path.Combine(work, "folder"));
        Commit(work, "folder/x.txt", "x\n", "add folder");
        G(work, "push", "origin", "main");
        G(temp.Path, "clone", remote, "other");
        Commit(other, "n.txt", "new\n", "newer");
        G(other, "push", "origin", "main");
        Directory.Delete(Path.Combine(work, "folder"), true);
        File.WriteAllText(Path.Combine(work, ".git", "info", "exclude"), "/folder\n");
        Write(work, "folder", "MY IGNORED FILE\n");

        await versioning.Sync(workspace, CancellationToken.None);

        Assert.Equal("MY IGNORED FILE\n", Read("folder"));
    }

    [Fact]
    public async Task SyncKeepsStagedContentThatItCannotStageAgainAndEditsOfFilesGitWasToldNotToWatch()
    {
        G(temp.Path, "clone", remote, "other");
        Commit(other, "n.txt", "new\n", "newer");
        G(other, "push", "origin", "main");
        Write(work, "a.txt", "staged version\n");
        G(work, "add", "a.txt");
        Write(work, "a.txt", "work tree version\n");

        await versioning.Sync(workspace, CancellationToken.None);

        Assert.Equal(("staged version", "work tree version\n", "new\n"), (G(work, "show", ":a.txt"), Read("a.txt"), Read("n.txt")));

        Commit(other, "o.txt", "newest\n", "newest");
        G(other, "push", "origin", "main");
        G(work, "reset", "--hard");
        G(work, "update-index", "--assume-unchanged", "a.txt");
        Write(work, "a.txt", "local override\n");
        Write(work, "pending.txt", "pending\n");

        await versioning.Sync(workspace, CancellationToken.None);

        Assert.Equal("local override\n", Read("a.txt"));
    }

    [Fact]
    public async Task SyncKeepsEverythingWhenTheAiMakesTheConflictsOfTheRestoreGoAwayByDiscarding()
    {
        G(temp.Path, "clone", remote, "other");
        Commit(other, "a.txt", "theirs\n", "newer");
        G(other, "push", "origin", "main");
        Write(work, "a.txt", "MY PENDING WORK\n");
        prompts.Setup(x => x.IsBusy(workspace.Id)).Returns(false);
        prompts.Setup(x => x.Prompt(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            G(work, "reset", "--hard");
            return Task.CompletedTask;
        });

        await versioning.Sync(workspace, CancellationToken.None);

        Assert.Equal("MY PENDING WORK", G(work, "show", "stash@{0}:a.txt"));
    }

    [Fact]
    public async Task SyncNeverMovesABranchPastACommitMadeDuringItsFetch()
    {
        G(work, "branch", "feature");
        G(work, "push", "origin", "feature");
        string made = string.Empty;
        Mock<IGitService> slow = new();
        slow.Setup(x => x.Run(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>())).Returns(async (string folder, IReadOnlyList<string> arguments, CancellationToken cancellation, TimeSpan? limit) =>
        {
            GitOutput result = await git.Run(folder, arguments, cancellation, limit);

            if (arguments[0] == "fetch")
            {
                made = G(work, "commit-tree", "feature^{tree}", "-p", "feature", "-m", "made during the fetch");
                G(work, "update-ref", "refs/heads/feature", made);
            }

            return result;
        });

        await new WorkspaceVersioning(slow.Object, prompts.Object, events, activity).Sync(workspace, CancellationToken.None);

        Assert.Equal(made, Tip(work, "feature"));
    }

    // The remote refuses every push until the returned hook is deleted.
    private string RefusePushes()
    {
        string hook = Path.Combine(remote, "hooks", "pre-receive");
        File.WriteAllText(hook, "#!/bin/sh\nexit 1\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return hook;
    }

    private void Resolving(Action resolve)
    {
        prompts.Setup(x => x.IsBusy(workspace.Id)).Returns(false);
        prompts.Setup(x => x.Prompt(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            resolve();
            return Task.CompletedTask;
        });
    }

    private string Backups(string commit) => G(work, "for-each-ref", "--contains", commit, "refs/auto-remote/backup");

    [Fact]
    public async Task PushedCommitsThatWereResetAwayAreNotErasedByARewrite()
    {
        G(work, "checkout", "-b", "feature");
        Commit(work, "f.txt", "1\n", "one");
        Commit(work, "g.txt", "2\n", "two");
        string pushed = Commit(work, "h.txt", "3\n", "three");
        G(work, "push", "origin", "feature");
        G(work, "reset", "--hard", "HEAD~2");
        Commit(work, "i.txt", "4\n", "four");
        G(work, "checkout", "main");

        RequestException refused = await Assert.ThrowsAsync<RequestException>(() => versioning.Squash(workspace, "feature", "main", "squashed", CancellationToken.None));

        Assert.Contains("would erase", refused.Message);
        Assert.Equal(pushed, Tip(remote, "feature"));
    }

    [Fact]
    public async Task ARewriteThatWasNeverPushedStopsExcusingTheRemoteOnceTheBranchLeavesIt()
    {
        G(work, "checkout", "-b", "feature");
        Commit(work, "f.txt", "1\n", "one");
        string pushed = Commit(work, "g.txt", "2\n", "two");
        G(work, "push", "origin", "feature");
        G(work, "checkout", "main");
        string hook = RefusePushes();
        await Assert.ThrowsAsync<RequestException>(() => versioning.Squash(workspace, "feature", "main", "squashed", CancellationToken.None));
        File.Delete(hook);
        G(work, "checkout", "feature");
        G(work, "reset", "--hard", "main");
        Commit(work, "i.txt", "4\n", "four");
        Commit(work, "j.txt", "5\n", "five");
        G(work, "checkout", "main");

        await Assert.ThrowsAsync<RequestException>(() => versioning.Squash(workspace, "feature", "main", "squashed again", CancellationToken.None));

        Assert.Equal(pushed, Tip(remote, "feature"));
    }

    [Fact]
    public async Task CommitsRebasedByHandMayBeReplacedAndWhatAForcePushReplacesIsKept()
    {
        G(work, "checkout", "-b", "feature");
        Commit(work, "f.txt", "1\n", "one");
        string pushed = Commit(work, "g.txt", "2\n", "two");
        G(work, "push", "origin", "feature");
        G(work, "checkout", "main");
        Commit(work, "m.txt", "m\n", "main work");
        G(work, "rebase", "main", "feature");
        G(work, "checkout", "main");

        await versioning.Squash(workspace, "feature", "main", "squashed", CancellationToken.None);

        Assert.Equal(Tip(work, "feature"), Tip(remote, "feature"));
        Assert.NotEqual(string.Empty, Backups(pushed));
    }

    [Fact]
    public async Task MarkersOfABranchDoNotStandInTheWayOfOneNestedUnderItsName()
    {
        G(work, "checkout", "-b", "feat");
        Commit(work, "f.txt", "1\n", "one");
        Commit(work, "g.txt", "2\n", "two");
        G(work, "push", "origin", "feat");
        G(work, "checkout", "main");
        string hook = RefusePushes();
        await Assert.ThrowsAsync<RequestException>(() => versioning.Squash(workspace, "feat", "main", "squashed", CancellationToken.None));
        File.Delete(hook);

        await versioning.DeleteBranch(workspace, "feat", CancellationToken.None);

        Assert.Equal(string.Empty, G(work, "for-each-ref", "refs/auto-remote/unpushed"));
    }

    [Fact]
    public async Task CancellingNeverWritesOverAnIgnoredFileMadeDuringTheConflict()
    {
        Commit(work, "local.cfg", "shared settings\n", "track settings");
        G(work, "checkout", "-b", "feature");
        Commit(work, "a.txt", "from the branch\n", "branch work");
        G(work, "checkout", "main");
        G(work, "rm", "local.cfg");
        Commit(work, ".gitignore", "local.cfg\n", "stop tracking settings");
        Commit(work, "a.txt", "from main\n", "main work");

        await CancelDuringTheConflict(() => versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None), () => Write(work, "local.cfg", "MINE\n"));

        Assert.Equal("MINE\n", Read("local.cfg"));
        Assert.True(Directory.Exists(Path.Combine(work, ".git", "rebase-merge")));
    }

    [Fact]
    public async Task CancellingKeepsACommitMadeWhileTheRebaseWasPaused()
    {
        Conflicting("feature");
        string made = string.Empty;

        await CancelDuringTheConflict(() => versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None), () =>
        {
            Write(work, "a.txt", "resolved and committed\n");
            G(work, "add", "a.txt");
            G(work, "commit", "-m", "made during the conflict");
            made = Tip(work, "HEAD");
        });

        Assert.NotEqual(string.Empty, Backups(made));
    }

    [Fact]
    public async Task WhatAnAbortPutsInTheStashAppliesWhileUntrackedFilesAreStillThere()
    {
        Commit(work, "b.txt", "bee\n", "add b");
        Conflicting("feature");

        await CancelDuringTheConflict(() => versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None), () =>
        {
            Write(work, "b.txt", "edited meanwhile\n");
            Write(work, "untracked.txt", "left where it is\n");
        });

        G(work, "stash", "apply");

        Assert.Equal(("edited meanwhile\n", "left where it is\n"), (Read("b.txt"), Read("untracked.txt")));
    }

    [Fact]
    public async Task ACommitThatBecomesEmptyBecauseItsChangeIsThereAlreadyDoesNotRefuseAGoodResolution()
    {
        G(work, "checkout", "-b", "feature");
        Commit(work, "f.txt", "x\n", "add f");
        Commit(work, "a.txt", "from the branch\n", "branch work");
        G(work, "checkout", "main");
        Write(work, "m.txt", "m\n");
        Commit(work, "f.txt", "x\n", "add f and m");
        Commit(work, "a.txt", "from main\n", "main work");
        Resolving(() =>
        {
            Write(work, "a.txt", "both\n");
            G(work, "add", "a.txt");
        });

        await versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None);

        Assert.Equal("both", G(work, "show", "feature:a.txt"));
        Assert.Equal("1", G(work, "rev-list", "--count", "main..feature"));
    }

    [Fact]
    public async Task AnAiThatContinuesTheRebaseItselfAndKeepsEveryCommitIsAccepted()
    {
        Conflicting("feature");
        Resolving(() =>
        {
            Write(work, "a.txt", "both\n");
            G(work, "add", "a.txt");
            G(work, "-c", "core.editor=true", "rebase", "--continue");
        });

        await versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None);

        Assert.Equal("both", G(work, "show", "feature:a.txt"));
        Assert.Equal(Tip(work, "feature"), Tip(remote, "feature"));
    }

    [Fact]
    public async Task AnAiThatSkipsTheConflictingCommitChangesNothing()
    {
        string feature = Conflicting("feature");
        Resolving(() => G(work, "rebase", "--skip"));

        RequestException refused = await Assert.ThrowsAsync<RequestException>(() => versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None));

        Assert.Contains("dropped a commit", refused.Message);
        Assert.Equal(feature, Tip(work, "feature"));
    }

    [Fact]
    public async Task SyncKeepsNoBackupOfABranchItOnlyMovesForward()
    {
        G(work, "branch", "feature");
        G(work, "push", "origin", "feature");
        string theirs = Teammate("feature");

        await versioning.Sync(workspace, CancellationToken.None);

        Assert.Equal(theirs, Tip(work, "feature"));
        Assert.Equal(string.Empty, G(work, "for-each-ref", "refs/auto-remote/backup"));
    }

    [Fact]
    public async Task CheckingOutWhileDiscardingGetsPastConflictsLeftByAStashAndKeepsThem()
    {
        G(work, "branch", "elsewhere");
        Write(work, "a.txt", "stashed\n");
        G(work, "stash", "push");
        Commit(work, "a.txt", "committed\n", "moved on");
        Assert.NotEqual(0, Try(work, "stash", "apply"));
        string conflicted = Read("a.txt");

        await versioning.Checkout(workspace, "elsewhere", true, CancellationToken.None);

        Assert.Equal(("elsewhere", "one\n"), (Current(), Read("a.txt")));
        Assert.Equal(conflicted.Trim(), G(work, "show", "stash@{0}:a.txt"));
    }

    [Fact]
    public async Task SyncTellsTheUserWhenItLeavesTheirChangesInTheStash()
    {
        List<string> told = [];
        using IDisposable watch = events.Subscribe(remoteEvent =>
        {
            if (remoteEvent.Kind == RemoteEventKind.Failed)
            {
                told.Add(remoteEvent.Error!);
            }
        });
        G(temp.Path, "clone", remote, "other");
        Commit(other, "a.txt", "theirs\n", "newer");
        G(other, "push", "origin", "main");
        Write(work, "a.txt", "mine\n");

        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(work, "link"), "a.txt");
        }

        Resolving(() =>
        {
            Write(work, "a.txt", "resolved\n");
            G(work, "add", "a.txt");
        });

        await versioning.Sync(workspace, CancellationToken.None);

        Assert.Contains("a copy of them is kept in the stash", Assert.Single(told));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NothingElseIsCheckedOutWhileAnAbortIsRefused(bool merge)
    {
        G(work, "checkout", "-b", "feature");
        Commit(work, "a.txt", "from the branch\n", "one");
        Commit(work, "later.txt", "added later\n", "two");
        G(work, "checkout", "main");
        Commit(work, "a.txt", "from main\n", "main work");
        string main = Tip(work, "main");
        Resolving(() =>
        {
            Write(work, "a.txt", "resolved\n");
            G(work, "add", "a.txt");
            Write(work, "later.txt", "made by a script\n");
        });

        await Assert.ThrowsAsync<RequestException>(() => merge
            ? versioning.Merge(workspace, "feature", "main", null, CancellationToken.None)
            : versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None));

        Assert.True(Directory.Exists(Path.Combine(work, ".git", "rebase-merge")));
        Assert.Equal(("HEAD", main, "made by a script\n"), (Current(), Tip(work, "main"), Read("later.txt")));
    }

    [Fact]
    public async Task ARewriteNeverReplacesACommitThatDiffersOnlyInWhiteSpace()
    {
        Commit(work, "code.py", "def f(x):\n    if x:\n        pass\n", "add code");
        G(work, "push", "origin", "main");
        G(work, "checkout", "-b", "feature");
        Commit(work, "f.txt", "1\n", "one");
        G(work, "push", "origin", "feature");
        G(temp.Path, "clone", remote, "other");
        G(other, "checkout", "feature");
        string theirs = Commit(other, "code.py", "def f(x):\n    if x:\n        pass\n    return 1\n", "return");
        G(other, "push", "origin", "feature");
        Commit(work, "code.py", "def f(x):\n    if x:\n        pass\n        return 1\n", "return");
        G(work, "checkout", "main");

        await Assert.ThrowsAsync<RequestException>(() => versioning.Squash(workspace, "feature", "main", "squashed", CancellationToken.None));

        Assert.Equal(theirs, Tip(remote, "feature"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscardingNeverReplacesAFileGitDoesNotTrack(bool conflicts)
    {
        Commit(work, "keep.cfg", "committed\n", "add settings");
        G(work, "branch", "elsewhere");

        if (conflicts)
        {
            Write(work, "a.txt", "stashed\n");
            G(work, "stash", "push");
            Commit(work, "a.txt", "newer\n", "moved on");
            Assert.NotEqual(0, Try(work, "stash", "apply"));
        }

        G(work, "rm", "--cached", "keep.cfg");
        File.WriteAllText(Path.Combine(work, ".git", "info", "exclude"), "keep.cfg\n");
        Write(work, "keep.cfg", "MINE\n");
        string unresolved = Read("a.txt");

        RequestException refused = await Assert.ThrowsAsync<RequestException>(() => versioning.Checkout(workspace, "elsewhere", true, CancellationToken.None));

        Assert.Contains("keep.cfg", refused.Message);
        Assert.Equal(("main", "MINE\n", unresolved), (Current(), Read("keep.cfg"), Read("a.txt")));
    }

    [Fact]
    public async Task DiscardingConflictsResetsNothingWhenAFileWouldStopTheCheckout()
    {
        G(work, "checkout", "-b", "elsewhere");
        Commit(work, "blocker.txt", "tracked there\n", "add blocker");
        G(work, "checkout", "main");
        Write(work, "a.txt", "stashed\n");
        G(work, "stash", "push");
        Commit(work, "a.txt", "newer\n", "moved on");
        Assert.NotEqual(0, Try(work, "stash", "apply"));
        Write(work, "blocker.txt", "untracked here\n");
        string unresolved = Read("a.txt");

        await Assert.ThrowsAsync<RequestException>(() => versioning.Checkout(workspace, "elsewhere", true, CancellationToken.None));

        Assert.Equal(("main", unresolved, "untracked here\n"), (Current(), Read("a.txt"), Read("blocker.txt")));
    }

    [Fact]
    public async Task ACheckoutThatIsRefusedDiscardsNothingAndOneThatDiscardsKeepsFilesIgnoredOnlyByDiscardedRules()
    {
        G(work, "checkout", "-b", "tracks");
        Commit(work, "config.txt", "tracked\n", "track config");
        G(work, "checkout", "main");
        File.WriteAllText(Path.Combine(work, ".git", "info", "exclude"), "config.txt\n");
        Write(work, "config.txt", "MY SETTINGS\n");
        Write(work, "a.txt", "pending edit\n");

        await Assert.ThrowsAsync<RequestException>(() => versioning.Checkout(workspace, "tracks", true, CancellationToken.None));

        Assert.Equal(("main", "MY SETTINGS\n", "pending edit\n"), (Current(), Read("config.txt"), Read("a.txt")));
        File.Delete(Path.Combine(work, "config.txt"));
        File.Delete(Path.Combine(work, ".git", "info", "exclude"));
        Write(work, ".gitignore", ".env\n");
        Write(work, ".env", "SECRET\n");

        await versioning.Checkout(workspace, "tracks", true, CancellationToken.None);

        Assert.Equal(("tracks", "SECRET\n"), (Current(), Read(".env")));
        Assert.False(File.Exists(Path.Combine(work, ".gitignore")));
        Assert.Equal(string.Empty, G(work, "stash", "list"));
    }

    [Fact]
    public async Task DeletingABranchNeverDeletesATagOfTheSameNameOnTheRemote()
    {
        G(work, "tag", "rel");
        G(work, "push", "origin", "refs/tags/rel");
        G(work, "push", "origin", "refs/heads/main:refs/heads/team/rel");
        G(work, "branch", "rel");

        await versioning.DeleteBranch(workspace, "rel", CancellationToken.None);

        Assert.Equal(Tip(work, "main"), Tip(remote, "refs/tags/rel"));
    }

    [Fact]
    public async Task MergingNeverDeletesARemoteBranchThatHoldsUnmergedCommits()
    {
        G(work, "checkout", "-b", "feature");
        Commit(work, "f.txt", "1\n", "one");
        G(work, "push", "origin", "feature");
        G(work, "checkout", "main");
        string theirs = Teammate("feature");
        string main = Tip(work, "main");

        await Assert.ThrowsAsync<RequestException>(() => versioning.Merge(workspace, "feature", "main", null, CancellationToken.None));

        Assert.Equal(theirs, Tip(remote, "feature"));
        Assert.Equal(main, Tip(work, "main"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncDeletesTheCheckedOutBranchWithItsRemoteOneOnlyWhenTheRemoteHadAllItHolds(bool unpushed)
    {
        G(work, "checkout", "-b", "feature");
        Commit(work, "f.txt", "1\n", "one");
        G(work, "push", "origin", "feature");
        string tip = unpushed ? Commit(work, "f.txt", "2\n", "not pushed") : Tip(work, "HEAD");
        G(remote, "branch", "-D", "feature");

        await versioning.Sync(workspace, CancellationToken.None);

        Assert.Equal(unpushed ? "feature" : "HEAD", Current());
        Assert.Equal(tip, Tip(work, "HEAD"));
        Assert.Equal(unpushed, G(work, "branch", "--list", "feature").Length > 0);
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
    }

    [Fact]
    public async Task SyncNeverTouchesAnOlderStashWhenThereIsNothingItCanStash()
    {
        string sub = temp.Combine("sub");
        Directory.CreateDirectory(sub);
        G(sub, "init", "-b", "main");
        Commit(sub, "s.txt", "s\n", "sub");
        G(work, "-c", "protocol.file.allow=always", "submodule", "add", sub, "module");
        G(work, "commit", "-m", "add module");
        G(work, "push", "origin", "main");
        Write(work, "a.txt", "stashed long ago\n");
        G(work, "stash", "push");
        string old = Tip(work, "refs/stash");
        G(temp.Path, "clone", remote, "other");
        Commit(other, "n.txt", "new\n", "newer");
        G(other, "push", "origin", "main");
        Write(Path.Combine(work, "module"), "s.txt", "dirty\n");

        await versioning.Sync(workspace, CancellationToken.None);

        Assert.Equal(old, Tip(work, "refs/stash"));
        Assert.Equal(("one\n", "new\n"), (Read("a.txt"), Read("n.txt")));
    }

    [Fact]
    public async Task SyncKeepsTheStashWhenAFileGitDidNotTrackCouldNotBeRestored()
    {
        G(temp.Path, "clone", remote, "other");
        Write(other, "a.txt", "theirs\n");
        Commit(other, "notes.txt", "their notes\n", "newer");
        G(other, "push", "origin", "main");
        Write(work, "a.txt", "mine\n");
        Write(work, "notes.txt", "MY NOTES\n");
        prompts.Setup(x => x.IsBusy(workspace.Id)).Returns(false);
        prompts.Setup(x => x.Prompt(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            Write(work, "a.txt", "resolved\n");
            G(work, "add", "a.txt");
            return Task.CompletedTask;
        });

        RequestException failed = await Assert.ThrowsAsync<RequestException>(() => versioning.Sync(workspace, CancellationToken.None));

        Assert.Contains("kept in the stash", failed.Message);
        Assert.Equal("MY NOTES", G(work, "show", "stash@{0}^3:notes.txt"));
    }

    [Fact]
    public async Task SyncLeavesTheTreeAloneOnceTheAiStartedWorkingDuringItsFetch()
    {
        G(temp.Path, "clone", remote, "other");
        Commit(other, "n.txt", "new\n", "newer");
        G(other, "push", "origin", "main");
        Write(work, "a.txt", "being edited\n");
        string head = Tip(work, "HEAD");
        Mock<IGitService> slow = new();
        slow.Setup(x => x.Run(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>())).Returns(async (string folder, IReadOnlyList<string> arguments, CancellationToken cancellation, TimeSpan? _) =>
        {
            GitOutput result = await git.Run(folder, arguments, cancellation);

            if (arguments[0] == "fetch")
            {
                activity.TryEnter(workspace.Id, WorkspaceState.Working);
            }

            return result;
        });

        await new WorkspaceVersioning(slow.Object, prompts.Object, events, activity).Sync(workspace, CancellationToken.None);

        Assert.Equal((head, "being edited\n", WorkspaceState.Working), (Tip(work, "HEAD"), Read("a.txt"), activity.Get(workspace.Id)));
        Assert.Equal(string.Empty, G(work, "stash", "list"));
    }

    [Fact]
    public async Task StoppingSyncWhileItsConflictIsResolvedAsksTheAiOnceAndKeepsTheStash()
    {
        G(temp.Path, "clone", remote, "other");
        Commit(other, "a.txt", "theirs\n", "newer");
        G(other, "push", "origin", "main");
        Write(work, "a.txt", "mine\n");
        TaskCompletionSource asked = Hanging();
        Task sync = versioning.Sync(workspace, CancellationToken.None);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(WorkspaceState.Managing, activity.Get(workspace.Id));

        await versioning.Cancel(workspace.Id);
        RequestException stopped = await Assert.ThrowsAsync<RequestException>(() => sync);

        Assert.Contains("kept in the stash", stopped.Message);
        prompts.Verify(x => x.Prompt(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotEqual(string.Empty, G(work, "stash", "list"));
        // The fake AI is still busy, which the state has to say once the action ended.
        Assert.Equal(WorkspaceState.Working, activity.Get(workspace.Id));
    }

    [Fact]
    public async Task ARebaseThatCannotContinueIsAbortedAndKeepsTheOtherEditsTheAiLeft()
    {
        Commit(work, "b.txt", "bee\n", "add b");
        string feature = Conflicting("feature");
        prompts.Setup(x => x.IsBusy(workspace.Id)).Returns(false);
        prompts.Setup(x => x.Prompt(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            Write(work, "a.txt", "resolved\n");
            G(work, "add", "a.txt");
            Write(work, "a.txt", "left unstaged\n");
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<RequestException>(() => versioning.Rebase(workspace, "feature", "main", null, CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(work, ".git", "rebase-merge")));
        Assert.Equal(("main", feature), (Current(), Tip(work, "feature")));
        Assert.Equal("left unstaged", G(work, "show", "stash@{0}:a.txt"));
    }

    [Fact]
    public async Task ARebaseThatWouldReplaceAnIgnoredFileIsRefusedAndASquashItBeganIsTakenBack()
    {
        G(work, "checkout", "-b", "old");
        Commit(work, "o.txt", "1\n", "one");
        string tip = Commit(work, "o.txt", "2\n", "two");
        G(work, "checkout", "main");
        Commit(work, "config.txt", "tracked on main\n", "track config");
        G(work, "checkout", "old");
        File.WriteAllText(Path.Combine(work, ".git", "info", "exclude"), "config.txt\n");
        Write(work, "config.txt", "MY SETTINGS\n");

        RequestException refused = await Assert.ThrowsAsync<RequestException>(() => versioning.Rebase(workspace, "old", "main", "squashed", CancellationToken.None));

        Assert.Contains("config.txt", refused.Message);
        Assert.Equal(("MY SETTINGS\n", tip), (Read("config.txt"), Tip(work, "old")));
    }
}
