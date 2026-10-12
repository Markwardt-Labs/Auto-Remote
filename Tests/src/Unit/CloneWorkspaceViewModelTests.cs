namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class CloneWorkspaceViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly FolderListing work = new() { Path = "/work", Parent = "/", Folders = [] };

    public CloneWorkspaceViewModelTests() => connection.Setup(x => x.ListFolders(null, It.IsAny<CancellationToken>())).ReturnsAsync(work);

    [Fact]
    public async Task Clone_NeedsAUrlAndAFolderThenClonesIntoTheCurrentFolder()
    {
        WorkspaceInfo cloned = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.CloneWorkspace("https://x/app.git", "/work", null, It.IsAny<CancellationToken>())).ReturnsAsync(cloned);
        CloneWorkspaceViewModel model = new(connection.Object);
        WorkspaceInfo? created = null;
        model.Created += workspace => created = workspace;

        Assert.False(model.CloneCommand.CanExecute(null));
        await model.Open();
        Assert.False(model.CloneCommand.CanExecute(null));
        model.Url = "  https://x/app.git ";
        Assert.True(model.CloneCommand.CanExecute(null));
        model.CloneCommand.Execute(null);
        await Task.Delay(100);

        Assert.Equal(cloned, created);
    }

    [Fact]
    public async Task Clone_PassesTheTypedFolderName()
    {
        connection.Setup(x => x.CloneWorkspace("https://x/app.git", "/work", "mine", It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceInfo { Id = Guid.NewGuid(), Folder = "/work/mine" });
        CloneWorkspaceViewModel model = new(connection.Object) { Url = "https://x/app.git", Name = " mine " };
        await model.Open();

        model.CloneCommand.Execute(null);
        await Task.Delay(100);

        connection.Verify(x => x.CloneWorkspace("https://x/app.git", "/work", "mine", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Clone_ShowsTheServersFailureAndStaysOpen()
    {
        connection.Setup(x => x.CloneWorkspace(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("fatal: repository not found"));
        CloneWorkspaceViewModel model = new(connection.Object) { Url = "https://x/missing.git" };
        bool created = false;
        model.Created += _ => created = true;
        await model.Open();

        model.CloneCommand.Execute(null);
        await Task.Delay(100);

        Assert.Equal("fatal: repository not found", model.Error);
        Assert.False(created);
        Assert.True(model.CloneCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancel_RaisesCancelled()
    {
        CloneWorkspaceViewModel model = new(connection.Object);
        bool cancelled = false;
        model.Cancelled += () => cancelled = true;
        await model.Open();

        model.CancelCommand.Execute(null);

        Assert.True(cancelled);
    }
}
