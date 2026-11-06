namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class FilesNavigationTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly Guid id = Guid.NewGuid();

    private readonly GitViewModel git;

    private readonly FilesViewModel files;

    public FilesNavigationTests()
    {
        git = new GitViewModel(connection.Object, id);
        connection.Setup(x => x.GetGitStatus(id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = true });
        connection.Setup(x => x.GetGitChanges(id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.ListFiles(id, string.Empty, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [Entry("a.txt"), Entry("b.txt"), Entry("c.txt")] });
        connection.Setup(x => x.ReadFile(id, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid _, string path, CancellationToken _) => new FileContent { Path = path, Text = "text of " + path });
        connection.Setup(x => x.GetGitDiff(id, "a.txt", null, It.IsAny<CancellationToken>())).ReturnsAsync(new GitDiff { Path = "a.txt", Lines = [] });
        connection.Setup(x => x.GetGitDiff(id, "b.txt", "abc1234def", It.IsAny<CancellationToken>())).ReturnsAsync(new GitDiff { Path = "b.txt", Lines = [] });
        files = new FilesViewModel(connection.Object, id, git, true, TimeSpan.Zero);
    }

    private FileEntry Entry(string path) => new() { Name = path, Path = path, IsFolder = false };

    private FileNode Node(string path) => files.Nodes.Single(node => node.Path == path);

    private async Task Settle() => await Task.Delay(100);

    private string Showing => files.Preview?.Path ?? files.PreviewTitle;

    [Fact]
    public async Task Back_And_Forward_AreUnavailableUntilThereIsSomewhereToGo()
    {
        await files.Start();
        Assert.False(files.BackCommand.CanExecute(null));
        Assert.False(files.ForwardCommand.CanExecute(null));

        await files.Choose(Node("a.txt"));
        Assert.False(files.BackCommand.CanExecute(null));

        await files.Choose(Node("b.txt"));
        Assert.True(files.BackCommand.CanExecute(null));
        Assert.False(files.ForwardCommand.CanExecute(null));
    }

    [Fact]
    public async Task Back_And_Forward_WalkThroughTheFilesOpenedInOrder()
    {
        await files.Start();
        await files.Choose(Node("a.txt"));
        await files.Choose(Node("b.txt"));
        await files.Choose(Node("c.txt"));

        files.BackCommand.Execute(null);
        await Settle();
        Assert.Equal("b.txt", Showing);
        files.BackCommand.Execute(null);
        await Settle();
        Assert.Equal("a.txt", Showing);
        Assert.False(files.BackCommand.CanExecute(null));
        Assert.True(files.ForwardCommand.CanExecute(null));

        files.ForwardCommand.Execute(null);
        await Settle();
        files.ForwardCommand.Execute(null);
        await Settle();
        Assert.Equal("c.txt", Showing);
        Assert.False(files.ForwardCommand.CanExecute(null));
    }

    [Fact]
    public async Task Opening_AnotherFileAfterGoingBack_DropsWhatWasAhead()
    {
        await files.Start();
        await files.Choose(Node("a.txt"));
        await files.Choose(Node("b.txt"));
        files.BackCommand.Execute(null);
        await Settle();

        await files.Choose(Node("c.txt"));

        Assert.False(files.ForwardCommand.CanExecute(null));
        files.BackCommand.Execute(null);
        await Settle();
        Assert.Equal("a.txt", Showing);
    }

    [Fact]
    public async Task Opening_TheFileThatIsAlreadyShown_IsNotAnotherStep()
    {
        await files.Start();
        await files.Choose(Node("a.txt"));

        await files.Choose(Node("a.txt"));

        Assert.False(files.BackCommand.CanExecute(null));
    }

    [Fact]
    public async Task Diffs_AreStepsToo_AndComeBackAsTheSameDiff()
    {
        await files.Start();
        await files.Choose(Node("a.txt"));
        git.RequestDiff("a.txt", null);
        await Settle();
        git.RequestDiff("b.txt", "abc1234def");
        await Settle();
        Assert.True(files.IsDiffPreview);

        files.BackCommand.Execute(null);
        await Settle();
        Assert.Equal("a.txt (changes)", files.PreviewTitle);
        files.BackCommand.Execute(null);
        await Settle();
        Assert.False(files.IsDiffPreview);
        Assert.Equal("a.txt", files.Preview?.Path);
        files.ForwardCommand.Execute(null);
        await Settle();
        files.ForwardCommand.Execute(null);
        await Settle();

        Assert.Equal("b.txt (abc1234)", files.PreviewTitle);
        connection.Verify(x => x.GetGitDiff(id, "b.txt", "abc1234def", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Reading_TheOpenFileAgainAfterChangesIsNotAStep()
    {
        await files.Start();
        await files.Choose(Node("a.txt"));
        await files.Choose(Node("b.txt"));

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.FilesChanged, WorkspaceId = id, Path = string.Empty });
        await Settle();

        files.BackCommand.Execute(null);
        await Settle();
        Assert.Equal("a.txt", Showing);
        Assert.False(files.BackCommand.CanExecute(null));
    }

    [Fact]
    public async Task Back_StaysWhereItIsAndShowsWhyWhenTheFileIsGone()
    {
        await files.Start();
        await files.Choose(Node("a.txt"));
        await files.Choose(Node("b.txt"));
        connection.Setup(x => x.ReadFile(id, "a.txt", It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("The file 'a.txt' does not exist."));

        files.BackCommand.Execute(null);
        await Settle();

        Assert.Equal("b.txt", Showing);
        Assert.True(files.BackCommand.CanExecute(null));
        Assert.Equal("The file 'a.txt' does not exist.", files.Error);
    }

    [Fact]
    public async Task ClosingThePreview_ForgetsTheStepsSoBackStartsFresh()
    {
        await files.Start();
        await files.Choose(Node("a.txt"));
        await files.Choose(Node("b.txt"));

        files.ClosePreviewCommand.Execute(null);

        Assert.False(files.BackCommand.CanExecute(null));
        Assert.False(files.ForwardCommand.CanExecute(null));
        Assert.False(files.IsPreviewing);
    }
}
