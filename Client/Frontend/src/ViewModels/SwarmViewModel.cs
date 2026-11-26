namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The swarm screen: the settings of this server in the swarm, pairing with other servers, the paired servers with what each may do, syncing the drive, delegating a task by hand and following the delegated tasks.
/// </summary>
internal sealed class SwarmViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    /// <param name="initialTask">A task to open right away, or <see langword="null" />.</param>
    /// <param name="initialWorkspace">The workspace to delegate from by default, or <see langword="null" />.</param>
    public SwarmViewModel(IServerConnection connection, Guid? initialTask = null, Guid? initialWorkspace = null)
    {
        this.connection = connection;
        this.initialTask = initialTask;
        this.initialWorkspace = initialWorkspace;
        selectedCapability = Capabilities.First(choice => choice.Value == AiCapability.Balanced);
        selectedWorker = anyWorker;
        Workers.Add(anyWorker);
        CloseCommand = new AsyncCommand(async _ =>
        {
            if (await Apply())
            {
                Closed?.Invoke();
            }
        });
        CreateCodeCommand = new AsyncCommand(_ => Act(() => connection.CreatePairingCode(CancellationToken.None)));
        PairCommand = new AsyncCommand(async _ =>
        {
            if (await Act(() => connection.PairWithCode(pairAddress.Trim(), pairCode.Trim(), CancellationToken.None)))
            {
                PairCode = string.Empty;
                PairAddress = string.Empty;
            }
        }, () => pairAddress.Trim().Length > 0 && pairCode.Trim().Length > 0);
        InviteCommand = new AsyncCommand(_ => Act(() => connection.WriteDriveInvitation(CancellationToken.None)));
        SyncCommand = new AsyncCommand(_ => Act(() => connection.SyncDrive(CancellationToken.None)));
        DelegateCommand = new AsyncCommand(_ => Delegate(), () => selectedWorkspace is not null && taskTitle.Trim().Length > 0 && taskInstructions.Trim().Length > 0);
        BackCommand = new AsyncCommand(_ =>
        {
            Detail = null;
            return Task.CompletedTask;
        });
        OpenTaskCommand = new AsyncCommand(parameter => parameter is SwarmTaskViewModel row ? Open(row.Task.Id) : Task.CompletedTask, null, false);
        CancelTaskCommand = new AsyncCommand(_ => OnTask(id => connection.CancelSwarmTask(id, CancellationToken.None)));
        RetryTaskCommand = new AsyncCommand(_ => OnTask(id => connection.RetrySwarmTask(id, CancellationToken.None)));
        ApplyTaskCommand = new AsyncCommand(_ => OnTask(id => connection.ApplySwarmTask(id, CancellationToken.None)));
        DiscardTaskCommand = new AsyncCommand(async _ =>
        {
            if (await OnTask(id => connection.DiscardSwarmTask(id, CancellationToken.None)))
            {
                Detail = null;
            }
        });
        ConfirmRevokeCommand = new AsyncCommand(async _ =>
        {
            if (revoking is { } peer)
            {
                Revoking = null;
                await Act(() => connection.RevokeSwarmPeer(peer.Id, CancellationToken.None));
            }
        });
        CancelRevokeCommand = new AsyncCommand(_ =>
        {
            Revoking = null;
            return Task.CompletedTask;
        });
    }

    private readonly IServerConnection connection;

    private readonly Guid? initialTask;

    private readonly Guid? initialWorkspace;

    private readonly Choice<string?> anyWorker = new(null, "Any networked worker");

    private SwarmServer server = new();

    private string serverName = string.Empty;

    private string delegatedFolder = string.Empty;

    private string driveFolder = string.Empty;

    private decimal? maxTasks = 10;

    private SwarmPairingCode? pairingCode;

    private string pairAddress = string.Empty;

    private string pairCode = string.Empty;

    private string? report;

    private SwarmPeerViewModel? revoking;

    private SwarmTaskDetail? detail;

    private WorkspaceInfo? selectedWorkspace;

    private string taskTitle = string.Empty;

    private string taskInstructions = string.Empty;

    private string taskExpected = string.Empty;

    private Choice<AiCapability> selectedCapability;

    private Choice<string?> selectedWorker;

    private bool subscribed;

    private bool loaded;

    private bool missed;

    private bool refreshing;

    private bool refreshAgain;

    private bool reading;

    private bool readAgain;

    /// <summary>
    /// Raised when the screen should give way to the one before it.
    /// </summary>
    public event Action? Closed;

    /// <summary>
    /// Raised with the identifiers of the tasks this screen delegated by hand, so the user is told when they end.
    /// </summary>
    public event Action<IReadOnlyList<Guid>>? Delegated;

    /// <summary>
    /// Gets or sets the name paired servers see.
    /// </summary>
    public string ServerName
    {
        get => serverName;
        set => Set(ref serverName, value ?? string.Empty);
    }

    /// <summary>
    /// Gets the fingerprint of this server.
    /// </summary>
    public string Fingerprint => $"Fingerprint {server.Fingerprint}";

    /// <summary>
    /// Gets or sets a value indicating whether the server takes delegated work; sent as soon as it changes.
    /// </summary>
    public bool AcceptDelegatedWork
    {
        get => server.AcceptDelegatedWork;
        set
        {
            if (value != server.AcceptDelegatedWork)
            {
                server = server with { AcceptDelegatedWork = value };
                Raise(nameof(AcceptDelegatedWork));
                _ = Switch(value);
            }
        }
    }

    /// <summary>
    /// Gets or sets the folder delegated workspaces are made in, empty for the default.
    /// </summary>
    public string DelegatedFolder
    {
        get => delegatedFolder;
        set => Set(ref delegatedFolder, value ?? string.Empty);
    }

    /// <summary>
    /// Gets or sets the folder of the drive on the machine of the server.
    /// </summary>
    public string DriveFolder
    {
        get => driveFolder;
        set => Set(ref driveFolder, value ?? string.Empty);
    }

    /// <summary>
    /// Gets or sets the most tasks a delegated job may have.
    /// </summary>
    public decimal? MaxTasks
    {
        get => maxTasks;
        set => Set(ref maxTasks, value);
    }

    /// <summary>
    /// Gets what the pairing code is and for how long it works, or an empty text.
    /// </summary>
    public string PairingCodeText => pairingCode?.Code ?? string.Empty;

    /// <summary>
    /// Gets the explanation of the pairing code.
    /// </summary>
    public string PairingCodeHint => pairingCode is { } code ? $"On the other server, enter this server's address and this code under Pair with Code before {code.ExpiresAt.ToLocalTime():t}. It works once." : string.Empty;

    /// <summary>
    /// Gets a value indicating whether a pairing code works now.
    /// </summary>
    public bool HasPairingCode => pairingCode is not null;

    /// <summary>
    /// Gets or sets the address of the server to pair with by code.
    /// </summary>
    public string PairAddress
    {
        get => pairAddress;
        set
        {
            if (Set(ref pairAddress, value ?? string.Empty))
            {
                PairCommand.Refresh();
            }
        }
    }

    /// <summary>
    /// Gets or sets the code of the server to pair with.
    /// </summary>
    public string PairCode
    {
        get => pairCode;
        set
        {
            if (Set(ref pairCode, value ?? string.Empty))
            {
                PairCommand.Refresh();
            }
        }
    }

    /// <summary>
    /// Gets the pairings on the drive that wait to be confirmed.
    /// </summary>
    public ObservableCollection<SwarmPairingViewModel> Pairings { get; } = [];

    /// <summary>
    /// Gets the paired servers.
    /// </summary>
    public ObservableCollection<SwarmPeerViewModel> Peers { get; } = [];

    /// <summary>
    /// Gets a value indicating whether no server is paired.
    /// </summary>
    public bool HasNoPeers => loaded && Peers.Count == 0;

    /// <summary>
    /// Gets the delegated tasks, the newest first.
    /// </summary>
    public ObservableCollection<SwarmTaskViewModel> Tasks { get; } = [];

    /// <summary>
    /// Gets a value indicating whether there is no delegated task.
    /// </summary>
    public bool HasNoTasks => loaded && Tasks.Count == 0;

    /// <summary>
    /// Gets the workspaces a task can be delegated from.
    /// </summary>
    public ObservableCollection<WorkspaceInfo> Workspaces { get; } = [];

    /// <summary>
    /// Gets or sets the workspace a task is delegated from.
    /// </summary>
    public WorkspaceInfo? SelectedWorkspace
    {
        get => selectedWorkspace;
        set
        {
            if (Set(ref selectedWorkspace, value))
            {
                DelegateCommand.Refresh();
            }
        }
    }

    /// <summary>
    /// Gets or sets the title of the task to delegate.
    /// </summary>
    public string TaskTitle
    {
        get => taskTitle;
        set
        {
            if (Set(ref taskTitle, value ?? string.Empty))
            {
                DelegateCommand.Refresh();
            }
        }
    }

    /// <summary>
    /// Gets or sets what the task to delegate asks.
    /// </summary>
    public string TaskInstructions
    {
        get => taskInstructions;
        set
        {
            if (Set(ref taskInstructions, value ?? string.Empty))
            {
                DelegateCommand.Refresh();
            }
        }
    }

    /// <summary>
    /// Gets or sets what the result of the task is expected to be.
    /// </summary>
    public string TaskExpected
    {
        get => taskExpected;
        set => Set(ref taskExpected, value ?? string.Empty);
    }

    /// <summary>
    /// Gets the capabilities a task can need.
    /// </summary>
    public IReadOnlyList<Choice<AiCapability>> Capabilities { get; } = [.. Enum.GetValues<AiCapability>().Select(value => new Choice<AiCapability>(value, value.ToString()))];

    /// <summary>
    /// Gets or sets the capability the task needs.
    /// </summary>
    public Choice<AiCapability> SelectedCapability
    {
        get => selectedCapability;
        set
        {
            if (value is not null)
            {
                Set(ref selectedCapability, value);
            }
        }
    }

    /// <summary>
    /// Gets the workers the task can be given to.
    /// </summary>
    public ObservableCollection<Choice<string?>> Workers { get; } = [];

    /// <summary>
    /// Gets or sets the worker the task is given to.
    /// </summary>
    public Choice<string?> SelectedWorker
    {
        get => selectedWorker;
        set
        {
            if (value is not null)
            {
                Set(ref selectedWorker, value);
            }
        }
    }

    /// <summary>
    /// Gets what the last action did, or <see langword="null" />.
    /// </summary>
    public string? Outcome
    {
        get => report;
        private set
        {
            if (Set(ref report, value))
            {
                Raise(nameof(HasOutcome));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether there is something to tell about the last action.
    /// </summary>
    public bool HasOutcome => report is not null;

    /// <summary>
    /// Gets the server whose revocation is being asked about, or <see langword="null" />.
    /// </summary>
    public SwarmPeerViewModel? Revoking
    {
        get => revoking;
        private set
        {
            if (Set(ref revoking, value))
            {
                Raise(nameof(IsConfirmingRevoke));
                Raise(nameof(RevokeText));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the revocation question is showing.
    /// </summary>
    public bool IsConfirmingRevoke => revoking is not null;

    /// <summary>
    /// Gets the revocation question.
    /// </summary>
    public string RevokeText => revoking is null ? string.Empty : $"Revoke {revoking.Name}? Nothing can then be sent between the two until they pair again, and the tasks between them end.";

    /// <summary>
    /// Gets the opened task, or <see langword="null" /> while the lists show.
    /// </summary>
    public SwarmTaskDetail? Detail
    {
        get => detail;
        private set
        {
            if (Set(ref detail, value))
            {
                Raise(nameof(HasDetail));
                Raise(nameof(DetailTitle));
                Raise(nameof(DetailStatus));
                Raise(nameof(DetailError));
                Raise(nameof(HasDetailError));
                Raise(nameof(HasDetailExpected));
                Raise(nameof(HasDetailReport));
                Raise(nameof(HasDetailMessages));
                Raise(nameof(CanCancelTask));
                Raise(nameof(CanRetryTask));
                Raise(nameof(CanApplyTask));
                Raise(nameof(CanDiscardTask));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether a task is opened.
    /// </summary>
    public bool HasDetail => detail is not null;

    /// <summary>
    /// Gets the title of the opened task.
    /// </summary>
    public string DetailTitle => detail?.Task.Title ?? string.Empty;

    /// <summary>
    /// Gets where the opened task stands, with its capability and branch.
    /// </summary>
    public string DetailStatus => detail is null ? string.Empty : string.Join(" - ", new[] { new SwarmTaskViewModel(detail.Task).Detail, detail.Task.Capability.ToString(), detail.Task.Branch }.Where(part => !string.IsNullOrEmpty(part)));

    /// <summary>
    /// Gets why the opened task failed or ended, or <see langword="null" />.
    /// </summary>
    public string? DetailError => detail?.Task.Error;

    /// <summary>
    /// Gets a value indicating whether the opened task has an error.
    /// </summary>
    public bool HasDetailError => detail?.Task.Error is not null;

    /// <summary>
    /// Gets a value indicating whether the opened task says what it expects.
    /// </summary>
    public bool HasDetailExpected => !string.IsNullOrEmpty(detail?.ExpectedResult);

    /// <summary>
    /// Gets a value indicating whether the opened task has a report.
    /// </summary>
    public bool HasDetailReport => !string.IsNullOrEmpty(detail?.Report);

    /// <summary>
    /// Gets a value indicating whether the conversation of the opened task has arrived.
    /// </summary>
    public bool HasDetailMessages => detail is { Messages.Count: > 0 };

    /// <summary>
    /// Gets a value indicating whether the opened task can be cancelled.
    /// </summary>
    public bool CanCancelTask => detail?.Task.CanCancel == true;

    /// <summary>
    /// Gets a value indicating whether the opened task can be delegated again.
    /// </summary>
    public bool CanRetryTask => detail?.Task.CanRetry == true;

    /// <summary>
    /// Gets a value indicating whether the changes of the opened task can be applied.
    /// </summary>
    public bool CanApplyTask => detail?.Task.CanApply == true;

    /// <summary>
    /// Gets a value indicating whether the opened task can be removed.
    /// </summary>
    public bool CanDiscardTask => detail?.Task.CanDiscard == true;

    /// <summary>
    /// Gets the command that sends pending edits and closes the screen.
    /// </summary>
    public AsyncCommand CloseCommand { get; }

    /// <summary>
    /// Gets the command that makes a pairing code.
    /// </summary>
    public AsyncCommand CreateCodeCommand { get; }

    /// <summary>
    /// Gets the command that pairs with the server of a code.
    /// </summary>
    public AsyncCommand PairCommand { get; }

    /// <summary>
    /// Gets the command that writes a pairing invitation to the drive.
    /// </summary>
    public AsyncCommand InviteCommand { get; }

    /// <summary>
    /// Gets the command that syncs the drive.
    /// </summary>
    public AsyncCommand SyncCommand { get; }

    /// <summary>
    /// Gets the command that delegates the written task.
    /// </summary>
    public AsyncCommand DelegateCommand { get; }

    /// <summary>
    /// Gets the command that opens the task given as its parameter.
    /// </summary>
    public AsyncCommand OpenTaskCommand { get; }

    /// <summary>
    /// Gets the command that goes back from the opened task.
    /// </summary>
    public AsyncCommand BackCommand { get; }

    /// <summary>
    /// Gets the command that cancels the opened task.
    /// </summary>
    public AsyncCommand CancelTaskCommand { get; }

    /// <summary>
    /// Gets the command that delegates the opened task again.
    /// </summary>
    public AsyncCommand RetryTaskCommand { get; }

    /// <summary>
    /// Gets the command that applies the changes of the opened task to its workspace.
    /// </summary>
    public AsyncCommand ApplyTaskCommand { get; }

    /// <summary>
    /// Gets the command that removes the opened task.
    /// </summary>
    public AsyncCommand DiscardTaskCommand { get; }

    /// <summary>
    /// Gets the command that revokes the server asked about.
    /// </summary>
    public AsyncCommand ConfirmRevokeCommand { get; }

    /// <summary>
    /// Gets the command that closes the revocation question.
    /// </summary>
    public AsyncCommand CancelRevokeCommand { get; }

    /// <summary>
    /// Reads the swarm and starts following it.
    /// </summary>
    /// <returns>A task that completes once read.</returns>
    public async Task Load()
    {
        if (!subscribed)
        {
            subscribed = true;
            connection.EventReceived += Receive;
            connection.Reconnected += Resync;
        }

        SwarmStatus? status = null;
        WorkspaceCatalog? catalog = null;

        if (await Attempt(async () =>
        {
            status = await connection.GetSwarm(CancellationToken.None);
            catalog = await connection.ListWorkspaces(CancellationToken.None);
        }))
        {
            HashSet<Guid> delegated = [.. catalog!.Delegations.Select(delegation => delegation.WorkspaceId)];

            foreach (WorkspaceInfo workspace in catalog.Workspaces.Where(workspace => !delegated.Contains(workspace.Id)).OrderBy(workspace => workspace.Name, StringComparer.OrdinalIgnoreCase))
            {
                Workspaces.Add(workspace);
            }

            SelectedWorkspace = Workspaces.FirstOrDefault(workspace => workspace.Id == initialWorkspace);
            loaded = true;
            Show(status!, true);

            if (missed)
            {
                missed = false;
                _ = Refresh();
            }

            if (initialTask is { } task)
            {
                await Open(task);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (subscribed)
        {
            subscribed = false;
            connection.EventReceived -= Receive;
            connection.Reconnected -= Resync;
        }
    }

    /// <summary>
    /// Sends the edits still pending, as is done before the screen gives way to another.
    /// </summary>
    /// <returns><see langword="true" /> if there were none or the server took them; <see langword="false" /> when the screen should stay to show why not.</returns>
    public Task<bool> Leave() => Apply();

    private SwarmServer Server() => server with { Name = serverName.Trim(), DelegatedFolder = delegatedFolder.Trim(), DriveFolder = driveFolder.Trim(), MaxTasksPerJob = maxTasks is { } tasks ? (int)Math.Clamp(Math.Round(tasks), 1, 100) : 10 };

    private async Task<bool> Apply()
    {
        if (!loaded)
        {
            return true;
        }

        SwarmServer wanted = Server();

        if (wanted != server && !await Send(() => connection.SetSwarm(wanted, CancellationToken.None), true))
        {
            return false;
        }

        foreach (SwarmPeerViewModel peer in Peers.Where(peer => peer.HasAddressEdit).ToList())
        {
            SwarmPeer settings = peer.Settings();

            if (!await Send(() => connection.SetSwarmPeer(settings, CancellationToken.None), false))
            {
                return false;
            }
        }

        return true;
    }

    // Edits waiting to be sent go first, so an action such as syncing uses the folder that is typed.
    private async Task<bool> Act(Func<Task<SwarmStatus>> action)
    {
        Outcome = null;
        return await Apply() && await Send(action, false);
    }

    private async Task<bool> Send(Func<Task<SwarmStatus>> action, bool serverSent)
    {
        SwarmStatus? status = null;

        if (!await Attempt(async () => status = await action()))
        {
            return false;
        }

        Outcome = status!.Report;
        Show(status, serverSent);
        return true;
    }

    private async Task PeerChanged(SwarmPeerViewModel peer)
    {
        SwarmPeer settings = peer.Settings();

        if (!await Send(() => connection.SetSwarmPeer(settings, CancellationToken.None), false))
        {
            await Refresh();
        }
    }

    private async Task Delegate()
    {
        Guid workspace = selectedWorkspace!.Id;
        SwarmTaskRequest request = new() { Title = taskTitle.Trim(), Instructions = taskInstructions.Trim(), ExpectedResult = taskExpected.Trim(), Capability = selectedCapability.Value, PeerId = selectedWorker.Value };
        HashSet<Guid> before = [.. Tasks.Select(task => task.Task.Id)];

        if (await Act(() => connection.DelegateTask(workspace, request, CancellationToken.None)))
        {
            TaskTitle = string.Empty;
            TaskInstructions = string.Empty;
            TaskExpected = string.Empty;
            Outcome = "The task was delegated.";
            Delegated?.Invoke([.. Tasks.Where(task => !before.Contains(task.Task.Id) && !task.Task.IsIncoming).Select(task => task.Task.Id)]);
        }
    }

    private async Task<bool> OnTask(Func<Guid, Task<SwarmStatus>> action)
    {
        if (detail?.Task.Id is not { } id || !await Act(() => action(id)))
        {
            return false;
        }

        if (Tasks.Any(task => task.Task.Id == id))
        {
            await ReadDetail();
        }

        return true;
    }

    private async Task Open(Guid id)
    {
        SwarmTaskDetail? found = null;

        if (await Attempt(async () => found = await connection.GetSwarmTask(id, CancellationToken.None)))
        {
            Detail = found;
        }
    }

    // A task that can no longer be read was removed, so the list shows again.
    private async Task ReadDetail()
    {
        if (reading)
        {
            readAgain = true;
            return;
        }

        reading = true;

        try
        {
            do
            {
                readAgain = false;

                // Another task may have been opened while the last one was read.
                if (detail?.Task.Id is not { } id)
                {
                    break;
                }

                try
                {
                    SwarmTaskDetail found = await connection.GetSwarmTask(id, CancellationToken.None);

                    if (detail?.Task.Id == id)
                    {
                        Detail = found;
                    }
                }
                catch (Exception exception) when (exception is RemoteException or IOException)
                {
                    if (detail?.Task.Id == id && exception is RemoteException)
                    {
                        Detail = null;
                    }
                }
            }
            while (readAgain);
        }
        finally
        {
            reading = false;
        }
    }

    private async Task Refresh()
    {
        if (refreshing)
        {
            refreshAgain = true;
            return;
        }

        refreshing = true;

        try
        {
            do
            {
                refreshAgain = false;

                try
                {
                    Show(await connection.GetSwarm(CancellationToken.None), false);
                }
                catch (Exception exception) when (exception is RemoteException or IOException)
                {
                }
            }
            while (refreshAgain);
        }
        finally
        {
            refreshing = false;
        }
    }

    private void Receive(RemoteEvent remoteEvent)
    {
        if (!loaded)
        {
            missed |= remoteEvent.Kind == RemoteEventKind.SwarmChanged;
            return;
        }

        if (remoteEvent.Kind == RemoteEventKind.SwarmChanged)
        {
            _ = Refresh();
        }
        else if (remoteEvent.Kind == RemoteEventKind.SwarmTaskChanged && remoteEvent.TaskId is { } id && detail?.Task.Id == id)
        {
            _ = ReadDetail();
        }
    }

    // What changed while the connection was gone was not told, and a screen that could not be read gets another try.
    private void Resync()
    {
        if (!loaded)
        {
            _ = Load();
            return;
        }

        _ = Refresh();
        _ = ReadDetail();
    }

    // Edits sent first bring the server's old values back, so what was chosen is kept aside.
    private async Task Switch(bool accept)
    {
        if (!await Act(() => connection.SetSwarm(Server() with { AcceptDelegatedWork = accept }, CancellationToken.None)))
        {
            await Refresh();
        }
    }

    private void Show(SwarmStatus status, bool takeServer)
    {
        bool editing = !takeServer && (serverName != server.Name || delegatedFolder != server.DelegatedFolder || driveFolder != server.DriveFolder || maxTasks != server.MaxTasksPerJob);
        server = status.Server;

        if (!editing)
        {
            serverName = server.Name;
            delegatedFolder = server.DelegatedFolder;
            driveFolder = server.DriveFolder;
            maxTasks = server.MaxTasksPerJob;
        }

        pairingCode = status.PairingCode;

        for (int index = Peers.Count - 1; index >= 0; index--)
        {
            if (status.Peers.All(peer => peer.Id != Peers[index].Id))
            {
                Peers.RemoveAt(index);
            }
        }

        foreach (SwarmPeer peer in status.Peers)
        {
            if (Peers.FirstOrDefault(row => row.Id == peer.Id) is { } existing)
            {
                existing.Show(peer);
            }
            else
            {
                Peers.Add(new SwarmPeerViewModel(peer, PeerChanged, row => Revoking = row) { IsExpanded = true });
            }
        }

        List<Choice<string?>> workers = [anyWorker, .. status.Peers.Where(peer => peer.SendWork).Select(peer => new Choice<string?>(peer.Id, peer.Name))];

        if (!workers.SequenceEqual(Workers))
        {
            string? chosen = selectedWorker.Value;
            Workers.Clear();

            foreach (Choice<string?> worker in workers)
            {
                Workers.Add(worker);
            }

            selectedWorker = workers.FirstOrDefault(worker => worker.Value == chosen) ?? anyWorker;
            Raise(nameof(SelectedWorker));
        }

        // A row that stays keeps its button, which is disabled while its confirmation runs.
        if (!status.DrivePairings.SequenceEqual(Pairings.Select(row => row.Pairing)))
        {
            Pairings.Clear();

            foreach (SwarmDrivePairing pairing in status.DrivePairings)
            {
                Pairings.Add(new SwarmPairingViewModel(pairing, found => Act(() => connection.ConfirmDrivePairing(found.Id, CancellationToken.None))));
            }
        }

        List<SwarmTaskViewModel> tasks = [.. status.Tasks.Select(task => new SwarmTaskViewModel(task))];

        if (!tasks.SequenceEqual(Tasks))
        {
            Tasks.Clear();

            foreach (SwarmTaskViewModel task in tasks)
            {
                Tasks.Add(task);
            }
        }

        Raise(nameof(ServerName));
        Raise(nameof(Fingerprint));
        Raise(nameof(AcceptDelegatedWork));
        Raise(nameof(DelegatedFolder));
        Raise(nameof(DriveFolder));
        Raise(nameof(MaxTasks));
        Raise(nameof(PairingCodeText));
        Raise(nameof(PairingCodeHint));
        Raise(nameof(HasPairingCode));
        Raise(nameof(HasNoPeers));
        Raise(nameof(HasNoTasks));
    }
}
