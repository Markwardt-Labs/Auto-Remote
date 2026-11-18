namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ConnectViewModelTests
{
    private readonly Mock<IServerConnection.IFactory> factory = new();

    [Fact]
    public void Constructor_PrefillsSavedSettings()
    {
        ConnectViewModel model = new(factory.Object, new ConnectionSettings { Host = "h", Port = 9, AccessKey = "k" }, "v");

        Assert.Equal(("h", "9", "k"), (model.Host, model.Port, model.AccessKey));
    }

    [Fact]
    public void Constructor_DefaultsThePortWithoutSavedSettings() => Assert.Equal("52679", new ConnectViewModel(factory.Object, null, "v").Port);

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 250 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }
    }

    private static LocalServer Server(int port) => new() { Port = port, Version = "1.0.0" };

    [Fact]
    public async Task Watch_OffersOneButtonForASingleLocalServerAndNamesThePortWhenThereAreSeveral()
    {
        Mock<ILocalServers> locals = new();
        locals.Setup(x => x.Find(It.IsAny<CancellationToken>())).ReturnsAsync([Server(5000)]);
        ConnectViewModel model = new(factory.Object, null, "v", locals.Object);

        Task watching = model.Watch();
        await WaitFor(() => model.HasLocalServers);

        Assert.Equal("Connect to Local Server", Assert.Single(model.LocalServers).Text);

        locals.Setup(x => x.Find(It.IsAny<CancellationToken>())).ReturnsAsync([Server(5000), Server(6000)]);
        await WaitFor(() => model.LocalServers.Count == 2);

        Assert.Equal(["Connect to Local Server (Port 5000)", "Connect to Local Server (Port 6000)"], model.LocalServers.Select(choice => choice.Text));

        model.Stop();
        await watching;
    }

    [Fact]
    public async Task Watch_HidesTheButtonsOnceNoLocalServerAnswersAndDoesNothingWithoutAWayToFindThem()
    {
        Mock<ILocalServers> locals = new();
        locals.Setup(x => x.Find(It.IsAny<CancellationToken>())).ReturnsAsync([Server(5000)]);
        ConnectViewModel model = new(factory.Object, null, "v", locals.Object);
        Task watching = model.Watch();
        await WaitFor(() => model.HasLocalServers);

        locals.Setup(x => x.Find(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        await WaitFor(() => !model.HasLocalServers);

        Assert.Empty(model.LocalServers);
        model.Stop();
        await watching;

        ConnectViewModel plain = new(factory.Object, null, "v");
        await plain.Watch();

        Assert.False(plain.HasLocalServers);
    }

    [Fact]
    public async Task Stop_EndsTheSearchForLocalServers()
    {
        Mock<ILocalServers> locals = new();
        locals.Setup(x => x.Find(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        ConnectViewModel model = new(factory.Object, null, "v", locals.Object);
        Task watching = model.Watch();
        await WaitFor(() => locals.Invocations.Count > 0);

        model.Stop();
        await watching;
        int searches = locals.Invocations.Count;
        await Task.Delay(100);

        Assert.Equal(searches, locals.Invocations.Count);
    }

    [Fact]
    public async Task TheLocalButton_ConnectsAsALocalServerWithoutAKeyAndReportsThoseSettings()
    {
        Mock<ILocalServers> locals = new();
        locals.Setup(x => x.Find(It.IsAny<CancellationToken>())).ReturnsAsync([Server(5000)]);
        Mock<IServerConnection> connection = new();
        factory.Setup(x => x.Connect(It.IsAny<ConnectionSettings>(), It.IsAny<CancellationToken>())).ReturnsAsync(connection.Object);
        ConnectViewModel model = new(factory.Object, null, "v", locals.Object);
        List<(IServerConnection Connection, ConnectionSettings Settings)> connected = [];
        model.Connected += (opened, settings) => connected.Add((opened, settings));
        Task watching = model.Watch();
        await WaitFor(() => model.HasLocalServers);

        model.LocalServers[0].Command.Execute(null);
        await WaitFor(() => connected.Count > 0);

        (IServerConnection Connection, ConnectionSettings Settings) result = Assert.Single(connected);
        Assert.Same(connection.Object, result.Connection);
        Assert.Equal(new ConnectionSettings { Host = "127.0.0.1", Port = 5000, AccessKey = string.Empty, Local = true }, result.Settings);
        factory.Verify(x => x.Connect(result.Settings, It.IsAny<CancellationToken>()), Times.Once);
        model.Stop();
        await watching;
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException), "not in localUsers")]
    [InlineData(typeof(IOException), "no server")]
    public async Task TheLocalButton_ShowsWhyTheConnectionFailed(Type failure, string message)
    {
        Mock<ILocalServers> locals = new();
        locals.Setup(x => x.Find(It.IsAny<CancellationToken>())).ReturnsAsync([Server(5000)]);
        factory.Setup(x => x.Connect(It.IsAny<ConnectionSettings>(), It.IsAny<CancellationToken>())).ThrowsAsync((Exception)Activator.CreateInstance(failure, message)!);
        ConnectViewModel model = new(factory.Object, null, "v", locals.Object);
        bool connected = false;
        model.Connected += (_, _) => connected = true;
        Task watching = model.Watch();
        await WaitFor(() => model.HasLocalServers);

        model.LocalServers[0].Command.Execute(null);
        await WaitFor(() => model.HasError);

        Assert.Equal(message, model.Error);
        Assert.False(connected);
        model.Stop();
        await watching;
    }
}
