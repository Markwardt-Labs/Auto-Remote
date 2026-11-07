namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ServerUpdaterTests
{
    private readonly Mock<IAppInfo> info = new();

    private readonly Mock<IReleaseFeed> feed = new();

    private readonly Mock<IExecutableInstaller> installer = new();

    private readonly Mock<IServerRestart> restart = new();

    private readonly ServerUpdater updater;

    public ServerUpdaterTests()
    {
        info.SetupGet(x => x.Version).Returns("1.2.3");
        installer.SetupGet(x => x.Executable).Returns("/opt/server");
        updater = new ServerUpdater(info.Object, feed.Object, installer.Object, restart.Object);
    }

    private static ReleaseInfo Release(string version) => new() { Version = AppVersion.TryParse(version, out AppVersion? parsed) ? parsed : throw new ArgumentException(version), Assets = [] };

    private string Asset() => OperatingSystem.IsWindows() ? "AutoRemote-Server-win.exe" : "AutoRemote-Server-linux";

    [Fact]
    public async Task Update_DownloadsTheNewerReleaseAndRequestsARestart()
    {
        ReleaseInfo newer = Release("1.3.0");
        feed.Setup(x => x.Latest(It.IsAny<CancellationToken>())).ReturnsAsync(newer);

        await updater.Update(CancellationToken.None);

        installer.Verify(x => x.Stage(newer, Asset(), null, It.IsAny<CancellationToken>()), Times.Once);
        restart.Verify(x => x.Request(), Times.Once);
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("1.2.2")]
    public async Task Update_RefusesWhenThereIsNothingNewer(string latest)
    {
        feed.Setup(x => x.Latest(It.IsAny<CancellationToken>())).ReturnsAsync(Release(latest));

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => updater.Update(CancellationToken.None));

        Assert.Contains("up to date", exception.Message);
        installer.Verify(x => x.Stage(It.IsAny<ReleaseInfo>(), It.IsAny<string>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.Never);
        restart.Verify(x => x.Request(), Times.Never);
    }

    [Fact]
    public async Task Update_RefusesWhenGitHubHasNoRelease()
    {
        feed.Setup(x => x.Latest(It.IsAny<CancellationToken>())).ReturnsAsync((ReleaseInfo?)null);

        await Assert.ThrowsAsync<RequestException>(() => updater.Update(CancellationToken.None));
    }

    [Fact]
    public async Task Update_RefusesWhenTheServerDoesNotRunFromAnExecutable()
    {
        installer.SetupGet(x => x.Executable).Returns((string?)null);

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => updater.Update(CancellationToken.None));

        Assert.Contains("installed release", exception.Message);
        feed.Verify(x => x.Latest(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_DoesNotRestartWhenTheDownloadFails()
    {
        feed.Setup(x => x.Latest(It.IsAny<CancellationToken>())).ReturnsAsync(Release("2.0.0"));
        installer.Setup(x => x.Stage(It.IsAny<ReleaseInfo>(), It.IsAny<string>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk full"));

        await Assert.ThrowsAsync<IOException>(() => updater.Update(CancellationToken.None));

        restart.Verify(x => x.Request(), Times.Never);
    }

    [Fact]
    public async Task Update_RefusesASecondUpdateWhileOneRunsAndAfterARestartWasRequested()
    {
        TaskCompletionSource gate = new();
        feed.Setup(x => x.Latest(It.IsAny<CancellationToken>())).ReturnsAsync(Release("2.0.0"));
        installer.Setup(x => x.Stage(It.IsAny<ReleaseInfo>(), It.IsAny<string>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>())).Returns(gate.Task);

        Task first = updater.Update(CancellationToken.None);
        RequestException running = await Assert.ThrowsAsync<RequestException>(() => updater.Update(CancellationToken.None));
        gate.SetResult();
        await first;
        restart.SetupGet(x => x.IsRequested).Returns(true);
        RequestException restarting = await Assert.ThrowsAsync<RequestException>(() => updater.Update(CancellationToken.None));

        Assert.Contains("already updating", running.Message);
        Assert.Contains("already restarting", restarting.Message);
    }
}
