namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class ServerRunTests : IAsyncLifetime
{
    private readonly TempFolder temp = new();

    private readonly CancellationTokenSource stopping = new();

    private Task running = Task.CompletedTask;

    private int port;

    private readonly List<string> logged = [];

    private string fingerprint = string.Empty;

    public async Task InitializeAsync()
    {
        Mock<IRequestHandler> handler = new();
        handler.Setup(item => item.Handle(It.IsAny<RemoteRequest>(), It.IsAny<object>(), It.IsAny<CancellationToken>())).ReturnsAsync((RemoteRequest request, object _, CancellationToken _) => new RemoteResponse { Id = request.Id, Catalog = new WorkspaceCatalog { Groups = [], Workspaces = [] } });
        Mock<ILog> log = new();
        log.Setup(item => item.Write(It.IsAny<string>())).Callback<string>(message =>
        {
            lock (logged)
            {
                logged.Add(message);
            }
        });
        ConnectionServer server = new(new Handshake(), new IMessageChannel.Factory(), new WebSocketUpgrade(), new CertificateProvider(), handler.Object, new EventHub(), log.Object);
        TaskCompletionSource<int> listening = new();
        server.Listening += value => listening.SetResult(value);
        server.Certified += value => fingerprint = value;
        running = server.Run(new ServerConfig { Port = 0, Key = "secret", DataFolder = temp.Combine("data") }, stopping.Token);
        port = await listening.Task;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await stopping.CancelAsync();
        await running;
        temp.Dispose();
    }

    private async Task<string> Connect(string key)
    {
        using ClientWebSocket socket = await OpenSocket();
        return await Exchange(socket, $"{{\"accessKey\":\"{key}\"}}");
    }

    private async Task<string[]> WaitForLog(Func<string[], bool> ready)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);

        while (true)
        {
            string[] lines;

            lock (logged)
            {
                lines = [.. logged];
            }

            if (ready(lines) || DateTime.UtcNow >= deadline)
            {
                return lines;
            }

            await Task.Delay(20);
        }
    }

    private async Task<ClientWebSocket> OpenSocket()
    {
        ClientWebSocket socket = new();
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{port}"), CancellationToken.None);
        return socket;
    }

    private async Task<string> Exchange(ClientWebSocket socket, string message)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
        byte[] buffer = new byte[4096];
        WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, result.Count).Trim();
    }

    [Fact]
    public async Task AdmitsOnlyClientsWithAccessKey()
    {
        Assert.Equal("{\"accepted\":true}", await Connect("secret"));
        Assert.Equal("{\"accepted\":false}", await Connect("nope"));
    }

    [Fact]
    public async Task LogsEachConnectionWithItsOutcome()
    {
        await Connect("secret");
        await Connect("nope");

        string[] lines = await WaitForLog(items => items.Any(line => line.StartsWith("Accepted 127.0.0.1:")) && items.Any(line => line.StartsWith("Refused 127.0.0.1:")));

        Assert.Equal(2, lines.Count(line => line.StartsWith("Connection from 127.0.0.1:")));
        Assert.Equal(2, lines.Count(line => line.StartsWith("TLS established with 127.0.0.1:")));
        Assert.Single(lines, line => line.StartsWith("Accepted 127.0.0.1:"));
        Assert.Single(lines, line => line.StartsWith("Refused 127.0.0.1:") && line.EndsWith("wrong key"));
    }

    [Fact]
    public async Task DropsClientsThatDoNotUseTls()
    {
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"accessKey\":\"secret\"}\n"));

        byte[] buffer = new byte[256];
        int read;

        try
        {
            read = await stream.ReadAsync(buffer);
        }
        catch (IOException)
        {
            read = 0;
        }

        Assert.DoesNotContain("accepted", Encoding.UTF8.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task SpeaksTheProtocolOverWebSocket()
    {
        using ClientWebSocket socket = await OpenSocket();
        Assert.Equal("{\"accepted\":true}", await Exchange(socket, "{\"accessKey\":\"secret\"}"));
        Assert.Equal("{\"id\":1,\"catalog\":{\"groups\":[],\"workspaces\":[]}}", await Exchange(socket, "{\"id\":1,\"command\":\"ListWorkspaces\"}"));

        using ClientWebSocket denied = await OpenSocket();
        Assert.Equal("{\"accepted\":false}", await Exchange(denied, "{\"accessKey\":\"nope\"}"));
    }

    [Fact]
    public async Task AnswersPlainHttpRequestsSoBrowsersCanTrustTheCertificate()
    {
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using SslStream stream = new(client.GetStream(), false, (_, _, _, _) => true);
        await stream.AuthenticateAsClientAsync("localhost");
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n\r\n"));
        using StreamReader reader = new(stream);

        Assert.StartsWith("HTTP/1.1 200", await reader.ReadLineAsync());
    }

    [Fact]
    public async Task Client_LogsTheStagesOfSucceedingAndFailingConnections()
    {
        List<string> lines = [];
        Mock<ILog> log = new();
        log.Setup(item => item.Write(It.IsAny<string>())).Callback<string>(lines.Add);
        IServerConnection.IFactory factory = new IServerConnection.Factory(new IMessageChannel.Factory(), log.Object);
        ConnectionSettings settings = new() { Host = "127.0.0.1", Port = port, AccessKey = "secret" };

        await using (await factory.Connect(settings, CancellationToken.None))
        {
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => factory.Connect(settings with { AccessKey = "nope" }, CancellationToken.None));

        Assert.Contains($"Connecting to wss://127.0.0.1:{port}/", lines);
        Assert.Contains(lines, line => line.StartsWith($"WebSocket and TLS established with 127.0.0.1:{port}") && line.Contains(fingerprint));
        Assert.Contains($"Connected to 127.0.0.1:{port}", lines);
        Assert.Contains($"Connection to 127.0.0.1:{port} failed during the key handshake: UnauthorizedAccessException: The server rejected the access key.", lines);
        Assert.DoesNotContain(lines, line => line.Contains("secret") || line.Contains("nope"));
    }

    [Fact]
    public async Task Client_AcceptsTheServersSelfSignedCertificate()
    {
        IServerConnection.IFactory factory = new IServerConnection.Factory(new IMessageChannel.Factory(), Mock.Of<ILog>());
        ConnectionSettings settings = new() { Host = "127.0.0.1", Port = port, AccessKey = "secret" };

        await using (await factory.Connect(settings, CancellationToken.None))
        {
        }

        await using (await factory.Connect(settings, CancellationToken.None))
        {
        }
    }
}
