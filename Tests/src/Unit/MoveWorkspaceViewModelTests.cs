namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class MoveWorkspaceViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };

    private readonly FolderListing work = new() { Path = "/work", Parent = "/", Folders = [new FolderEntry { Name = "other", Path = "/work/other" }] };

    public MoveWorkspaceViewModelTests() => connection.Setup(x => x.ListFolders("/work", It.IsAny<CancellationToken>())).ReturnsAsync(work);

    [Fact]
    public async Task Open_ShowsTheFolderTheWorkspaceIsInAndSaysWhatMovesWhere()
    {
        MoveWorkspaceViewModel model = new(connection.Object, workspace);

        await model.Open();

        Assert.Equal("/work", model.Browser.CurrentPath);
        Assert.Contains("'app'", model.Description);
        Assert.Contains("No files are moved", model.Description);
        Assert.True(model.MoveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Move_MovesTheFolderIntoTheFolderBeingViewedAndRaisesMoved()
    {
        WorkspaceInfo moved = workspace with { Folder = "/work/other/app" };
        connection.Setup(x => x.MoveWorkspaceToFolder(workspace.Id, "/work", It.IsAny<CancellationToken>())).ReturnsAsync(moved);
        MoveWorkspaceViewModel model = new(connection.Object, workspace);
        WorkspaceInfo? raised = null;
        model.Moved += result => raised = result;
        await model.Open();

        model.MoveCommand.Execute(null);
        for (int attempt = 0; attempt < 500 && raised is null; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(moved, raised);
    }

    [Fact]
    public async Task Move_ShowsWhyTheServerRefusedAndStaysOnTheScreen()
    {
        connection.Setup(x => x.MoveWorkspaceToFolder(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("'app' already exists in '/work'."));
        MoveWorkspaceViewModel model = new(connection.Object, workspace);
        bool moved = false;
        model.Moved += _ => moved = true;
        await model.Open();

        model.MoveCommand.Execute(null);
        for (int attempt = 0; attempt < 500 && !model.HasError; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Equal("'app' already exists in '/work'.", model.Error);
        Assert.False(moved);
    }

    [Fact]
    public async Task Open_StartsAtTheDefaultFolderWhenTheFolderTheWorkspaceWasInIsGoneToo()
    {
        connection.Setup(x => x.ListFolders("/work", It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("The folder '/work' does not exist."));
        connection.Setup(x => x.ListFolders(null, It.IsAny<CancellationToken>())).ReturnsAsync(new FolderListing { Path = "/home/me", Parent = "/home", Folders = [] });
        MoveWorkspaceViewModel model = new(connection.Object, workspace);

        await model.Open();

        Assert.Equal("/home/me", model.Browser.CurrentPath);
        Assert.False(model.HasError);
        Assert.False(model.Browser.HasError);
    }

    [Fact]
    public async Task Open_StartsAtTheDefaultFolderWhenTheWorkspaceIsInARootFolder()
    {
        connection.Setup(x => x.ListFolders(null, It.IsAny<CancellationToken>())).ReturnsAsync(work);
        MoveWorkspaceViewModel model = new(connection.Object, new WorkspaceInfo { Id = Guid.NewGuid(), Folder = "app" });

        await model.Open();

        connection.Verify(x => x.ListFolders(null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Cancel_RaisesCancelled()
    {
        MoveWorkspaceViewModel model = new(connection.Object, workspace);
        bool cancelled = false;
        model.Cancelled += () => cancelled = true;

        model.CancelCommand.Execute(null);

        Assert.True(cancelled);
    }
}
