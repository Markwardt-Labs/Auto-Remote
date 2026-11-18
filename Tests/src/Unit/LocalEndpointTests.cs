namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class LocalEndpointTests
{
    [Fact]
    public void Folders_OnLinuxStartWithTheRuntimeFolderOfTheUserAndEndWithTheOneOfTheSystem()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        LocalEndpoint endpoint = new(name => name == "XDG_RUNTIME_DIR" ? "/run/user/1000" : null);

        Assert.Equal(["/run/user/1000/auto-remote", "/run/auto-remote"], endpoint.Folders);
    }

    [Fact]
    public void Folders_WithoutARuntimeFolderOfTheUserHoldOnlyTheOneOfTheSystem()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(["/run/auto-remote"], new LocalEndpoint(_ => null).Folders);
        Assert.Equal(["/run/auto-remote"], new LocalEndpoint(_ => string.Empty).Folders);
    }

    [Fact]
    public void Names_CarryThePortAndReadBackToIt()
    {
        LocalEndpoint endpoint = new(_ => null);

        Assert.Equal("AutoRemote-52679", endpoint.PipeName(52679));
        Assert.Equal(Path.Combine("/x", "52679.sock"), endpoint.SocketPath("/x", 52679));
        Assert.Equal(52679, endpoint.Port("AutoRemote-52679"));
        Assert.Equal(52679, endpoint.Port("52679.sock"));
    }

    [Theory]
    [InlineData("AutoRemote-")]
    [InlineData("AutoRemote-x")]
    [InlineData("AutoRemote-0")]
    [InlineData("AutoRemote-70000")]
    [InlineData("AutoRemote--5")]
    [InlineData("other.sock")]
    [InlineData("52679")]
    [InlineData("")]
    public void Port_IgnoresNamesThatAreNotAServers(string name) => Assert.Null(new LocalEndpoint(_ => null).Port(name));
}
