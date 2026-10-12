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
    public async Task EmptyRepository_ShowsNoCommits()
    {
        connection.Setup(x => x.GetGitHistory(id, 0, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GitHistory { Commits = [] });
        GitViewModel git = new(connection.Object, id);

        await git.History.Start();

        Assert.True(git.History.IsEmpty);
    }

}
