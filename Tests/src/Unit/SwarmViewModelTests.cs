namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class SwarmViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly List<string> calls = [];

    private readonly WorkspaceInfo app = new() { Id = Guid.NewGuid(), Folder = "/work/app" };

    private readonly WorkspaceInfo delegated = new() { Id = Guid.NewGuid(), Folder = "/data/swarm/work/task-1" };

    private SwarmStatus status;

    public SwarmViewModelTests()
    {
        status = new SwarmStatus
        {
            Server = new SwarmServer { Name = "here", Fingerprint = "AAAA-BBBB" },
            Peers = [new SwarmPeer { Id = "peer", Name = "there", Fingerprint = "CCCC-DDDD", Mode = SwarmMode.Direct }],
            Tasks = [Task(SwarmTaskState.Done) with { Branch = "swarm/x" }]
        };
        connection.Setup(x => x.GetSwarm(It.IsAny<CancellationToken>())).ReturnsAsync(() => status);
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [app, delegated], Delegations = [new WorkspaceDelegation { WorkspaceId = delegated.Id, From = "there", Title = "t" }] });
        connection.Setup(x => x.SetSwarm(It.IsAny<SwarmServer>(), It.IsAny<CancellationToken>())).ReturnsAsync((SwarmServer server, CancellationToken _) => Record($"server {server.Name} {server.AcceptDelegatedWork} {server.DriveFolder} {server.MaxTasksPerJob}", status = status with { Server = server }));
        connection.Setup(x => x.SetSwarmPeer(It.IsAny<SwarmPeer>(), It.IsAny<CancellationToken>())).ReturnsAsync((SwarmPeer peer, CancellationToken _) => Record($"peer {peer.SendWork} {peer.AcceptWork} {peer.Mode} {peer.Address} {peer.Permissions.IsShellAllowed}", status = status with { Peers = [peer] }));
        connection.Setup(x => x.SyncDrive(It.IsAny<CancellationToken>())).ReturnsAsync(() => Record("sync", status with { Report = "Read 1 package." }));
        connection.Setup(x => x.RevokeSwarmPeer(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string id, CancellationToken _) => Record($"revoke {id}", status = status with { Peers = [] }));
        connection.Setup(x => x.GetSwarmTask(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid id, CancellationToken _) => new SwarmTaskDetail { Task = status.Tasks.First(task => task.Id == id), Instructions = "do it" });
    }

    private SwarmTaskInfo Task(SwarmTaskState state, string title = "fix") => new() { Id = Guid.NewGuid(), Title = title, State = state, PeerName = "there" };

    private SwarmStatus Record(string call, SwarmStatus result)
    {
        calls.Add(call);
        return result;
    }

    private async Task<SwarmViewModel> Loaded()
    {
        SwarmViewModel model = new(connection.Object, null, app.Id);
        await model.Load();
        return model;
    }

    [Fact]
    public async Task ARefusedSwitchOfAcceptingWorkShowsWhatTheServerStillHas()
    {
        SwarmViewModel model = await Loaded();
        connection.Setup(x => x.SetSwarm(It.IsAny<SwarmServer>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("No."));

        model.AcceptDelegatedWork = true;

        Assert.False(model.AcceptDelegatedWork);
        Assert.Equal("No.", model.Error);
    }

    [Fact]
    public async Task TheSwitchOfAcceptingWorkKeepsWhatWasChosenWhenAnEditOfAPeerIsSentFirst()
    {
        SwarmViewModel model = await Loaded();
        model.Peers[0].Address = "box:1";

        model.AcceptDelegatedWork = true;

        Assert.Equal(["peer False False Direct box:1 False", "server here True  10"], calls);
        Assert.True(model.AcceptDelegatedWork);
    }

    [Fact]
    public async Task ARefreshKeepsASpaceBeingTypedAndAReconnectReadsAgain()
    {
        SwarmViewModel model = await Loaded();
        model.ServerName = "here ";
        model.Peers[0].Address = " ";

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.SwarmChanged, WorkspaceId = Guid.Empty });

        Assert.Equal(("here ", " "), (model.ServerName, model.Peers[0].Address));

        status = status with { Tasks = [] };
        connection.Raise(x => x.Reconnected += null);

        Assert.Empty(model.Tasks);
    }

    [Fact]
    public async Task Load_ShowsTheServerThePeersAndTasksAndOffersOnlyWorkspacesThatAreNotDelegated()
    {
        SwarmViewModel model = await Loaded();

        Assert.Equal(("here", "Fingerprint AAAA-BBBB", false), (model.ServerName, model.Fingerprint, model.HasNoPeers));
        Assert.Equal("there", Assert.Single(model.Peers).Name);
        Assert.Equal("→ fix", Assert.Single(model.Tasks).Title);
        Assert.Equal([app], model.Workspaces);
        Assert.Same(app, model.SelectedWorkspace);
        Assert.Equal(["Any networked worker"], model.Workers.Select(worker => worker.Name));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Switches_AreSentAtOnceAndSendingWorkOffersThePeerAsAWorker()
    {
        SwarmViewModel model = await Loaded();

        model.AcceptDelegatedWork = true;
        model.Peers[0].SendWork = true;
        model.Peers[0].IsShellAllowed = true;
        model.Peers[0].SelectedMode = model.Peers[0].Modes[2];

        Assert.Equal(["server here True  10", "peer True False Direct  False", "peer True False Direct  True", "peer True False AirGapped  True"], calls);
        Assert.Equal(["Any networked worker", "there"], model.Workers.Select(worker => worker.Name));
        Assert.False(model.Peers[0].IsNetworked);
    }

    [Fact]
    public async Task TypedFoldersAndAddressesAreSentBeforeTheNextActionAndWhenTheScreenCloses()
    {
        SwarmViewModel model = await Loaded();
        bool closed = false;
        model.Closed += () => closed = true;
        model.DriveFolder = "/media/usb";
        model.MaxTasks = 500;

        model.SyncCommand.Execute(null);

        Assert.Equal(["server here False /media/usb 100", "sync"], calls);
        Assert.Equal("Read 1 package.", model.Outcome);
        calls.Clear();
        model.Peers[0].Address = " box:4000 ";

        model.CloseCommand.Execute(null);

        Assert.Equal(["peer False False Direct box:4000 False"], calls);
        Assert.True(closed);
    }

    [Fact]
    public async Task ARefusedChangeKeepsTheScreenOpenAndSaysWhy()
    {
        SwarmViewModel model = await Loaded();
        bool closed = false;
        model.Closed += () => closed = true;
        connection.Setup(x => x.SetSwarm(It.IsAny<SwarmServer>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("The server needs a name."));
        model.ServerName = " ";

        model.CloseCommand.Execute(null);

        Assert.False(closed);
        Assert.Equal("The server needs a name.", model.Error);
    }

    [Fact]
    public async Task Delegating_NeedsAWorkspaceTitleAndInstructionsAndReportsTheNewTasks()
    {
        SwarmViewModel model = await Loaded();
        IReadOnlyList<Guid>? followed = null;
        model.Delegated += ids => followed = ids;
        SwarmTaskInfo made = Task(SwarmTaskState.Pending, "new");
        SwarmTaskRequest? sent = null;
        connection.Setup(x => x.DelegateTask(app.Id, It.IsAny<SwarmTaskRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid _, SwarmTaskRequest request, CancellationToken _) =>
        {
            sent = request;
            return status = status with { Tasks = [made, .. status.Tasks] };
        });
        Assert.False(model.DelegateCommand.CanExecute(null));
        model.TaskTitle = " new ";
        model.TaskInstructions = "write it";
        Assert.True(model.DelegateCommand.CanExecute(null));
        model.SelectedCapability = model.Capabilities[2];

        model.DelegateCommand.Execute(null);

        Assert.Equal(("new", "write it", AiCapability.Strong, (string?)null), (sent!.Title, sent.Instructions, sent.Capability, sent.PeerId));
        Assert.Equal([made.Id], followed);
        Assert.Equal((string.Empty, "The task was delegated."), (model.TaskTitle, model.Outcome));
    }

    [Fact]
    public async Task Revoking_AsksFirst()
    {
        SwarmViewModel model = await Loaded();

        model.Peers[0].RevokeCommand.Execute(null);
        Assert.True(model.IsConfirmingRevoke);
        model.CancelRevokeCommand.Execute(null);
        Assert.Empty(calls);

        model.Peers[0].RevokeCommand.Execute(null);
        model.ConfirmRevokeCommand.Execute(null);

        Assert.Equal(["revoke peer"], calls);
        Assert.Empty(model.Peers);
        Assert.True(model.HasNoPeers);
    }

    [Fact]
    public async Task AnOpenedTaskOffersWhatItsStateAllowsFollowsItsChangesAndClosesWhenDiscarded()
    {
        SwarmViewModel model = await Loaded();

        model.OpenTaskCommand.Execute(model.Tasks[0]);

        Assert.True(model.HasDetail);
        Assert.Equal((true, false, false, true), (model.CanApplyTask, model.CanRetryTask, model.CanCancelTask, model.CanDiscardTask));
        Guid id = status.Tasks[0].Id;
        status = status with { Tasks = [status.Tasks[0] with { State = SwarmTaskState.Lost, Branch = null }] };

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.SwarmTaskChanged, WorkspaceId = Guid.Empty, TaskId = id });

        Assert.Equal((false, true, true, true), (model.CanApplyTask, model.CanRetryTask, model.CanCancelTask, model.CanDiscardTask));
        connection.Setup(x => x.DiscardSwarmTask(id, It.IsAny<CancellationToken>())).ReturnsAsync(() => status = status with { Tasks = [] });

        model.DiscardTaskCommand.Execute(null);

        Assert.False(model.HasDetail);
        Assert.True(model.HasNoTasks);
        model.Dispose();
    }
}
