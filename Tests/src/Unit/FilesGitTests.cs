namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class FilesGitTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly Guid id = Guid.NewGuid();

    private List<GitChange> changes = [new() { Path = "a.txt", Status = GitChangeStatus.Modified }, new() { Path = "new", Status = GitChangeStatus.Added }, new() { Path = "out/", Status = GitChangeStatus.Ignored }];

    public FilesGitTests()
    {
        connection.Setup(x => x.GetGitStatus(id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = true, Branch = "main" });
        connection.Setup(x => x.GetGitChanges(id, It.IsAny<CancellationToken>())).ReturnsAsync(() => changes);
        connection.Setup(x => x.ListFiles(id, string.Empty, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles
        {
            Path = string.Empty,
            Entries =
            [
                new FileEntry { Name = "new", Path = "new", IsFolder = true },
                new FileEntry { Name = "out", Path = "out", IsFolder = true },
                new FileEntry { Name = "a.txt", Path = "a.txt", IsFolder = false },
                new FileEntry { Name = "b.txt", Path = "b.txt", IsFolder = false },
            ],
        });
    }

    private GitDiff Diff(string path, params GitDiffLine[] lines) => new() { Path = path, Lines = lines };

    [Fact]
    public async Task Nodes_AreColoredByHowGitSeesThemAndRecoloredWhenTheChangesUpdate()
    {
        GitViewModel git = new(connection.Object, id);
        FilesViewModel files = new(connection.Object, id, git);
        await git.Start();
        await files.Start();

        Assert.Equal([GitChangeStatus.Added, GitChangeStatus.Ignored, GitChangeStatus.Modified, null], files.Nodes.Select(node => node.GitStatus));
        Assert.True(files.Nodes[0].IsAdded);
        Assert.True(files.Nodes[1].IsIgnored);
        Assert.True(files.Nodes[2].IsModified);

        changes = [new GitChange { Path = "b.txt", Status = GitChangeStatus.Modified }];
        await git.Refresh();

        Assert.Equal([null, null, null, GitChangeStatus.Modified], files.Nodes.Select(node => node.GitStatus));
    }

    [Fact]
    public async Task NodesThatAppearLater_AreColoredRightAway()
    {
        GitViewModel git = new(connection.Object, id);
        FilesViewModel files = new(connection.Object, id, git);
        await files.Start();
        await git.Start();
        connection.Setup(x => x.ListFiles(id, string.Empty, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [new FileEntry { Name = "a.txt", Path = "a.txt", IsFolder = false }, new FileEntry { Name = "c.txt", Path = "c.txt", IsFolder = false }] });
        changes = [new GitChange { Path = "c.txt", Status = GitChangeStatus.Added }];
        await git.Refresh();

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.FilesChanged, WorkspaceId = id, Path = string.Empty });
        await Task.Delay(100);

        Assert.Equal([null, GitChangeStatus.Added], files.Nodes.Select(node => node.GitStatus));
    }

    [Fact]
    public async Task AskingForADiff_ReplacesTheFilePreviewAndClosingClearsIt()
    {
        connection.Setup(x => x.ReadFile(id, "b.txt", It.IsAny<CancellationToken>())).ReturnsAsync(new FileContent { Path = "b.txt", Text = "text" });
        GitDiff diff = Diff("a.txt", new GitDiffLine { Kind = GitDiffKind.Added, Text = "x", NewNumber = 1 });
        connection.Setup(x => x.GetGitDiff(id, "a.txt", null, It.IsAny<CancellationToken>())).ReturnsAsync(diff);
        connection.Setup(x => x.GetGitDiff(id, "a.txt", "1234567890", It.IsAny<CancellationToken>())).ReturnsAsync(diff);
        GitViewModel git = new(connection.Object, id);
        FilesViewModel files = new(connection.Object, id, git);
        await files.Start();

        files.Selected = files.Nodes[3];
        await Task.Delay(100);
        Assert.True(files.IsTextPreview);
        Assert.False(files.IsDiffPreview);

        git.RequestDiff("a.txt", null);
        await Task.Delay(100);
        Assert.True(files.IsDiffPreview);
        Assert.True(files.IsPreviewing);
        Assert.False(files.IsTextPreview);
        Assert.Null(files.Preview);
        Assert.Equal("a.txt (changes)", files.PreviewTitle);

        git.RequestDiff("a.txt", "1234567890");
        await Task.Delay(100);
        Assert.Equal("a.txt (1234567)", files.PreviewTitle);

        files.Selected = files.Nodes[3];
        await Task.Delay(100);
        Assert.True(files.IsTextPreview);
        Assert.False(files.IsDiffPreview);

        git.RequestDiff("a.txt", null);
        await Task.Delay(100);
        files.ClosePreviewCommand.Execute(null);
        Assert.False(files.IsPreviewing);
        Assert.Null(files.Diff);
    }

    [Fact]
    public async Task AnOpenWorkingTreeDiff_IsReadAgainWhenTheChangesUpdate_ButACommitsDiffIsNot()
    {
        connection.Setup(x => x.GetGitDiff(id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Diff("a.txt"));
        GitViewModel git = new(connection.Object, id);
        FilesViewModel files = new(connection.Object, id, git);
        await git.Start();

        git.RequestDiff("a.txt", null);
        await Task.Delay(100);
        await git.Refresh();
        await Task.Delay(100);
        connection.Verify(x => x.GetGitDiff(id, "a.txt", null, It.IsAny<CancellationToken>()), Times.Exactly(2));

        git.RequestDiff("a.txt", "1234567890");
        await Task.Delay(100);
        await git.Refresh();
        await Task.Delay(100);
        connection.Verify(x => x.GetGitDiff(id, "a.txt", "1234567890", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CollapseAll_AlsoClosesTheChangeTree()
    {
        GitViewModel git = new(connection.Object, id);
        FilesViewModel files = new(connection.Object, id, git);
        changes = [new GitChange { Path = "src/a.txt", Status = GitChangeStatus.Modified }];
        await git.Start();

        files.CollapseAllCommand.Execute(null);
        await Task.Delay(50);

        Assert.False(git.ChangeTree[0].IsExpanded);
    }
}
