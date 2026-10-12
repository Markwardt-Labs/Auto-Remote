namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class GitHistoryViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly Guid id = Guid.NewGuid();

    private GitStatus status = new() { IsRepo = true, Branch = "main", Commit = "abc1234", Message = "second" };

    private GitCommit Commit(string hash, string subject, params GitRef[] refs) => new() { Hash = hash, Subject = subject, Author = "Ann", Date = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), Refs = refs };

    public GitHistoryViewModelTests()
    {
        connection.Setup(x => x.GetGitStatus(id, It.IsAny<CancellationToken>())).ReturnsAsync(() => status);
        connection.Setup(x => x.GetGitChanges(id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetGitHistory(id, 0, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => new GitHistory { Commits = [Commit("2222222222", "second", new GitRef { Name = "main", Kind = GitRefKind.Branch, IsCurrent = true }, new GitRef { Name = "v1", Kind = GitRefKind.Tag }), Commit("1111111111", "first")], HasMore = true });
    }

    [Fact]
    public async Task Start_LoadsTheFirstChunkOnceAndShowsCommitDetails()
    {
        GitViewModel git = new(connection.Object, id);

        await git.History.Start();
        await git.History.Start();

        Assert.Equal(["second", "first"], git.History.Commits.Select(commit => commit.Subject));
        Assert.True(git.History.HasMore);
        Assert.False(git.History.IsEmpty);
        GitCommitViewModel newest = git.History.Commits[0];
        Assert.Equal("2222222", newest.ShortHash);
        Assert.True(newest.HasRefs);
        Assert.StartsWith("Ann · ", newest.Detail);
        Assert.EndsWith(" · 2222222", newest.Detail);
        connection.Verify(x => x.GetGitHistory(id, 0, It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoadMore_AppendsOlderCommitsAndStopsWhenThereAreNoMore()
    {
        connection.Setup(x => x.GetGitHistory(id, 2, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GitHistory { Commits = [Commit("0000000000", "root")], HasMore = false });
        GitViewModel git = new(connection.Object, id);
        await git.History.Start();

        Assert.True(git.History.LoadMoreCommand.CanExecute(null));
        git.History.LoadMoreCommand.Execute(null);
        await Task.Delay(100);

        Assert.Equal(["second", "first", "root"], git.History.Commits.Select(commit => commit.Subject));
        Assert.False(git.History.HasMore);
        Assert.False(git.History.LoadMoreCommand.CanExecute(null));
    }

    [Fact]
    public async Task EmptyRepository_ShowsNoCommits()
    {
        connection.Setup(x => x.GetGitHistory(id, 0, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GitHistory { Commits = [] });
        GitViewModel git = new(connection.Object, id);

        await git.History.Start();

        Assert.True(git.History.IsEmpty);
    }

    [Fact]
    public async Task ExpandingACommit_ReadsItsChangesIntoATreeAndOpeningAFileRequestsItsDiff()
    {
        connection.Setup(x => x.GetGitCommitChanges(id, "2222222222", It.IsAny<CancellationToken>())).ReturnsAsync([new GitChange { Path = "src/a.txt", Status = GitChangeStatus.Deleted }, new GitChange { Path = "b.txt", Status = GitChangeStatus.Added }]);
        GitViewModel git = new(connection.Object, id);
        await git.History.Start();
        List<(string, string?)> requested = [];
        git.DiffRequested += (path, commit) => requested.Add((path, commit));
        GitCommitViewModel commit = git.History.Commits[0];

        commit.ToggleCommand.Execute(null);
        await Task.Delay(100);

        Assert.True(commit.IsExpanded);
        Assert.False(commit.IsLoading);
        Assert.Equal(["src", "b.txt"], commit.Changes.Select(node => node.Name));
        commit.Changes[0].Children[0].OpenCommand.Execute(null);
        commit.Changes[1].OpenCommand.Execute(null);
        Assert.Equal([("src/a.txt", (string?)"2222222222"), ("b.txt", "2222222222")], requested);

        commit.ToggleCommand.Execute(null);
        commit.ToggleCommand.Execute(null);
        await Task.Delay(50);
        connection.Verify(x => x.GetGitCommitChanges(id, "2222222222", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ACommitThatChangedNothingSaysSo()
    {
        connection.Setup(x => x.GetGitCommitChanges(id, "1111111111", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        GitViewModel git = new(connection.Object, id);
        await git.History.Start();

        git.History.Commits[1].ToggleCommand.Execute(null);
        await Task.Delay(100);

        Assert.True(git.History.Commits[1].HasNoChanges);
    }

    [Fact]
    public async Task WhenTheHeadMoves_TheHistoryOnlyReloadsOnceItWasLookedAtAndKeepsOpenCommits()
    {
        connection.Setup(x => x.GetGitCommitChanges(id, "1111111111", It.IsAny<CancellationToken>())).ReturnsAsync([new GitChange { Path = "a.txt", Status = GitChangeStatus.Added }]);
        GitViewModel git = new(connection.Object, id);
        await git.Start();
        status = status with { Commit = "def5678" };
        await git.Refresh();
        connection.Verify(x => x.GetGitHistory(id, It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);

        await git.History.Start();
        git.History.Commits[1].ToggleCommand.Execute(null);
        await Task.Delay(100);
        connection.Setup(x => x.GetGitHistory(id, 0, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GitHistory { Commits = [Commit("3333333333", "third"), Commit("2222222222", "second"), Commit("1111111111", "first")] });
        status = status with { Commit = "9999999" };
        await git.Refresh();
        await Task.Delay(100);

        Assert.Equal(["third", "second", "first"], git.History.Commits.Select(commit => commit.Subject));
        Assert.True(git.History.Commits[2].IsExpanded);
        Assert.False(git.History.Commits[0].IsExpanded);
    }
}
