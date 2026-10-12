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

    private GitCommit Commit(params GitRef[] refs) => new() { Hash = "1234567890abcdef", Subject = "work", Author = "Ann", Date = DateTimeOffset.UnixEpoch, Refs = refs };

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
