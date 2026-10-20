namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class FileTreeSelectionTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly Guid id = Guid.NewGuid();

    private FileEntry Entry(string path, bool folder) => new() { Name = path.Split('/')[^1], Path = path, IsFolder = folder };

    private FilesViewModel Create(bool desktop)
    {
        connection.Setup(x => x.GetGitStatus(id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = false });
        connection.Setup(x => x.GetGitChanges(id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.ListFiles(id, string.Empty, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [Entry("src", true), Entry("top.txt", false)] });
        connection.Setup(x => x.ListFiles(id, "src", It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = "src", Parent = string.Empty, Entries = [Entry("src/deep", true), Entry("src/a.txt", false)] });
        connection.Setup(x => x.ListFiles(id, "src/deep", It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = "src/deep", Parent = "src", Entries = [Entry("src/deep/b.txt", false)] });
        connection.Setup(x => x.ReadFile(id, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid _, string path, CancellationToken _) => new FileContent { Path = path, Text = "text" });
        return new FilesViewModel(connection.Object, id, new GitViewModel(connection.Object, id), desktop);
    }

    private async Task Settle() => await Task.Delay(100);

    [Fact]
    public async Task ShowHidden_ReloadsTheTreeWithTheFlag()
    {
        FilesViewModel files = Create(true);
        connection.Setup(x => x.ListFiles(id, string.Empty, true, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [Entry("src", true), Entry("top.txt", false), Entry("secret.txt", false)] });
        await files.Start();

        Assert.Equal(2, files.Nodes.Count);

        files.ShowHidden = true;
        await Settle();

        Assert.Equal(["src", "top.txt", "secret.txt"], files.Nodes.Select(node => node.Name));
        connection.Verify(x => x.ListFiles(id, string.Empty, true, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Desktop_OpeningAFileSelectsItAndOpensItsFolders()
    {
        FilesViewModel files = Create(true);
        await files.Start();
        files.Nodes[0].IsExpanded = true;
        await Settle();
        files.Nodes[0].Children[0].IsExpanded = true;
        await Settle();

        await files.Choose(files.Nodes[0].Children[0].Children[0]);
        await files.Choose(files.Nodes[1]);
        await Settle();

        Assert.Same(files.Nodes[1], files.Selected);
        files.Nodes[0].IsExpanded = false;
        await files.Choose(files.Nodes[0].Children[0].Children[0]);
        await Settle();
        Assert.Equal("src/deep/b.txt", files.Selected?.Path);
        Assert.True(files.Nodes[0].IsExpanded);
    }

    [Fact]
    public async Task Desktop_AFolderAboveTheOpenFileCanBeCollapsedAndStaysCollapsed()
    {
        FilesViewModel files = Create(true);
        await files.Start();
        files.Nodes[0].IsExpanded = true;
        await Settle();
        files.Nodes[0].Children[0].IsExpanded = true;
        await Settle();
        await files.Choose(files.Nodes[0].Children[0].Children[0]);
        await Settle();

        await files.Choose(files.Nodes[0].Children[0]);
        await Settle();

        Assert.False(files.Nodes[0].Children[0].IsExpanded);
        Assert.True(files.Nodes[0].IsExpanded);
        Assert.Null(files.Selected);

        await files.Choose(files.Nodes[0]);
        await Settle();

        Assert.False(files.Nodes[0].IsExpanded);
        Assert.False(files.Nodes[0].Children[0].IsExpanded);
        Assert.Equal("src/deep/b.txt", files.Preview?.Path);
    }

    [Fact]
    public async Task Desktop_ReadingTheSameFileAgainDoesNotOpenItsCollapsedFoldersButAnotherFileDoes()
    {
        FilesViewModel files = Create(true);
        await files.Start();
        files.Nodes[0].IsExpanded = true;
        await Settle();
        FileNode file = files.Nodes[0].Children[1];
        await files.Choose(file);
        await Settle();
        files.Nodes[0].IsExpanded = false;

        await files.Choose(file);
        await Settle();

        Assert.False(files.Nodes[0].IsExpanded);

        files.Nodes[0].Children[0].IsExpanded = true;
        await Settle();
        await files.Choose(files.Nodes[0].Children[0].Children[0]);
        await Settle();

        Assert.True(files.Nodes[0].IsExpanded);
        Assert.Equal("src/deep/b.txt", files.Selected?.Path);
    }

    [Fact]
    public async Task Desktop_ARevealedFileInACollapsedTreeLoadsAndOpensEveryFolderAboveIt()
    {
        FilesViewModel files = Create(true);
        await files.Start();
        connection.Setup(x => x.GetGitDiff(id, "src/deep/b.txt", null, It.IsAny<CancellationToken>())).ReturnsAsync(new GitDiff { Path = "src/deep/b.txt", Lines = [] });
        files.Git.RequestDiff("src/deep/b.txt", null);
        await Settle();

        Assert.True(files.Nodes[0].IsExpanded);
        Assert.True(files.Nodes[0].Children[0].IsExpanded);
        Assert.Equal("src/deep/b.txt", files.Selected?.Path);
    }

    [Fact]
    public async Task Desktop_TappingAFolderKeepsTheOpenFileSelectedAndClosingThePreviewClearsIt()
    {
        FilesViewModel files = Create(true);
        await files.Start();
        await files.Choose(files.Nodes[1]);
        await Settle();

        await files.Choose(files.Nodes[0]);
        await Settle();

        Assert.True(files.Nodes[0].IsExpanded);
        Assert.Same(files.Nodes[1], files.Selected);
        files.ClosePreviewCommand.Execute(null);
        await Settle();
        Assert.Null(files.Selected);
    }

    [Fact]
    public async Task Desktop_HasNoNavigationButOtherLayoutsDo()
    {
        FilesViewModel desktop = Create(true);
        FilesViewModel mobile = Create(false);

        Assert.False(desktop.ShowNavigation);
        Assert.True(mobile.ShowNavigation);
        await mobile.Start();
        await mobile.Choose(mobile.Nodes[1]);
        await Settle();
        Assert.Null(mobile.Selected);
    }

    [Theory]
    [InlineData("/work/app", "src/a.txt", "/work/app/src/a.txt")]
    [InlineData("/work/app/", "src", "/work/app/src")]
    [InlineData("C:\\work\\app", "src/a.txt", "C:\\work\\app\\src\\a.txt")]
    [InlineData("/work/app", "", "/work/app")]
    public void AbsolutePath_JoinsTheWorkspaceFolderWithItsOwnSeparator(string folder, string relative, string expected)
    {
        WorkspaceViewModel model = new(connection.Object, new WorkspaceInfo { Id = id, Folder = folder });

        Assert.Equal(expected, model.AbsolutePath(relative));
    }

    [Fact]
    public async Task History_ShowsABranchAndItsRemoteCopyOnTheSameCommitAsOneLabel()
    {
        connection.Setup(x => x.GetGitStatus(id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = true, Branch = "main", Commit = "abc1234" });
        connection.Setup(x => x.GetGitChanges(id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        GitRef[] refs = [new GitRef { Name = "main", Kind = GitRefKind.Branch, IsCurrent = true }, new GitRef { Name = "origin/main", Kind = GitRefKind.Remote }, new GitRef { Name = "origin/other", Kind = GitRefKind.Remote }, new GitRef { Name = "v1", Kind = GitRefKind.Tag }];
        connection.Setup(x => x.GetGitHistory(id, 0, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GitHistory { Commits = [new GitCommit { Hash = "2222222", Subject = "s", Author = "Ann", Date = DateTimeOffset.UnixEpoch, Refs = refs }] });
        GitViewModel git = new(connection.Object, id);

        await git.History.Start();

        GitCommitViewModel commit = git.History.Commits[0];
        Assert.Equal(["main", "origin/other", "v1"], commit.Refs.Select(item => item.Name));
        Assert.Equal("origin", commit.Refs[0].RemoteLabel);
        Assert.True(commit.Refs[0].HasRemote);
        Assert.False(commit.Refs[1].HasRemote);
    }
}
