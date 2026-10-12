namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class GitViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly Guid id = Guid.NewGuid();

    private GitStatus status = new() { IsRepo = true, Branch = "main", Commit = "abc1234", Message = "first", HasChanges = true, Remote = "https://x/app.git" };

    private List<GitChange> changes = [new() { Path = "src/a.txt", Status = GitChangeStatus.Modified }, new() { Path = "src/new/b.txt", Status = GitChangeStatus.Added }, new() { Path = "bin/", Status = GitChangeStatus.Ignored }];

    public GitViewModelTests()
    {
        connection.Setup(x => x.GetGitStatus(id, It.IsAny<CancellationToken>())).ReturnsAsync(() => status);
        connection.Setup(x => x.GetGitChanges(id, It.IsAny<CancellationToken>())).ReturnsAsync(() => changes);
    }

    private RemoteEvent Event(RemoteEventKind kind, bool? working = null, Guid? workspace = null) => new() { Kind = kind, WorkspaceId = workspace ?? id, Working = working, Path = string.Empty };

    [Fact]
    public async Task Start_ReadsTheStatusAndBuildsTheChangeTreeWithoutIgnoredPaths()
    {
        GitViewModel git = new(connection.Object, id);

        await git.Start();

        Assert.True(git.IsRepo);
        Assert.Equal("main", git.Title);
        Assert.Equal("first", git.Message);
        Assert.Equal("abc1234", git.Commit);
        Assert.True(git.HasChanges);
        Assert.Equal("https://x/app.git", git.Remote);
        ChangeNode src = Assert.Single(git.ChangeTree);
        Assert.Equal("src", src.Name);
        Assert.Equal(["new", "a.txt"], src.Children.Select(child => child.Name));
        Assert.Equal(GitChangeStatus.Added, src.Children[0].Children[0].Status);
        Assert.Equal("src/new/b.txt", src.Children[0].Children[0].Path);
    }

    [Fact]
    public async Task Start_ForAFolderThatIsNotARepositoryShowsNothingAndRefusesChangesMode()
    {
        status = new GitStatus { IsRepo = false };
        GitViewModel git = new(connection.Object, id);

        await git.Start();
        git.IsChangesMode = true;

        Assert.False(git.IsRepo);
        Assert.False(git.IsChangesMode);
        connection.Verify(x => x.GetGitChanges(id, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Title_FallsBackToTheTagOrANoteWithoutABranch()
    {
        GitViewModel git = new(connection.Object, id);
        status = status with { Branch = null, Tag = "v1" };
        await git.Refresh();
        Assert.Equal("v1", git.Title);
        Assert.False(git.HasTag);

        status = status with { Branch = null, Tag = null };
        await git.Refresh();
        Assert.Equal("Detached head", git.Title);

        status = new GitStatus { IsRepo = true, Branch = "main", Tag = "v2" };
        await git.Refresh();
        Assert.Equal("v2", git.Tag);
        Assert.True(git.HasTag);
        Assert.Equal(string.Empty, git.Commit);
    }

    [Fact]
    public async Task StatusOf_ColorsFilesFoldersAndIgnoredPaths()
    {
        GitViewModel git = new(connection.Object, id);
        await git.Start();

        Assert.Equal(GitChangeStatus.Modified, git.StatusOf("src/a.txt", false));
        Assert.Equal(GitChangeStatus.Added, git.StatusOf("src/new/b.txt", false));
        Assert.Equal(GitChangeStatus.Modified, git.StatusOf("src", true));
        Assert.Equal(GitChangeStatus.Added, git.StatusOf("src/new", true));
        Assert.Equal(GitChangeStatus.Ignored, git.StatusOf("bin", true));
        Assert.Equal(GitChangeStatus.Ignored, git.StatusOf("bin/out.dll", false));
        Assert.Null(git.StatusOf("docs", true));
        Assert.Null(git.StatusOf("README.md", false));
    }

    [Fact]
    public async Task Refresh_WhileOneRunsRunsOnceMoreInsteadOfOverlapping()
    {
        TaskCompletionSource<GitStatus> gate = new();
        int calls = 0;
        connection.Setup(x => x.GetGitStatus(id, It.IsAny<CancellationToken>())).Returns(() => ++calls == 1 ? gate.Task : Task.FromResult(status));
        GitViewModel git = new(connection.Object, id);

        Task first = git.Refresh();
        await git.Refresh();
        await git.Refresh();
        gate.SetResult(status);
        await first;

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ChangedFiles_OpeningOneRequestsItsDiffAndFoldersToggle()
    {
        GitViewModel git = new(connection.Object, id);
        await git.Start();
        List<(string, string?)> requested = [];
        git.DiffRequested += (path, commit) => requested.Add((path, commit));

        ChangeNode src = git.ChangeTree[0];
        src.OpenCommand.Execute(null);
        Assert.False(src.IsExpanded);
        src.OpenCommand.Execute(null);
        Assert.True(src.IsExpanded);
        src.Children[1].OpenCommand.Execute(null);

        Assert.Equal([("src/a.txt", (string?)null)], requested);
        git.ToggleChangesModeCommand.Execute(null);
        Assert.True(git.IsChangesMode);
        git.ToggleChangesModeCommand.Execute(null);
        Assert.False(git.IsChangesMode);
    }
}
