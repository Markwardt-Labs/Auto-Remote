namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class UpdateViewModelTests
{
    private readonly Mock<IAppInfo> info = new();

    private readonly Mock<IServerConnection> connection = new();

    private readonly Mock<IReleaseFeed> feed = new();

    private readonly Mock<IClientUpdater> updater = new();

    private string? serverVersion = "1.0.0";

    public UpdateViewModelTests()
    {
        info.SetupGet(x => x.Version).Returns("1.0.0");
        connection.SetupGet(x => x.ServerVersion).Returns(() => serverVersion);
        updater.SetupGet(x => x.IsSupported).Returns(true);
        Latest("1.1.0");
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }
    }

    private void Latest(string? version) => feed.Setup(x => x.Latest(It.IsAny<CancellationToken>())).ReturnsAsync(version is null ? null : new ReleaseInfo { Version = AppVersion.TryParse(version, out AppVersion? parsed) ? parsed : throw new ArgumentException(version), Assets = [] });

    private UpdateViewModel Create(IReleaseFeed? source = null, IClientUpdater? installer = null) => new(info.Object, connection.Object, source ?? feed.Object, installer ?? updater.Object, TimeSpan.FromMilliseconds(30));

    [Fact]
    public async Task Check_OffersAnUpdateWhenTheClientAndTheServerAreOlder()
    {
        using UpdateViewModel update = Create();

        await update.Check();

        Assert.True(update.ClientNeedsUpdate);
        Assert.True(update.ServerNeedsUpdate);
        Assert.True(update.IsAvailable);
        Assert.Contains("1.1.0 replaces 1.0.0 on this device and 1.0.0 on the server", update.Summary);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.9.0")]
    public async Task Check_OffersNothingWhenNothingIsOlderThanTheLatestRelease(string latest)
    {
        Latest(latest);
        using UpdateViewModel update = Create();

        await update.Check();

        Assert.False(update.IsAvailable);
    }

    [Fact]
    public async Task Check_OffersOnlyTheServerWhenTheClientIsCurrentOrCannotUpdateItself()
    {
        info.SetupGet(x => x.Version).Returns("1.1.0");
        using UpdateViewModel current = Create();
        await current.Check();

        Assert.False(current.ClientNeedsUpdate);
        Assert.True(current.ServerNeedsUpdate);
        Assert.Contains("on the server", current.Summary);
        Assert.DoesNotContain("this device", current.Summary);

        info.SetupGet(x => x.Version).Returns("1.0.0");
        updater.SetupGet(x => x.IsSupported).Returns(false);
        using UpdateViewModel unsupported = Create();
        await unsupported.Check();

        Assert.False(unsupported.ClientNeedsUpdate);
        Assert.True(unsupported.IsAvailable);
    }

    [Fact]
    public async Task Check_OffersOnlyTheClientWhenTheServerIsCurrent()
    {
        serverVersion = "1.1.0";
        using UpdateViewModel update = Create();

        await update.Check();

        Assert.True(update.ClientNeedsUpdate);
        Assert.False(update.ServerNeedsUpdate);
        Assert.Contains("on this device", update.Summary);
    }

    [Fact]
    public async Task Check_TreatsAServerThatSaysNoVersionAsOlder()
    {
        serverVersion = null;
        info.SetupGet(x => x.Version).Returns("1.1.0");
        using UpdateViewModel update = Create();

        await update.Check();

        Assert.True(update.ServerNeedsUpdate);
    }

    [Fact]
    public async Task Check_KeepsQuietAboutAMissingReleaseAndReportsAFailedLookup()
    {
        Latest(null);
        using UpdateViewModel none = Create();
        await none.Check();

        Assert.False(none.IsAvailable);
        Assert.Null(none.CheckError);
        Assert.Equal("This client and its server are up to date.", none.Summary);

        feed.Setup(x => x.Latest(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("offline"));
        using UpdateViewModel failing = Create();
        await failing.Check();

        Assert.False(failing.IsAvailable);
        Assert.Equal("offline", failing.CheckError);
        Assert.True(failing.HasCheckError);
    }

    [Fact]
    public async Task Check_DoesNothingWithoutAFeed()
    {
        using UpdateViewModel update = new(info.Object, connection.Object, null, updater.Object);

        await update.Check();

        Assert.False(update.IsAvailable);
    }

    [Fact]
    public async Task Begin_ShowsTheConfirmationOnlyWhenAnUpdateIsAvailable()
    {
        using UpdateViewModel update = Create();
        update.Begin();

        Assert.False(update.IsOpen);

        await update.Check();
        update.BeginCommand.Execute(null);

        Assert.True(update.IsConfirming);
        Assert.True(update.IsOpen);
        Assert.Equal("Cancel", update.CloseText);

        update.CloseCommand.Execute(null);

        Assert.False(update.IsOpen);
        updater.Verify(x => x.Install(It.IsAny<ReleaseInfo>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Never);
        connection.Verify(x => x.UpdateServer(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Confirm_UpdatesTheServerFirstAndThenTheClient()
    {
        List<string> order = [];
        connection.Setup(x => x.UpdateServer(It.IsAny<CancellationToken>())).Returns(() =>
        {
            order.Add("server");
            return Task.CompletedTask;
        });
        updater.Setup(x => x.Install(It.IsAny<ReleaseInfo>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            order.Add("client");
            return Task.CompletedTask;
        });
        using UpdateViewModel update = Create();
        await update.Check();
        update.Begin();

        update.ConfirmCommand.Execute(null);
        await WaitFor(() => update.Phase == UpdatePhase.Finished);

        Assert.Equal(["server", "client"], order);
        Assert.Equal(UpdatePhase.Finished, update.Phase);
        Assert.True(update.CanClose);
        Assert.Equal("Close", update.CloseText);
        updater.Verify(x => x.Install(It.Is<ReleaseInfo>(release => release.Version.ToString() == "1.1.0"), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Confirm_ForTheServerOnlyWaitsForItToComeBackAndThenCloses()
    {
        info.SetupGet(x => x.Version).Returns("1.1.0");
        using UpdateViewModel update = Create();
        await update.Check();
        update.Begin();

        update.ConfirmCommand.Execute(null);
        await WaitFor(() => update.Phase == UpdatePhase.Waiting);

        Assert.True(update.IsWorking);
        Assert.True(update.IsUpdating);
        Assert.False(update.IsInstalling);
        Assert.True(update.CanClose);
        updater.Verify(x => x.Install(It.IsAny<ReleaseInfo>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Never);

        int reconnected = 0;
        update.ServerReconnected += () => reconnected++;
        serverVersion = "1.1.0";
        connection.Raise(x => x.Reconnected += null);

        Assert.Equal(UpdatePhase.Idle, update.Phase);
        Assert.Equal(1, reconnected);
        await WaitFor(() => !update.IsAvailable);
        Assert.False(update.IsAvailable);
    }

    [Fact]
    public async Task Confirm_ShowsTheRefusalOfTheServerAndInstallsNothing()
    {
        connection.Setup(x => x.UpdateServer(It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("The server is up to date."));
        using UpdateViewModel update = Create();
        await update.Check();
        update.Begin();

        update.ConfirmCommand.Execute(null);
        await WaitFor(() => update.HasError);

        Assert.Equal("The server is up to date.", update.Error);
        Assert.Equal(UpdatePhase.Confirming, update.Phase);
        updater.Verify(x => x.Install(It.IsAny<ReleaseInfo>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(RemoteException))]
    public async Task Confirm_ExplainsThatAServerWithoutAVersionIsTooOldToUpdateItself(Type failure)
    {
        serverVersion = null;
        info.SetupGet(x => x.Version).Returns("1.1.0");
        connection.Setup(x => x.UpdateServer(It.IsAny<CancellationToken>())).ThrowsAsync((Exception)Activator.CreateInstance(failure, "closed")!);
        using UpdateViewModel update = Create();
        await update.Check();
        update.Begin();

        update.ConfirmCommand.Execute(null);
        await WaitFor(() => update.HasError);

        Assert.Contains("too old to update itself (closed)", update.Error);
        Assert.Equal(UpdatePhase.Confirming, update.Phase);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task Confirm_ShowsAFailedInstallationAndAllowsTryingAgain(Type failure)
    {
        serverVersion = "1.1.0";
        updater.Setup(x => x.Install(It.IsAny<ReleaseInfo>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>())).ThrowsAsync((Exception)Activator.CreateInstance(failure, "broken")!);
        using UpdateViewModel update = Create();
        await update.Check();
        update.Begin();

        update.ConfirmCommand.Execute(null);
        await WaitFor(() => update.HasError);

        Assert.Equal("broken", update.Error);
        Assert.True(update.IsConfirming);
        Assert.True(update.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task Confirm_ReportsTheDownloadProgress()
    {
        serverVersion = "1.1.0";
        TaskCompletionSource gate = new();
        updater.Setup(x => x.Install(It.IsAny<ReleaseInfo>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>())).Returns(async (ReleaseInfo _, IProgress<double> progress, CancellationToken _) =>
        {
            progress.Report(0.5);
            await gate.Task;
        });
        using UpdateViewModel update = Create();
        await update.Check();
        update.Begin();

        update.ConfirmCommand.Execute(null);
        await WaitFor(() => update.Progress > 0);

        Assert.Equal(0.5, update.Progress);
        Assert.False(update.IsProgressUnknown);
        Assert.True(update.IsInstalling);
        Assert.False(update.CanClose);
        Assert.Contains("1.1.0", update.Status);

        update.CloseCommand.Execute(null);
        Assert.True(update.IsUpdating);
        Assert.False(update.IsOpen);

        gate.SetResult();
        await WaitFor(() => update.Phase == UpdatePhase.Finished);
    }

    [Fact]
    public async Task Watch_LooksAgainAtIntervalsUntilDisposed()
    {
        int lookups = 0;
        feed.Setup(x => x.Latest(It.IsAny<CancellationToken>())).Returns(() =>
        {
            lookups++;
            return Task.FromResult<ReleaseInfo?>(null);
        });
        UpdateViewModel update = Create();

        Task watching = update.Watch();
        await WaitFor(() => lookups >= 3);
        update.Dispose();
        await watching;
        int after = lookups;
        await Task.Delay(100);

        Assert.True(after >= 3);
        Assert.Equal(after, lookups);
    }

    [Fact]
    public async Task Check_AfterDisposeDoesNothingAndDoesNotThrow()
    {
        UpdateViewModel update = Create();
        update.Dispose();

        await update.Check();
        await update.Watch();

        Assert.False(update.IsAvailable);
    }

    [Theory]
    [InlineData("1.1.0", "1.0.0", "Version 1.1.0 is available.")]
    [InlineData("1.0.0", "1.0.0", "Up to date. The newest release is 1.0.0.")]
    [InlineData(null, "1.0.0", "No release was found on GitHub.")]
    public async Task CheckCommand_SaysWhatTheManualCheckFound(string? latest, string server, string expected)
    {
        serverVersion = server;
        Latest(latest);
        using UpdateViewModel update = Create();
        Assert.False(update.HasCheckResult);

        update.CheckCommand.Execute(null);
        await WaitFor(() => update.CheckResult != "Checking..." && update.HasCheckResult);

        Assert.Equal(expected, update.CheckResult);
        Assert.Equal("This app 1.0.0, server " + server + ".", update.Versions);
    }

    [Fact]
    public async Task CheckCommand_SaysWhyTheLookupFailedAndIsDisabledWhileItRuns()
    {
        TaskCompletionSource gate = new();
        feed.Setup(x => x.Latest(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            await gate.Task;
            throw new IOException("offline");
        });
        using UpdateViewModel update = Create();

        update.CheckCommand.Execute(null);
        await WaitFor(() => update.CheckResult == "Checking...");
        bool whileRunning = update.CheckCommand.CanExecute(null);
        gate.SetResult();
        await WaitFor(() => update.CheckResult != "Checking...");

        Assert.False(whileRunning);
        Assert.Equal("The newest release could not be looked up: offline", update.CheckResult);
        Assert.True(update.CheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task Reconnecting_ReadsTheServerVersionAgain()
    {
        using UpdateViewModel update = Create();
        await update.Check();
        List<string?> raised = [];
        update.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        serverVersion = "1.1.0";
        connection.Raise(x => x.Reconnected += null);

        Assert.Contains(nameof(UpdateViewModel.ServerNeedsUpdate), raised);
        Assert.False(update.ServerNeedsUpdate);
    }
}
