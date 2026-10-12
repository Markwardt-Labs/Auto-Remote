namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class MainViewModelTests
{
    private readonly Mock<IAppInfo> info = new();

    private readonly Mock<IServerConnection.IFactory> factory = new();

    private readonly Mock<ISettingsStore> store = new();

    private readonly Mock<IServerConnection> connection = new();

    private readonly ConnectionSettings saved = new() { Host = "box", Port = 5, AccessKey = "k" };

    private readonly MainViewModel model;

    public MainViewModelTests()
    {
        info.SetupGet(x => x.Name).Returns("Auto Remote");
        info.SetupGet(x => x.Version).Returns("1.0.0");
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [] });
        model = new MainViewModel(info.Object, factory.Object, store.Object);
    }

    [Fact]
    public async Task Start_WithoutSavedSettings_AsksForServer()
    {
        await model.Start();

        Assert.IsType<ConnectViewModel>(model.Current);
        factory.Verify(x => x.Connect(It.IsAny<ConnectionSettings>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Start_WithSavedSettings_ConnectsStraightToWorkspaces()
    {
        store.Setup(x => x.Load(It.IsAny<CancellationToken>())).ReturnsAsync(saved);
        factory.Setup(x => x.Connect(saved, It.IsAny<CancellationToken>())).ReturnsAsync(connection.Object);

        await model.Start();

        Assert.IsType<HomeViewModel>(model.Current);
    }

    [Fact]
    public async Task Start_WhenSavedServerUnreachable_ShowsPrefilledConnectWithError()
    {
        store.Setup(x => x.Load(It.IsAny<CancellationToken>())).ReturnsAsync(saved);
        factory.Setup(x => x.Connect(saved, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("timed out"));

        await model.Start();

        ConnectViewModel connect = Assert.IsType<ConnectViewModel>(model.Current);
        Assert.Equal("box", connect.Host);
        Assert.Equal("timed out", connect.Error);
    }

    [Fact]
    public async Task Connecting_SavesSettingsAndShowsHome()
    {
        factory.Setup(x => x.Connect(It.IsAny<ConnectionSettings>(), It.IsAny<CancellationToken>())).ReturnsAsync(connection.Object);
        await model.Start();
        ConnectViewModel connect = Assert.IsType<ConnectViewModel>(model.Current);
        connect.Host = "box";
        connect.Port = "5";
        connect.AccessKey = "k";

        connect.ConnectCommand.Execute(null);
        await Task.Delay(200);

        store.Verify(x => x.Save(saved, It.IsAny<CancellationToken>()), Times.Once);
        Assert.IsType<HomeViewModel>(model.Current);
    }

    [Fact]
    public async Task NewWorkspace_CreatesWorkspaceAndFocusesIt()
    {
        WorkspaceInfo created = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        store.Setup(x => x.Load(It.IsAny<CancellationToken>())).ReturnsAsync(saved);
        factory.Setup(x => x.Connect(saved, It.IsAny<CancellationToken>())).ReturnsAsync(connection.Object);
        connection.Setup(x => x.ListFolders(null, It.IsAny<CancellationToken>())).ReturnsAsync(new FolderListing { Path = "/work/app", Folders = [] });
        connection.Setup(x => x.CreateWorkspace("/work/app", It.IsAny<CancellationToken>())).ReturnsAsync(created);
        await model.Start();
        HomeViewModel home = Assert.IsType<HomeViewModel>(model.Current);
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [created] });

        home.NewWorkspaceCommand.Execute(null);
        await Task.Delay(100);
        NewWorkspaceViewModel creating = Assert.IsType<NewWorkspaceViewModel>(model.Current);
        creating.StartCommand.Execute(null);
        await Task.Delay(200);

        HomeViewModel after = Assert.IsType<HomeViewModel>(model.Current);
        Assert.Equal(created, after.Selected);
        connection.Verify(x => x.DisposeAsync(), Times.Never);
    }

    [Fact]
    public async Task ChangeServer_ClosesConnectionAndShowsConnect()
    {
        store.Setup(x => x.Load(It.IsAny<CancellationToken>())).ReturnsAsync(saved);
        factory.Setup(x => x.Connect(saved, It.IsAny<CancellationToken>())).ReturnsAsync(connection.Object);
        await model.Start();

        Assert.IsType<HomeViewModel>(model.Current).ChangeServerCommand.Execute(null);
        await Task.Delay(100);

        Assert.IsType<ConnectViewModel>(model.Current);
        connection.Verify(x => x.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task CloneWorkspace_ClonesOnTheServerAndFocusesTheWorkspace()
    {
        WorkspaceInfo cloned = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        store.Setup(x => x.Load(It.IsAny<CancellationToken>())).ReturnsAsync(saved);
        factory.Setup(x => x.Connect(saved, It.IsAny<CancellationToken>())).ReturnsAsync(connection.Object);
        connection.Setup(x => x.ListFolders(null, It.IsAny<CancellationToken>())).ReturnsAsync(new FolderListing { Path = "/work", Folders = [] });
        connection.Setup(x => x.CloneWorkspace("https://x/app.git", "/work", null, It.IsAny<CancellationToken>())).ReturnsAsync(cloned);
        await model.Start();
        HomeViewModel home = Assert.IsType<HomeViewModel>(model.Current);
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [cloned] });

        home.CloneWorkspaceCommand.Execute(null);
        await Task.Delay(100);
        CloneWorkspaceViewModel cloning = Assert.IsType<CloneWorkspaceViewModel>(model.Current);
        cloning.Url = "https://x/app.git";
        cloning.CloneCommand.Execute(null);
        await Task.Delay(200);

        Assert.Equal(cloned, Assert.IsType<HomeViewModel>(model.Current).Selected);
    }

    [Fact]
    public async Task CloneWorkspace_CancelGoesBackToTheWorkspaces()
    {
        store.Setup(x => x.Load(It.IsAny<CancellationToken>())).ReturnsAsync(saved);
        factory.Setup(x => x.Connect(saved, It.IsAny<CancellationToken>())).ReturnsAsync(connection.Object);
        connection.Setup(x => x.ListFolders(null, It.IsAny<CancellationToken>())).ReturnsAsync(new FolderListing { Path = "/work", Folders = [] });
        await model.Start();
        HomeViewModel home = Assert.IsType<HomeViewModel>(model.Current);

        home.CloneWorkspaceCommand.Execute(null);
        await Task.Delay(100);
        Assert.IsType<CloneWorkspaceViewModel>(model.Current).CancelCommand.Execute(null);

        Assert.Same(home, model.Current);
    }
}
