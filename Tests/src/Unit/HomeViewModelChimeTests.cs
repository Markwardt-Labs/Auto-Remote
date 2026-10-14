namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class HomeViewModelChimeTests
{
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan span) => now += span;
    }

    private readonly Mock<IServerConnection> connection = new();

    private readonly Mock<IChime> chime = new();

    private readonly ManualTime time = new();

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/a" };

    private readonly HomeViewModel model;

    public HomeViewModelChimeTests()
    {
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0 });
        model = new HomeViewModel(connection.Object, ClientLayout.Tabs, null, chime.Object, time);
    }

    private void Work(bool working) => connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspace.Id, Working = working });

    [Fact]
    public async Task Chime_PlaysWhenAWorkspaceBecomesReadyAgain()
    {
        await model.Start();

        Work(true);
        time.Advance(TimeSpan.FromSeconds(5));
        chime.Verify(x => x.Play(), Times.Never);
        Work(false);

        chime.Verify(x => x.Play(), Times.Once);
    }

    [Fact]
    public async Task Chime_StaysSilentForAMomentaryBusyState()
    {
        await model.Start();

        Work(true);
        time.Advance(TimeSpan.FromMilliseconds(300));
        Work(false);

        chime.Verify(x => x.Play(), Times.Never);
    }

    [Fact]
    public async Task Chime_StaysSilentWhenNothingWasBusy()
    {
        await model.Start();

        Work(false);

        chime.Verify(x => x.Play(), Times.Never);
    }
}
