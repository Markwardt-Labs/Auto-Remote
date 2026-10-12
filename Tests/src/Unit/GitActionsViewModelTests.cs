namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class GitActionsViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly Guid id = Guid.NewGuid();

    private GitStatus status = new() { IsRepo = true, Branch = "main", Commit = "abc1234", Message = "second", Remote = "/remote.git" };

    public GitActionsViewModelTests()
    {
        connection.Setup(x => x.GetGitStatus(id, It.IsAny<CancellationToken>())).ReturnsAsync(() => status);
        connection.Setup(x => x.GetGitChanges(id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
    }

    private async Task<GitViewModel> Started(TimeSpan? interval = null)
    {
        GitViewModel git = new(connection.Object, id, interval);
        await git.Start();
        return git;
    }

    private async Task Answer(GitViewModel git, string? text = null)
    {
        if (text is not null)
        {
            git.PromptText = text;
        }

        git.ConfirmCommand.Execute(null);
        await Task.Delay(100);
    }

    private GitCommit Commit(params GitRef[] refs) => new() { Hash = "1234567890abcdef", Subject = "work", Author = "Ann", Date = DateTimeOffset.UnixEpoch, Refs = refs };

    [Fact]
    public async Task Actions_AskForTheIdentityFirstWhenGitHasNone_ThenContinueWithTheAction()
    {
        status = status with { HasIdentity = false };
        GitViewModel git = await Started();

        git.CommitCommand.Execute(null);

        Assert.Equal(GitPrompt.Identity, git.Prompt);
        Assert.True(git.HasInput);
        Assert.True(git.HasEmail);
        await Answer(git, "Ann");
        Assert.Equal("Enter your name and email.", git.Error);

        git.PromptEmail = "ann@example.com";
        status = status with { HasIdentity = true };
        await Answer(git);

        connection.Verify(x => x.GitSetIdentity(id, "Ann", "ann@example.com", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(GitPrompt.Input, git.Prompt);
        Assert.Equal("Commit", git.PromptTitle);
        Assert.False(git.HasEmail);
    }

    [Fact]
    public async Task BranchNeedsNoIdentityButTagAndHistoryRewritesDo()
    {
        status = status with { HasIdentity = false };
        GitViewModel git = await Started();

        git.BranchCommand.Execute(null);
        Assert.Equal("New branch", git.PromptTitle);
        git.CancelCommand.Execute(null);
        git.TagCommand.Execute(null);

        Assert.Equal(GitPrompt.Identity, git.Prompt);
    }

    [Fact]
    public async Task Checkout_AsksForConfirmationAndDiscardsPendingChangesOnlyWhenThereAreAny()
    {
        GitViewModel git = await Started();

        await git.Checkout("feature");
        Assert.Equal(GitPrompt.Confirm, git.Prompt);
        Assert.False(git.IsDestructive);
        Assert.Equal("Check out 'feature'?", git.PromptHint);
        await Answer(git);
        connection.Verify(x => x.GitCheckout(id, "feature", false, It.IsAny<CancellationToken>()), Times.Once);

        status = status with { HasChanges = true };
        await git.Refresh();
        await git.Checkout("1234567890abcdef1234");
        Assert.True(git.IsDestructive);
        Assert.Contains("discard your pending changes", git.PromptHint);
        Assert.Contains("1234567", git.PromptHint);
        Assert.Equal("Discard and checkout", git.PromptConfirm);
        await Answer(git);
        connection.Verify(x => x.GitCheckout(id, "1234567890abcdef1234", true, It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(git.IsPrompting);
    }

    [Fact]
    public async Task DeleteBranchAndTag_AskThenDeleteEverywhere()
    {
        GitViewModel git = await Started();

        await git.DeleteBranch("feature");
        Assert.True(git.IsDestructive);
        await Answer(git);
        await git.DeleteTag("v1");
        await Answer(git);

        connection.Verify(x => x.GitDeleteBranch(id, "feature", It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.GitDeleteTag(id, "v1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AFailedAction_KeepsTheQuestionWithTheServersMessageAndStillReadsTheStateAgain()
    {
        connection.Setup(x => x.GitCheckout(id, "feature", false, It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("boom"));
        GitViewModel git = await Started();
        await git.Checkout("feature");
        connection.Invocations.Clear();

        await Answer(git);

        Assert.True(git.IsPrompting);
        Assert.Equal("boom", git.Error);
        connection.Verify(x => x.GetGitStatus(id, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Squash_AsksForTheMessageDefaultingToTheTipsSubjectAndSendsIt()
    {
        connection.Setup(x => x.GetGitBranchState(id, "feature", "main", It.IsAny<CancellationToken>())).ReturnsAsync(new GitBranchState { Unique = 3, IsBased = true, Subject = "tip subject" });
        GitViewModel git = await Started();

        await git.Squash("feature", "main");

        Assert.Equal(GitPrompt.Input, git.Prompt);
        Assert.Equal("Squash 'feature'", git.PromptTitle);
        Assert.Contains("3 commits", git.PromptHint);
        Assert.Equal("tip subject", git.PromptText);
        await Answer(git, "  all in one ");
        connection.Verify(x => x.GitSquash(id, "feature", "main", "all in one", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Squash_TellsTheUserWhenThereIsNothingToSquashOrTheServerRefuses()
    {
        connection.Setup(x => x.GetGitBranchState(id, "feature", "main", It.IsAny<CancellationToken>())).ReturnsAsync(new GitBranchState { Unique = 0, IsBased = true });
        connection.Setup(x => x.GetGitBranchState(id, "ghost", "main", It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("There is no branch named 'ghost'."));
        GitViewModel git = await Started();

        await git.Squash("feature", "main");
        Assert.Equal(GitPrompt.Notice, git.Prompt);
        Assert.False(git.ShowCancel);
        Assert.Contains("has no commits", git.PromptHint);
        await Answer(git);
        Assert.False(git.IsPrompting);

        await git.Squash("ghost", "main");
        Assert.Equal(GitPrompt.Notice, git.Prompt);
        Assert.Equal("There is no branch named 'ghost'.", git.PromptHint);
        connection.Verify(x => x.GitSquash(id, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Rebase_RefusesWithPendingChangesOrWhenUpToDateAndAsksForAMessageOnlyForSeveralCommits()
    {
        connection.Setup(x => x.GetGitBranchState(id, "same", "main", It.IsAny<CancellationToken>())).ReturnsAsync(new GitBranchState { Unique = 0, IsBased = true });
        connection.Setup(x => x.GetGitBranchState(id, "one", "main", It.IsAny<CancellationToken>())).ReturnsAsync(new GitBranchState { Unique = 1, IsBased = false, Subject = "s" });
        connection.Setup(x => x.GetGitBranchState(id, "many", "main", It.IsAny<CancellationToken>())).ReturnsAsync(new GitBranchState { Unique = 4, IsBased = false, Subject = "tip" });
        GitViewModel git = await Started();
        status = status with { HasChanges = true };
        await git.Refresh();

        await git.Rebase("one", "main");
        Assert.Equal(GitPrompt.Notice, git.Prompt);
        Assert.Contains("clean working tree", git.PromptHint);
        await Answer(git);

        status = status with { HasChanges = false };
        await git.Refresh();
        await git.Rebase("same", "main");
        Assert.Contains("already up to date", git.PromptHint);
        await Answer(git);

        await git.Rebase("one", "main");
        Assert.Equal(GitPrompt.Confirm, git.Prompt);
        await Answer(git);
        connection.Verify(x => x.GitRebase(id, "one", "main", null, It.IsAny<CancellationToken>()), Times.Once);

        await git.Rebase("many", "main");
        Assert.Equal(GitPrompt.Input, git.Prompt);
        Assert.Equal("tip", git.PromptText);
        await Answer(git, "squashed");
        connection.Verify(x => x.GitRebase(id, "many", "main", "squashed", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Merge_RefusesWithPendingChangesOrNothingToMergeAndAsksForAMessageForSeveralCommits()
    {
        connection.Setup(x => x.GetGitBranchState(id, "empty", "main", It.IsAny<CancellationToken>())).ReturnsAsync(new GitBranchState { Unique = 0, IsBased = true });
        connection.Setup(x => x.GetGitBranchState(id, "one", "main", It.IsAny<CancellationToken>())).ReturnsAsync(new GitBranchState { Unique = 1, IsBased = true });
        connection.Setup(x => x.GetGitBranchState(id, "many", "main", It.IsAny<CancellationToken>())).ReturnsAsync(new GitBranchState { Unique = 2, IsBased = true, Subject = "tip" });
        GitViewModel git = await Started();

        await git.Merge("empty", "main");
        Assert.Contains("has no commits", git.PromptHint);
        await Answer(git);
        await git.Merge("one", "main");
        Assert.Equal(GitPrompt.Confirm, git.Prompt);
        Assert.Contains("deleted afterwards", git.PromptHint);
        await Answer(git);
        await git.Merge("many", "main");
        await Answer(git, "both");

        connection.Verify(x => x.GitMerge(id, "one", "main", null, It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.GitMerge(id, "many", "main", "both", It.IsAny<CancellationToken>()), Times.Once);
        status = status with { HasChanges = true };
        await git.Refresh();
        await git.Merge("one", "main");
        Assert.Contains("clean working tree", git.PromptHint);
    }

    [Fact]
    public async Task Working_FollowsTheServersBusyFlagAndLetsTheUserCancelAndUndo()
    {
        status = status with { Busy = true };
        GitViewModel git = await Started();

        Assert.True(git.IsWorking);
        git.CancelWorkCommand.Execute(null);
        await Task.Delay(50);
        connection.Verify(x => x.GitCancel(id, It.IsAny<CancellationToken>()), Times.Once);

        status = status with { Busy = false };
        await git.Refresh();
        Assert.False(git.IsWorking);
    }

    [Fact]
    public async Task GitChangedEvents_ReadTheStateAgain()
    {
        GitViewModel git = await Started();
        connection.Invocations.Clear();

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.GitChanged, WorkspaceId = id });
        await Task.Delay(50);
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.GitChanged, WorkspaceId = Guid.NewGuid() });
        await Task.Delay(50);

        connection.Verify(x => x.GetGitStatus(id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Fetch_AsksTheServerToFetchAndReadsTheStateAgain()
    {
        GitViewModel git = await Started();
        connection.Invocations.Clear();

        git.FetchCommand.Execute(null);
        await Task.Delay(100);

        connection.Verify(x => x.GitFetch(id, It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.GetGitStatus(id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncOnce_NeedsARepositoryWithARemoteAndNoGitWorkGoingOn()
    {
        GitViewModel git = await Started();

        await git.SyncOnce();
        connection.Verify(x => x.GitSync(id, It.IsAny<CancellationToken>()), Times.Once);

        status = status with { Remote = null };
        await git.Refresh();
        await git.SyncOnce();
        status = status with { Remote = "/r.git", Busy = true };
        await git.Refresh();
        await git.SyncOnce();
        connection.Verify(x => x.GitSync(id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncOnce_SwallowsAServerFailure()
    {
        connection.Setup(x => x.GitSync(id, It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("conflicts"));
        GitViewModel git = await Started();

        await git.SyncOnce();

        Assert.False(git.HasError);
        Assert.False(git.IsWorking);
    }

    [Fact]
    public async Task StartSync_SyncsNowAndEveryIntervalUntilStopped()
    {
        GitViewModel git = await Started(TimeSpan.FromMilliseconds(60));

        git.StartSync();
        git.StartSync();
        await Task.Delay(400);
        git.StopSync();
        int calls = connection.Invocations.Count(invocation => invocation.Method.Name == nameof(IServerConnection.GitSync));
        await Task.Delay(200);

        Assert.InRange(calls, 3, 12);
        Assert.Equal(calls, connection.Invocations.Count(invocation => invocation.Method.Name == nameof(IServerConnection.GitSync)));
    }

    [Fact]
    public async Task RefBadges_OfferCheckoutDeleteAndRelativeActionsOnlyWhereTheyMakeSense()
    {
        GitViewModel git = await Started();
        GitRefViewModel feature = new(new GitRef { Name = "feature_x", Kind = GitRefKind.Branch }, git, "main");
        GitRefViewModel current = new(new GitRef { Name = "main", Kind = GitRefKind.Branch, IsCurrent = true }, git, "main");
        GitRefViewModel tag = new(new GitRef { Name = "v1", Kind = GitRefKind.Tag }, git, "main");
        GitRefViewModel remote = new(new GitRef { Name = "origin/other", Kind = GitRefKind.Remote }, git, "main");
        GitRefViewModel detached = new(new GitRef { Name = "feature", Kind = GitRefKind.Branch }, git, null);

        Assert.True(feature.CanCheckout);
        Assert.True(feature.CanDelete);
        Assert.True(feature.HasRelative);
        Assert.Equal("Squash 'main' (current) since 'feature__x'", feature.SquashCurrentHeader);
        Assert.Equal("Squash 'feature__x' since 'main' (current)", feature.SquashSelectedHeader);
        Assert.Equal("Rebase 'main' (current) onto 'feature__x'", feature.RebaseCurrentHeader);
        Assert.Equal("Merge 'feature__x' into 'main' (current)", feature.MergeSelectedHeader);
        Assert.False(current.CanCheckout);
        Assert.False(current.CanDelete);
        Assert.False(current.HasRelative);
        Assert.True(tag.CanCheckout);
        Assert.False(tag.HasRelative);
        Assert.True(tag.IsTag);
        Assert.Equal("other", remote.Target);
        Assert.True(remote.HasRelative);
        Assert.False(detached.HasRelative);
    }

    [Fact]
    public async Task RefBadgeCommands_RunTheGitActionsAgainstTheRightBranches()
    {
        connection.Setup(x => x.GetGitBranchState(id, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GitBranchState { Unique = 1, IsBased = false });
        GitViewModel git = await Started();
        GitRefViewModel feature = new(new GitRef { Name = "feature", Kind = GitRefKind.Branch }, git, "main");
        GitRefViewModel tag = new(new GitRef { Name = "v1", Kind = GitRefKind.Tag }, git, "main");

        feature.CheckoutCommand.Execute(null);
        await Task.Delay(50);
        await Answer(git);
        feature.RebaseSelectedCommand.Execute(null);
        await Task.Delay(100);
        await Answer(git);
        feature.MergeCurrentCommand.Execute(null);
        await Task.Delay(100);
        await Answer(git);
        tag.DeleteCommand.Execute(null);
        await Task.Delay(50);
        await Answer(git);
        feature.DeleteCommand.Execute(null);
        await Task.Delay(50);
        await Answer(git);

        connection.Verify(x => x.GitCheckout(id, "feature", false, It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.GitRebase(id, "feature", "main", null, It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.GitMerge(id, "main", "feature", null, It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.GitDeleteTag(id, "v1", It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.GitDeleteBranch(id, "feature", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CommitRows_CarryTheirBadgesAndCanCheckTheCommitOut()
    {
        connection.Setup(x => x.GetGitHistory(id, 0, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GitHistory { Commits = [Commit(new GitRef { Name = "main", Kind = GitRefKind.Branch, IsCurrent = true }, new GitRef { Name = "feature", Kind = GitRefKind.Branch })] });
        GitViewModel git = await Started();
        await git.History.Start();

        GitCommitViewModel row = git.History.Commits[0];
        Assert.Equal(["main", "feature"], row.Refs.Select(item => item.Name));
        Assert.True(row.Refs[1].HasRelative);
        Assert.False(row.Refs[0].HasRelative);
        row.CheckoutCommand.Execute(null);
        await Task.Delay(50);
        await Answer(git);

        connection.Verify(x => x.GitCheckout(id, "1234567890abcdef", false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OpeningTheHistory_SyncsOnceAndLeavingTheWorkspaceStopsTheBackgroundSync()
    {
        connection.Setup(x => x.GetHistory(id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0 });
        connection.Setup(x => x.GetGitHistory(id, 0, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GitHistory { Commits = [] });
        connection.Setup(x => x.ListFiles(id, string.Empty, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [] });
        WorkspaceViewModel model = new(connection.Object, new WorkspaceInfo { Id = id, Folder = "/work/app" }, ClientLayout.Panels);
        await model.Start();
        await Task.Delay(50);
        await model.Focus();
        await Task.Delay(100);
        int afterFocus = connection.Invocations.Count(invocation => invocation.Method.Name == nameof(IServerConnection.GitSync));

        model.ShowHistoryCommand.Execute(null);
        await Task.Delay(100);
        Assert.Equal(afterFocus + 1, connection.Invocations.Count(invocation => invocation.Method.Name == nameof(IServerConnection.GitSync)));

        model.Blur();
        int stopped = connection.Invocations.Count(invocation => invocation.Method.Name == nameof(IServerConnection.GitSync));
        await Task.Delay(100);
        Assert.Equal(stopped, connection.Invocations.Count(invocation => invocation.Method.Name == nameof(IServerConnection.GitSync)));
    }

    [Fact]
    public async Task WhenTheAiStartsWorkingForAGitAction_TheWorkspaceShowsTheChat()
    {
        status = status with { Busy = true };
        connection.Setup(x => x.GetHistory(id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0 });
        connection.Setup(x => x.GetGitHistory(id, 0, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GitHistory { Commits = [] });
        connection.Setup(x => x.ListFiles(id, string.Empty, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [] });
        WorkspaceViewModel model = new(connection.Object, new WorkspaceInfo { Id = id, Folder = "/work/app" }, ClientLayout.Tabs);
        await model.Start();
        await Task.Delay(100);
        model.ShowHistoryCommand.Execute(null);
        await Task.Delay(100);
        Assert.True(model.ShowHistory);

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = id, Working = true });
        await Task.Delay(50);

        Assert.True(model.ShowChat);
    }

    [Fact]
    public async Task HistoryReloads_WhenRefsChangeWithoutTheHeadMoving()
    {
        int reads = 0;
        connection.Setup(x => x.GetGitHistory(id, 0, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => new GitHistory { Commits = [Commit(new GitRef { Name = $"branch{++reads}", Kind = GitRefKind.Branch })] });
        GitViewModel git = await Started();
        await git.History.Start();

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.GitChanged, WorkspaceId = id });
        await Task.Delay(100);
        Assert.Equal("branch2", git.History.Commits[0].Refs[0].Name);

        git.FetchCommand.Execute(null);
        await Task.Delay(100);
        Assert.Equal("branch3", git.History.Commits[0].Refs[0].Name);

        git.TagCommand.Execute(null);
        await Answer(git, "v9");
        Assert.Equal("branch4", git.History.Commits[0].Refs[0].Name);
    }

    [Fact]
    public async Task OpeningTheFilesTab_ReadsTheGitStateSoEditsMadeMeanwhileShow()
    {
        connection.Setup(x => x.GetHistory(id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0 });
        connection.Setup(x => x.ListFiles(id, string.Empty, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [] });
        WorkspaceViewModel model = new(connection.Object, new WorkspaceInfo { Id = id, Folder = "/work/app" }, ClientLayout.Tabs);
        await model.Start();
        await Task.Delay(100);
        Assert.False(model.Git.HasChanges);

        status = status with { HasChanges = true };
        model.ShowFilesCommand.Execute(null);
        await Task.Delay(150);

        Assert.True(model.Git.HasChanges);
    }

    [Fact]
    public async Task TheIdentityQuestionAsksForANameNotTheExplanationAsItsPlaceholder()
    {
        status = status with { HasIdentity = false };
        GitViewModel git = await Started();

        git.CommitCommand.Execute(null);

        Assert.Equal("Name", git.PromptPlaceholder);
        git.CancelCommand.Execute(null);
        status = status with { HasIdentity = true };
        await git.Refresh();
        git.CommitCommand.Execute(null);
        Assert.Equal("Message", git.PromptPlaceholder);
    }
}
