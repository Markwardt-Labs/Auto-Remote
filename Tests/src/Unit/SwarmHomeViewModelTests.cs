namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class SwarmHomeViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly Mock<IReadyNotifier> notifier = new();

    private readonly Mock<IWorkKeeper> keeper = new();

    private readonly WorkspaceInfo app = new() { Id = Guid.NewGuid(), Folder = "/work/app" };

    private readonly WorkspaceInfo remote = new() { Id = Guid.NewGuid(), Folder = "/data/task" };

    private List<SwarmTaskInfo> tasks = [];

    public SwarmHomeViewModelTests()
    {
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [app, remote], Delegations = [new WorkspaceDelegation { WorkspaceId = remote.Id, From = "laptop", Title = "t" }] });
        connection.Setup(x => x.GetSwarm(It.IsAny<CancellationToken>())).ReturnsAsync(() => new SwarmStatus { Server = new SwarmServer(), Tasks = tasks });
    }

    private SwarmTaskInfo Task(SwarmTaskState state, bool manual = false) => new() { Id = Guid.NewGuid(), Title = "fix tests", State = state, PeerName = "box", WorkspaceId = app.Id, IsManual = manual };

    private async Task<HomeViewModel> Started()
    {
        HomeViewModel home = new(connection.Object, ClientLayout.Tabs, null, null, null, notifier.Object, keeper.Object);
        await home.Start(app.Id);
        return home;
    }

    private void Changed() => connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.SwarmChanged, WorkspaceId = Guid.Empty });

    private void Working(bool working) => connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = app.Id, Working = working });

    [Fact]
    public async Task UnfinishedDelegatedTasksShowUnderTheirWorkspaceAndOpenOnTheSwarmScreen()
    {
        tasks = [Task(SwarmTaskState.Running), Task(SwarmTaskState.Done)];
        HomeViewModel home = await Started();
        List<Guid?> requested = [];
        home.SwarmRequested += requested.Add;

        DrawerEntry entry = Assert.Single(home.Entries, entry => entry.IsTask);
        Assert.Equal(("↳ fix tests", "Running - on box", 1), (entry.Text, entry.Detail, entry.Depth));
        Assert.True(home.Entries.IndexOf(entry) == home.Entries.IndexOf(home.Entries.First(row => row.Workspace?.Id == app.Id)) + 1);

        home.SelectedEntry = entry;

        Assert.Equal([tasks[0].Id], requested);
        Assert.Equal(app.Id, home.Selected?.Id);

        tasks = [tasks[0] with { State = SwarmTaskState.Cancelled }];
        Changed();

        Assert.DoesNotContain(home.Entries, row => row.IsTask);
        home.SwarmCommand.Execute(null);
        Assert.Equal([tasks[0].Id, null], requested);
    }

    [Fact]
    public async Task ADelegatedWorkspaceSaysWhoDelegatedItAndTakesNoInput()
    {
        HomeViewModel home = await Started();

        Assert.Contains(home.Entries, entry => entry.Detail == "Delegated by laptop - /data/task");

        home.Selected = remote;

        Assert.Equal("This workspace runs a task delegated by laptop", home.Active!.Chat.BlockReason);
        Assert.False(home.Active.Chat.DictateCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheReadyNotificationWaitsUntilTheAiHasNoDelegatedTasksOutstanding()
    {
        HomeViewModel home = await Started();
        home.Active!.Chat.Input = "split it up";
        home.Active.Chat.SendCommand.Execute(null);
        Working(true);
        tasks = [Task(SwarmTaskState.Running)];
        Changed();

        Working(false);

        notifier.Verify(x => x.Notify(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        keeper.Verify(x => x.Release(It.IsAny<Guid>()), Times.Never);
        tasks = [tasks[0] with { State = SwarmTaskState.Done }];
        Changed();
        Working(true);
        Working(false);

        notifier.Verify(x => x.Notify(app.Id, "app"), Times.Once);
        keeper.Verify(x => x.Release(app.Id), Times.Once);
    }

    [Fact]
    public async Task TheNotificationCanBeTurnedOnWhileOnlyDelegatedTasksAreOutstanding()
    {
        tasks = [Task(SwarmTaskState.Running)];
        HomeViewModel home = await Started();
        Assert.True(home.CanNotify);

        home.ToggleNotifyCommand.Execute(null);

        keeper.Verify(x => x.Hold(app.Id, "app"), Times.Once);
        tasks = [tasks[0] with { State = SwarmTaskState.Done }];
        Changed();
        Working(true);
        Working(false);

        notifier.Verify(x => x.Notify(app.Id, "app"), Times.Once);
        keeper.Verify(x => x.Release(app.Id), Times.Once);
        Assert.False(home.CanNotify);
    }

    [Fact]
    public async Task TasksThatOnlyWaitOnALostOneDoNotHoldTheReadyNotificationBack()
    {
        Guid job = Guid.NewGuid();
        tasks = [Task(SwarmTaskState.Lost) with { JobId = job }, Task(SwarmTaskState.Waiting) with { JobId = job }];
        HomeViewModel home = await Started();
        home.Active!.Chat.Input = "split it up";
        home.Active.Chat.SendCommand.Execute(null);
        Working(true);

        Working(false);

        notifier.Verify(x => x.Notify(app.Id, "app"), Times.Once);
        keeper.Verify(x => x.Release(app.Id), Times.Once);
    }

    [Fact]
    public async Task DiscardingEveryTaskTheAiDelegatedEndsTheWaitWithoutANotification()
    {
        HomeViewModel home = await Started();
        home.Active!.Chat.Input = "split it up";
        home.Active.Chat.SendCommand.Execute(null);
        Working(true);
        tasks = [Task(SwarmTaskState.Running)];
        Changed();
        Working(false);

        tasks = [];
        Changed();

        Assert.False(home.Active.NotifyWhenReady);
        keeper.Verify(x => x.Release(app.Id), Times.Once);
        notifier.Verify(x => x.Notify(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TasksThatAreBroughtInAndRemovedWhileThatWorkRunsLeaveTheWaitToIt()
    {
        HomeViewModel home = await Started();
        home.Active!.Chat.Input = "split it up";
        home.Active.Chat.SendCommand.Execute(null);
        Working(true);
        tasks = [Task(SwarmTaskState.Running)];
        Changed();
        Working(false);

        // The results come back, and the task is gone as soon as the AI confirms it, in the middle of that turn.
        tasks = [tasks[0] with { State = SwarmTaskState.Done }];
        Changed();
        Working(true);
        tasks = [];
        Changed();

        Assert.True(home.Active.NotifyWhenReady);
        keeper.Verify(x => x.Release(It.IsAny<Guid>()), Times.Never);
        Working(false);

        notifier.Verify(x => x.Notify(app.Id, "app"), Times.Once);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ATaskDelegatedByHandHoldsTheReadyNotificationOnlyWhenItIsToBeBroughtIn(bool bringIn, bool held)
    {
        tasks = [Task(SwarmTaskState.Running, true) with { BringIn = bringIn }];
        HomeViewModel home = await Started();
        home.ToggleNotifyCommand.Execute(null);
        Working(true);

        Working(false);

        notifier.Verify(x => x.Notify(app.Id, "app"), held ? Times.Never() : Times.Once());

        if (held)
        {
            // One that fails brings nothing in, which ends the wait.
            tasks = [tasks[0] with { State = SwarmTaskState.Failed }];
            Changed();

            notifier.Verify(x => x.Notify(app.Id, "app"), Times.Once);
        }
    }

    [Fact]
    public async Task ADelegatedWorkspaceClosesWhatTheServerWouldRefuseButCanStillBeDeletedAndSaysWhatStopDoes()
    {
        HomeViewModel home = await Started();

        Assert.True(home.Active!.Chat.TakesTyping);
        Assert.True(home.Active.OpenSendOptionsCommand.CanExecute(null));
        Assert.Equal(("Stop the work and keep what it did", false), (home.Active.StopTip, home.HasStopNote));

        home.Selected = remote;

        Assert.False(home.Active.Chat.TakesTyping);
        Assert.False(home.Active.Chat.ToggleSavedInputsCommand.CanExecute(null));
        Assert.False(home.Active.OpenSendOptionsCommand.CanExecute(null));
        Assert.False(home.Active.Settings.CanEdit);
        home.Sessions.NewCommand.Execute(null);
        Assert.False(home.Active.SendOptions.IsOpen);
        Assert.Equal("This workspace runs a task delegated by laptop, so no session can be made of it.", home.Active.Chat.Error);
        Assert.False(home.BeginRenameCommand.CanExecute(null));
        Assert.False(home.MoveWorkspaceToFolderCommand.CanExecute(null));
        Assert.True(home.BeginDeleteCommand.CanExecute(null));
        Assert.Equal(("Cancel the delegated task", "This cancels the task delegated by laptop, which is told."), (home.Active.StopTip, home.StopNote));
        Assert.True(await home.Active.Settings.Apply());
        connection.Verify(x => x.SetWorkspaceSettings(It.IsAny<Guid>(), It.IsAny<WorkspaceSettings>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ATaskDelegatedByHandFromThisClientIsAnnouncedOnceWhenItEnds()
    {
        SwarmTaskInfo manual = Task(SwarmTaskState.InTransit, true);
        tasks = [manual];
        HomeViewModel home = await Started();
        home.Follow([manual.Id]);

        tasks = [manual with { State = SwarmTaskState.Done }];
        Changed();
        Changed();

        notifier.Verify(x => x.Announce(app.Id, "Delegated task \"fix tests\": Done"), Times.Once);
    }
}
