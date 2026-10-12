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
}
