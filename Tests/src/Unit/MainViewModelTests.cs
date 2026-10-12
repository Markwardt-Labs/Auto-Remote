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

}
