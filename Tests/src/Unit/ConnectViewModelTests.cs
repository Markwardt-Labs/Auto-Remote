namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ConnectViewModelTests
{
    private readonly Mock<IServerConnection.IFactory> factory = new();

    [Fact]
    public async Task Connect_OpensConnectionAndRaisesConnected()
    {
        Mock<IServerConnection> connection = new();
        factory.Setup(x => x.Connect(new ConnectionSettings { Host = "box", Port = 4000, AccessKey = "k" }, It.IsAny<CancellationToken>())).ReturnsAsync(connection.Object);
        ConnectViewModel model = new(factory.Object, null, "v") { Host = " box ", Port = "4000", AccessKey = "k" };
        IServerConnection? opened = null;
        model.Connected += (value, _) => opened = value;

        model.ConnectCommand.Execute(null);
        await Task.Delay(100);

        Assert.Same(connection.Object, opened);
        Assert.Null(model.Error);
    }

    [Theory]
    [InlineData("", "10", "k")]
    [InlineData("box", "abc", "k")]
    [InlineData("box", "70000", "k")]
    [InlineData("box", "10", "")]
    public async Task Connect_RejectsInvalidInputWithoutConnecting(string host, string port, string key)
    {
        ConnectViewModel model = new(factory.Object, null, "v") { Host = host, Port = port, AccessKey = key };

        model.ConnectCommand.Execute(null);
        await Task.Delay(50);

        Assert.True(model.HasError);
        factory.Verify(x => x.Connect(It.IsAny<ConnectionSettings>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Connect_ShowsRejectedKey()
    {
        factory.Setup(x => x.Connect(It.IsAny<ConnectionSettings>(), It.IsAny<CancellationToken>())).ThrowsAsync(new UnauthorizedAccessException("rejected"));
        ConnectViewModel model = new(factory.Object, null, "v") { Host = "box", Port = "10", AccessKey = "bad" };

        model.ConnectCommand.Execute(null);
        await Task.Delay(100);

        Assert.Equal("rejected", model.Error);
    }

    [Fact]
    public void Constructor_PrefillsSavedSettings()
    {
        ConnectViewModel model = new(factory.Object, new ConnectionSettings { Host = "h", Port = 9, AccessKey = "k" }, "v");

        Assert.Equal(("h", "9", "k"), (model.Host, model.Port, model.AccessKey));
    }

    [Fact]
    public void Constructor_DefaultsThePortWithoutSavedSettings() => Assert.Equal("52678", new ConnectViewModel(factory.Object, null, "v").Port);
}
