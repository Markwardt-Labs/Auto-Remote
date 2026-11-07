namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class VersionMismatchViewModelTests
{
    private readonly Mock<IAppInfo> info = new();

    private readonly Mock<IServerConnection> connection = new();

    private readonly Mock<IReleaseFeed> feed = new();

    public VersionMismatchViewModelTests()
    {
        info.SetupGet(x => x.Version).Returns("1.0.0");
        connection.SetupGet(x => x.ServerVersion).Returns("1.2.0");
    }

    [Fact]
    public void Message_NamesBothVersionsOrTheMissingOne()
    {
        using UpdateViewModel update = new(info.Object, connection.Object, null, null);
        VersionMismatchViewModel model = new(info.Object, connection.Object, update);

        Assert.Contains("version 1.0.0", model.Message);
        Assert.Contains("version 1.2.0", model.Message);

        connection.SetupGet(x => x.ServerVersion).Returns((string?)null);

        Assert.Contains("before versions were checked", model.Message);
    }

    [Fact]
    public async Task Hint_OffersTheUpdateOtherwiseSaysWhyNot()
    {
        feed.Setup(x => x.Latest(It.IsAny<CancellationToken>())).ReturnsAsync((ReleaseInfo?)null);
        using UpdateViewModel update = new(info.Object, connection.Object, feed.Object, null);
        VersionMismatchViewModel model = new(info.Object, connection.Object, update);
        List<string?> raised = [];
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await update.Check();

        Assert.Contains("No newer release", model.Hint);

        feed.Setup(x => x.Latest(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("offline"));
        await update.Check();

        Assert.Contains("offline", model.Hint);

        feed.Setup(x => x.Latest(It.IsAny<CancellationToken>())).ReturnsAsync(new ReleaseInfo { Version = new AppVersion { Major = 1, Minor = 3, Patch = 0 }, Assets = [] });
        await update.Check();

        Assert.Contains("1.3.0", model.Hint);
        Assert.Contains(nameof(VersionMismatchViewModel.Hint), raised);
    }

    [Fact]
    public void ChangeServerCommand_RaisesTheRequest()
    {
        using UpdateViewModel update = new(info.Object, connection.Object, null, null);
        VersionMismatchViewModel model = new(info.Object, connection.Object, update);
        int requested = 0;
        model.ChangeServerRequested += () => requested++;

        model.ChangeServerCommand.Execute(null);

        Assert.Equal(1, requested);
    }
}
