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
    public async Task WindowTitle_FollowsTheOpenWorkspaceAndFallsBackToTheAppName()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/acme" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        store.Setup(x => x.Load(It.IsAny<CancellationToken>())).ReturnsAsync(saved);
        factory.Setup(x => x.Connect(saved, It.IsAny<CancellationToken>())).ReturnsAsync(connection.Object);
        List<string?> raised = [];
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await model.Start();
        HomeViewModel home = Assert.IsType<HomeViewModel>(model.Current);
        Assert.Equal("Auto Remote", model.WindowTitle);

        home.Selected = workspace;

        Assert.Equal("acme", model.WindowTitle);
        Assert.Contains(nameof(MainViewModel.WindowTitle), raised);
    }

    [Fact]
    public async Task Focus_OpensTheWorkspaceOnceTheAppConnectsWhenItWasAskedBeforehand()
    {
        WorkspaceInfo wanted = new() { Id = Guid.NewGuid(), Folder = "/work/wanted" };
        WorkspaceInfo other = new() { Id = Guid.NewGuid(), Folder = "/work/other" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [other, wanted] });
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0 });
        store.Setup(x => x.Load(It.IsAny<CancellationToken>())).ReturnsAsync(saved);
        factory.Setup(x => x.Connect(saved, It.IsAny<CancellationToken>())).ReturnsAsync(connection.Object);

        await model.Focus(wanted.Id);
        await model.Start();

        Assert.Equal(wanted, Assert.IsType<HomeViewModel>(model.Current).Selected);
    }

    [Fact]
    public async Task Focus_SwitchesAnAlreadyOpenAppToTheWorkspaceEvenFromAnotherScreen()
    {
        WorkspaceInfo first = new() { Id = Guid.NewGuid(), Folder = "/work/first" };
        WorkspaceInfo second = new() { Id = Guid.NewGuid(), Folder = "/work/second" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [first, second] });
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0 });
        store.Setup(x => x.Load(It.IsAny<CancellationToken>())).ReturnsAsync(saved);
        factory.Setup(x => x.Connect(saved, It.IsAny<CancellationToken>())).ReturnsAsync(connection.Object);
        await model.Start();
        HomeViewModel home = Assert.IsType<HomeViewModel>(model.Current);
        home.NewWorkspaceCommand.Execute(null);
        Assert.IsNotType<HomeViewModel>(model.Current);

        await model.Focus(second.Id);

        Assert.Same(home, model.Current);
        Assert.Equal(second, home.Selected);
    }
}
