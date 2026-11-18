namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class LocalConnectTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly List<RunningServer> servers = [];

    public void Dispose()
    {
        foreach (RunningServer server in servers)
        {
            server.Dispose();
        }

        temp.Dispose();
    }

    private ILocalEndpoint Endpoint()
    {
        LocalEndpoint real = new(_ => null);
        Mock<ILocalEndpoint> endpoint = new();
        endpoint.SetupGet(item => item.Folders).Returns([temp.Combine("sockets")]);
        endpoint.Setup(item => item.SocketPath(It.IsAny<string>(), It.IsAny<int>())).Returns((string folder, int port) => real.SocketPath(folder, port));
        endpoint.Setup(item => item.PipeName(It.IsAny<int>())).Returns((int port) => real.PipeName(port));
        endpoint.Setup(item => item.Port(It.IsAny<string>())).Returns((string name) => real.Port(name));
        return endpoint.Object;
    }

    private async Task<RunningServer> Start(IReadOnlyList<string>? users, int port = 0)
    {
        Mock<IRequestHandler> handler = new();
        handler.Setup(item => item.Handle(It.IsAny<RemoteRequest>(), It.IsAny<object>(), It.IsAny<CancellationToken>())).ReturnsAsync((RemoteRequest request, object _, CancellationToken _) => new RemoteResponse { Id = request.Id, Catalog = new WorkspaceCatalog { Groups = [], Workspaces = [] } });
        IAppInfo info = Mock.Of<IAppInfo>(item => item.Version == "1.2.3");
        ILocalListener local = new LocalListener(info, Endpoint(), new ILocalAccess.Factory(), Mock.Of<ILog>());
        ConnectionServer server = new(new Handshake(info), new IMessageChannel.Factory(), new WebSocketUpgrade(), new CertificateProvider(), handler.Object, new EventHub(), Mock.Of<ILog>(), local);
        TaskCompletionSource<int> listening = new();
        string fingerprint = string.Empty;
        server.Listening += value => listening.SetResult(value);
        server.Certified += value => fingerprint = value;
        CancellationTokenSource stopping = new();
        Task running = server.Run(new ServerConfig { Port = port, Key = "secret", DataFolder = temp.Combine("data"), LocalUsers = users }, stopping.Token);
        RunningServer started = new(stopping, running, await listening.Task, fingerprint);
        servers.Add(started);
        return started;
    }

    private string Me => Environment.UserName;

    private string Socket(RunningServer server) => Path.Combine(temp.Combine("sockets"), $"{server.Port}.sock");

    [Fact]
    public async Task Find_ListsAServerThatAllowsThisAccountAndOpenHandsOverEverythingAConnectionNeeds()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        RunningServer server = await Start([Me]);
        LocalServers locals = new(Endpoint());

        IReadOnlyList<LocalServer> found = await locals.Find(CancellationToken.None);
        LocalHandover handover = await locals.Open(server.Port, CancellationToken.None);

        LocalServer single = Assert.Single(found);
        Assert.Equal((server.Port, "1.2.3"), (single.Port, single.Version));
        Assert.Equal(new LocalHandover { Host = "127.0.0.1", Port = server.Port, Key = "secret", Version = "1.2.3", Fingerprint = server.Fingerprint }, handover);
    }

    [Fact]
    public async Task Connect_ToALocalServerOpensTheOrdinaryConnectionWithTheKeyTheServerHandedOver()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        RunningServer server = await Start([Me]);
        Mock<ILocalServers> locals = new();
        locals.Setup(item => item.Open(server.Port, It.IsAny<CancellationToken>())).Returns((int port, CancellationToken cancellation) => new LocalServers(Endpoint()).Open(port, cancellation));
        IServerConnection.IFactory factory = new IServerConnection.Factory(new IMessageChannel.Factory(), Mock.Of<ILog>(), null, locals.Object);

        await using IServerConnection connection = await factory.Connect(new ConnectionSettings { Host = "127.0.0.1", Port = server.Port, AccessKey = string.Empty, Local = true }, CancellationToken.None);
        WorkspaceCatalog catalog = await connection.ListWorkspaces(CancellationToken.None);
        await connection.Reconnect(CancellationToken.None);
        await connection.ListWorkspaces(CancellationToken.None);

        Assert.Empty(catalog.Workspaces);
        Assert.Equal("1.2.3", connection.ServerVersion);
        locals.Verify(item => item.Open(server.Port, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Connect_RefusesAServerWhoseCertificateIsNotTheOneItAnnounced()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        RunningServer server = await Start([Me]);
        Mock<ILocalServers> locals = new();
        locals.Setup(item => item.Open(server.Port, It.IsAny<CancellationToken>())).ReturnsAsync(new LocalHandover { Host = "127.0.0.1", Port = server.Port, Key = "secret", Version = "1.2.3", Fingerprint = new string('0', 64) });
        IServerConnection.IFactory factory = new IServerConnection.Factory(new IMessageChannel.Factory(), Mock.Of<ILog>(), null, locals.Object);

        await Assert.ThrowsAsync<IOException>(() => factory.Connect(new ConnectionSettings { Host = "127.0.0.1", Port = server.Port, AccessKey = string.Empty, Local = true }, CancellationToken.None));
    }

    [Fact]
    public async Task Connect_ToALocalServerWithoutAWayToAskForTheKeyFails()
    {
        IServerConnection.IFactory factory = new IServerConnection.Factory(new IMessageChannel.Factory(), Mock.Of<ILog>());

        await Assert.ThrowsAsync<IOException>(() => factory.Connect(new ConnectionSettings { Host = "127.0.0.1", Port = 1, AccessKey = string.Empty, Local = true }, CancellationToken.None));
    }

    [Fact]
    public async Task Open_AnswersNothingToAnAccountThatIsNotListedAndFindLeavesTheServerOut()
    {
        if (!OperatingSystem.IsLinux() || Me == "root")
        {
            return;
        }

        RunningServer server = await Start(["root"]);
        LocalServers locals = new(Endpoint());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => locals.Open(server.Port, CancellationToken.None));
        Assert.Empty(await locals.Find(CancellationToken.None));
    }

    [Fact]
    public async Task Open_FindsNoServerWhenTheConfigListsNoAccountAndNoChannelIsOpened()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        RunningServer server = await Start([]);
        LocalServers locals = new(Endpoint());

        Assert.False(File.Exists(Socket(server)));
        Assert.Empty(await locals.Find(CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => locals.Open(server.Port, CancellationToken.None));
    }

    [Fact]
    public async Task Stopping_TheServerRemovesItsSocketAndAStaleOneIsReplacedOnTheNextStart()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Directory.CreateDirectory(temp.Combine("sockets"));
        RunningServer first = await Start([Me]);
        string path = Socket(first);
        Assert.True(File.Exists(path));

        first.Dispose();

        Assert.False(File.Exists(path));
        Assert.Empty(await new LocalServers(Endpoint()).Find(CancellationToken.None));

        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int free = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        string stale = Path.Combine(temp.Combine("sockets"), $"{free}.sock");
        await File.WriteAllTextAsync(stale, "stale");
        RunningServer second = await Start([Me], free);

        Assert.Equal(free, Assert.Single(await new LocalServers(Endpoint()).Find(CancellationToken.None)).Port);
        Assert.Equal(free, second.Port);
    }

    private sealed class RunningServer(CancellationTokenSource stopping, Task running, int port, string fingerprint) : IDisposable
    {
        public int Port { get; } = port;

        public string Fingerprint { get; } = fingerprint;

        public void Dispose()
        {
            stopping.Cancel();
            running.GetAwaiter().GetResult();
        }
    }
}
