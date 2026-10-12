namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ApplicationTests
{
    private readonly Mock<IAppInfo> info = new();

    private readonly Mock<IConfigLoader> loader = new();

    private readonly Mock<IRequestHandler> handler = new();

    private readonly Mock<IConnectionServer> server = new();

    private readonly StringWriter output = new();

    public ApplicationTests()
    {
        info.SetupGet(x => x.Name).Returns("Auto Remote");
        info.SetupGet(x => x.Version).Returns("1.2.3");
    }

    [Fact]
    public async Task Run_LoadsDefaultConfigAndRunsServer()
    {
        ServerConfig config = new() { Port = 5000, WebPort = 5001, Key = "key" };
        loader.Setup(x => x.Load("config.json", It.IsAny<CancellationToken>())).ReturnsAsync(config);
        Application application = new(info.Object, loader.Object, handler.Object, server.Object, output);

        int exitCode = await application.Run([], CancellationToken.None);

        Assert.Equal(0, exitCode);
        handler.Verify(x => x.Open(config, It.IsAny<CancellationToken>()), Times.Once);
        server.Verify(x => x.Run(config, It.IsAny<CancellationToken>()), Times.Once);
        Assert.StartsWith("Auto Remote server 1.2.3", output.ToString());
    }

    [Fact]
    public async Task Run_UsesConfigPathArgument()
    {
        loader.Setup(x => x.Load("custom.json", It.IsAny<CancellationToken>())).ReturnsAsync(new ServerConfig { Port = 1, WebPort = 2, Key = "k" });
        Application application = new(info.Object, loader.Object, handler.Object, server.Object, output);

        Assert.Equal(0, await application.Run(["custom.json"], CancellationToken.None));
    }

    [Fact]
    public async Task Run_ReportsInvalidConfigAndReturnsOne()
    {
        loader.Setup(x => x.Load(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidDataException("bad config"));
        Application application = new(info.Object, loader.Object, handler.Object, server.Object, output);

        int exitCode = await application.Run([], CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("bad config", output.ToString());
        server.Verify(x => x.Run(It.IsAny<ServerConfig>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
