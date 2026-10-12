namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class NewWorkspaceViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly FolderListing home = new()
    {
        Path = "/home/me",
        Parent = "/home",
        Folders = [new FolderEntry { Name = "code", Path = "/home/me/code" }],
    };

    public NewWorkspaceViewModelTests() => connection.Setup(x => x.ListFolders(null, It.IsAny<CancellationToken>())).ReturnsAsync(home);

    [Fact]
    public async Task Open_ShowsHomeFolder()
    {
        NewWorkspaceViewModel model = new(connection.Object);

        await model.Open();

        Assert.Equal("/home/me", model.Browser.CurrentPath);
        Assert.Equal(home.Folders, model.Browser.Folders);
        Assert.True(model.Browser.CanGoUp);
        Assert.True(model.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Selecting_OpensFolderAndUpGoesBack()
    {
        FolderListing code = new() { Path = "/home/me/code", Parent = "/home/me", Folders = [] };
        connection.Setup(x => x.ListFolders("/home/me/code", It.IsAny<CancellationToken>())).ReturnsAsync(code);
        connection.Setup(x => x.ListFolders("/home/me", It.IsAny<CancellationToken>())).ReturnsAsync(home);
        NewWorkspaceViewModel model = new(connection.Object);
        await model.Open();

        model.Browser.Selected = home.Folders[0];
        await Task.Delay(100);
        Assert.Equal("/home/me/code", model.Browser.CurrentPath);

        model.Browser.UpCommand.Execute(null);
        await Task.Delay(100);
        Assert.Equal("/home/me", model.Browser.CurrentPath);
    }

    [Fact]
    public async Task CreateFolder_CreatesInCurrentFolderAndEntersIt()
    {
        FolderListing created = new() { Path = "/home/me/new", Parent = "/home/me", Folders = [] };
        connection.Setup(x => x.CreateFolder("/home/me", "new", It.IsAny<CancellationToken>())).ReturnsAsync(created);
        NewWorkspaceViewModel model = new(connection.Object);
        await model.Open();

        model.Browser.NewFolderName = "new";
        model.Browser.CreateFolderCommand.Execute(null);
        await Task.Delay(100);

        Assert.Equal("/home/me/new", model.Browser.CurrentPath);
        Assert.Equal(string.Empty, model.Browser.NewFolderName);
    }

    [Fact]
    public async Task Start_CreatesWorkspaceInCurrentFolder()
    {
        WorkspaceInfo created = new() { Id = Guid.NewGuid(), Folder = "/home/me" };
        connection.Setup(x => x.CreateWorkspace("/home/me", It.IsAny<CancellationToken>())).ReturnsAsync(created);
        NewWorkspaceViewModel model = new(connection.Object);
        WorkspaceInfo? raised = null;
        model.Created += workspace => raised = workspace;
        await model.Open();

        model.StartCommand.Execute(null);
        await Task.Delay(100);

        Assert.Equal(created, raised);
    }

    [Fact]
    public async Task Start_ShowsServerRefusal()
    {
        connection.Setup(x => x.CreateWorkspace(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("already hosts a workspace"));
        NewWorkspaceViewModel model = new(connection.Object);
        await model.Open();

        model.StartCommand.Execute(null);
        await Task.Delay(100);

        Assert.Equal("already hosts a workspace", model.Error);
    }

    [Fact]
    public void Cancel_RaisesCancelled()
    {
        NewWorkspaceViewModel model = new(connection.Object);
        bool cancelled = false;
        model.Cancelled += () => cancelled = true;

        model.CancelCommand.Execute(null);

        Assert.True(cancelled);
    }
}
