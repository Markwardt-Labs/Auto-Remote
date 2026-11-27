namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The main screen: the workspace list organized as a tree of groups, the focused workspace, and the dialogs that rename, delete or create things.
/// </summary>
internal sealed class HomeViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HomeViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    /// <param name="layout">How a focused workspace lays out its conversation and files.</param>
    /// <param name="drafts">Where the text typed in each workspace's message box is kept, or <see langword="null" /> to keep none.</param>
    /// <param name="chime">Plays the ding when a workspace becomes ready again, or <see langword="null" /> for silence.</param>
    /// <param name="time">The clock that times how long a workspace was busy, or <see langword="null" /> for the system clock.</param>
    /// <param name="notifier">Tells the user outside the app that a workspace they gave work to is ready, or <see langword="null" /> for no notifications.</param>
    /// <param name="keeper">Keeps the app alive while work this client gave a workspace runs, or <see langword="null" /> when the app is not stopped in the background.</param>
    /// <param name="recorder">Records speech for voice input, or <see langword="null" /> when this client cannot record.</param>
    /// <param name="newWindow">Opens another window of the app as a process of its own, or <see langword="null" /> when this client cannot, which hides the button.</param>
    /// <param name="update">Offers an update of the client and the server, or <see langword="null" /> when none is offered.</param>
    /// <param name="preferences">Where display choices such as numbered markdown sections are kept, or <see langword="null" /> to keep none.</param>
    public HomeViewModel(IServerConnection connection, ClientLayout layout = ClientLayout.Tabs, IDraftStore? drafts = null, IChime? chime = null, TimeProvider? time = null, IReadyNotifier? notifier = null, IWorkKeeper? keeper = null, IVoiceRecorder? recorder = null, INewWindow? newWindow = null, UpdateViewModel? update = null, IViewPreferences? preferences = null)
    {
        Update = update;
        this.recorder = recorder;
        this.newWindow = newWindow;
        this.notifier = notifier;
        this.keeper = keeper;
        this.chime = chime;
        this.time = time ?? TimeProvider.System;
        this.connection = connection;
        this.layout = layout;
        this.drafts = drafts;
        this.preferences = preferences;
        connection.Lost += Lose;
        connection.Reconnected += Return;
        connection.EventReceived += Receive;
        Usage = new UsageViewModel(connection);
        Resources = new ResourcesViewModel(connection) { IsSectionShown = false };
        Resources.Updated += RefreshBadges;
        // What was edited in main's settings applies to the session made next.
        Sessions = new SessionsViewModel(connection, id => opened.TryGetValue(id, out WorkspaceViewModel? view) && view.IsAiWorking || busy.Contains(id), id => opened.TryGetValue(id, out WorkspaceViewModel? view) ? view.Settings.Apply() : Task.FromResult(true));
        Sessions.Failed += message => active?.Chat.Show(message);
        Sessions.Discarding += Disarm;
        Sessions.Chosen += id => ShowSession(id);
        Sessions.ChangesRequested += id =>
        {
            if (ShowSession(id))
            {
                _ = active!.ShowSessionChanges();
            }
        };
        Sessions.Made += (made, hasTask) => Await(made, hasTask);
        ToggleUsageCommand = new AsyncCommand(_ =>
        {
            IsUsageOpen = !isUsageOpen;
            return Task.CompletedTask;
        });
        ToggleNotifyCommand = new AsyncCommand(_ =>
        {
            ToggleNotify();
            return Task.CompletedTask;
        });
        ToggleDrawerCommand = new AsyncCommand(_ =>
        {
            IsDrawerOpen = !isDrawerOpen;
            return Task.CompletedTask;
        });
        NewWorkspaceCommand = new AsyncCommand(_ =>
        {
            NewWorkspaceRequested?.Invoke();
            return Task.CompletedTask;
        });
        CloneWorkspaceCommand = new AsyncCommand(_ =>
        {
            CloneWorkspaceRequested?.Invoke();
            return Task.CompletedTask;
        });
        SwarmCommand = new AsyncCommand(_ =>
        {
            SwarmRequested?.Invoke(null, selected?.Id);
            return Task.CompletedTask;
        });
        EnvironmentCommand = new AsyncCommand(_ =>
        {
            EnvironmentRequested?.Invoke();
            return Task.CompletedTask;
        });
        NewWindowCommand = new AsyncCommand(_ => OpenNewWindow());
        ChangeServerCommand = new AsyncCommand(_ =>
        {
            ChangeServerRequested?.Invoke();
            return Task.CompletedTask;
        });
        BeginStopCommand = new AsyncCommand(_ => Begin(EditMode.StopWork, string.Empty), () => active?.CanHalt ?? false);
        ConfirmHaltCommand = new AsyncCommand(_ => Halt());
        MoveWorkspaceToFolderCommand = new AsyncCommand(
            async _ =>
            {
                if (selected is not null && (active is null || await active.Settings.Apply()))
                {
                    MoveWorkspaceRequested?.Invoke(selected);
                }
            },
            () => CanChangeSelected);
        BeginRenameCommand = new AsyncCommand(_ => Begin(EditMode.RenameWorkspace, selected?.Name ?? string.Empty), () => CanChangeSelected);
        ConfirmRenameCommand = new AsyncCommand(_ => Rename());
        BeginDeleteCommand = new AsyncCommand(
            _ =>
            {
                DeleteFolder = true;
                return Begin(EditMode.DeleteWorkspace, string.Empty);
            },
            () => CanEditSelected);
        ConfirmDeleteCommand = new AsyncCommand(_ => Delete());
        BeginNewGroupCommand = new AsyncCommand(_ => BeginNewGroup());
        ConfirmNewGroupCommand = new AsyncCommand(_ => CreateGroup());
        BeginRenameGroupCommand = new AsyncCommand(group => Begin(EditMode.RenameGroup, (group as WorkspaceGroup)?.Name ?? string.Empty, group as WorkspaceGroup));
        ConfirmRenameGroupCommand = new AsyncCommand(_ => RenameGroup());
        BeginDeleteGroupCommand = new AsyncCommand(group => Begin(EditMode.DeleteGroup, string.Empty, group as WorkspaceGroup));
        ConfirmDeleteGroupCommand = new AsyncCommand(_ => DeleteGroup());
        BeginMoveWorkspaceCommand = new AsyncCommand(workspace => BeginMove(EditMode.MoveWorkspace, workspace as WorkspaceInfo, null));
        BeginMoveGroupCommand = new AsyncCommand(group => BeginMove(EditMode.MoveGroup, null, group as WorkspaceGroup));
        ConfirmMoveCommand = new AsyncCommand(_ => Move());
        ShiftGroupUpCommand = new AsyncCommand(group => Shift(group as WorkspaceGroup, -1));
        ShiftGroupDownCommand = new AsyncCommand(group => Shift(group as WorkspaceGroup, 1));
        CancelEditCommand = new AsyncCommand(_ =>
        {
            EditMode = EditMode.None;
            Error = null;
            return Task.CompletedTask;
        });
    }

    private readonly IServerConnection connection;

    private readonly ClientLayout layout;

    private readonly IDraftStore? drafts;

    private readonly IViewPreferences? preferences;

    private readonly IVoiceRecorder? recorder;

    private readonly INewWindow? newWindow;

    private readonly IChime? chime;

    private readonly IReadyNotifier? notifier;

    private readonly IWorkKeeper? keeper;

    private readonly TimeProvider time;

    private readonly TimeSpan minimumBusy = TimeSpan.FromSeconds(2);

    private bool isUsageOpen;

    private readonly List<WorkspaceInfo> workspaces = [];

    private readonly List<WorkspaceInfo> sideSessions = [];

    private readonly Dictionary<Guid, Guid> shownSessions = [];

    private Guid? awaited;

    // Sessions made here with a first task, which the user is told about once it is done, whatever is on screen by then.
    private readonly HashSet<Guid> tasked = [];

    private readonly List<WorkspaceGroup> groups = [];

    private readonly HashSet<Guid> expanded = [];

    private readonly HashSet<Guid> busy = [];

    private readonly Dictionary<Guid, ResourceBadge> badges = [];

    private readonly Dictionary<Guid, bool> busyHeard = [];

    private readonly HashSet<Guid> looping = [];

    private readonly HashSet<Guid> waiting = [];

    private readonly Dictionary<Guid, WorkspaceDelegation> delegations = [];

    private readonly List<SwarmTaskInfo> swarmTasks = [];

    private readonly HashSet<Guid> followed = [];

    private readonly HashSet<Guid> held = [];

    private bool refreshingSwarm;

    private bool refreshSwarmAgain;

    private readonly Dictionary<Guid, WorkspaceViewModel> opened = [];

    private WorkspaceInfo? selected;

    private WorkspaceViewModel? active;

    private DrawerEntry? selectedEntry;

    private EditMode editMode;

    private WorkspaceGroup? editingGroup;

    private WorkspaceInfo? editingWorkspace;

    private GroupChoice? destination;

    private bool building;

    private bool isDrawerOpen;

    private bool isOffline;

    private bool refreshing;

    private bool refreshAgain;

    private string renameText = string.Empty;

    private string filter = string.Empty;

    private bool deleteFolder = true;

    /// <summary>
    /// Raised when the user asks to create a workspace.
    /// </summary>
    public event Action? NewWorkspaceRequested;

    /// <summary>
    /// Raised when the user asks to clone a repository into a new workspace.
    /// </summary>
    public event Action? CloneWorkspaceRequested;

    /// <summary>
    /// Raised when the user opens the environment settings of the server's AI.
    /// </summary>
    public event Action? EnvironmentRequested;

    /// <summary>
    /// Raised when the swarm screen should show, with a delegated task to open or <see langword="null" />, and the focused workspace.
    /// </summary>
    public event Action<Guid?, Guid?>? SwarmRequested;

    /// <summary>
    /// Raised with the selected workspace when the user asks to move it to another folder.
    /// </summary>
    public event Action<WorkspaceInfo>? MoveWorkspaceRequested;

    /// <summary>
    /// Raised when the user asks to connect to a different server.
    /// </summary>
    public event Action? ChangeServerRequested;

    /// <summary>
    /// Gets the drawer rows: the group tree flattened in display order. Within the top level and within each group the groups come first, in their manual order, and then the workspaces: the working ones first, then the ones a loop runs in, and among each the one whose AI session was loaded last first. Groups start collapsed, and the groups holding a workspace that gets focused open so that it shows.
    /// </summary>
    public ObservableCollection<DrawerEntry> Entries { get; } = [];

    /// <summary>
    /// Gets or sets the text the drawer is filtered by: only workspaces whose name or folder contains it show, with the groups that hold them or whose name contains it (everything in such a group shows). Groups are all open while it is set.
    /// </summary>
    public string Filter
    {
        get => filter;
        set
        {
            if (Set(ref filter, value ?? string.Empty))
            {
                BuildEntries();
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the filter hides every row.
    /// </summary>
    public bool HasNoMatches => IsFiltering && Entries.Count == 0;

    private bool IsFiltering => !string.IsNullOrWhiteSpace(filter);

    /// <summary>
    /// Gets or sets the tapped drawer row; tapping a workspace focuses it and tapping a group expands or collapses it.
    /// </summary>
    public DrawerEntry? SelectedEntry
    {
        get => selectedEntry;
        set
        {
            if (!building && value is not null)
            {
                AfterSelection(() => Tap(value));
            }
        }
    }

    /// <summary>
    /// Gets or sets how a tap on a drawer row is acted on: at once, unless a view puts it off until its list has finished selecting the row, since the rows must not change while it does.
    /// </summary>
    public Action<Action> AfterSelection { get; set; } = action => action();

    /// <summary>
    /// Gets the usage limits of the AI service of the focused workspace.
    /// </summary>
    public UsageViewModel Usage { get; }

    /// <summary>
    /// Gets what the workspaces take of the server's computer, which the workspace settings show for the focused workspace and the workspace list for the working ones.
    /// </summary>
    public ResourcesViewModel Resources { get; }

    /// <summary>
    /// Gets the sessions of the selected workspace.
    /// </summary>
    public SessionsViewModel Sessions { get; }

    /// <summary>
    /// Gets a value indicating whether a side session of the selected workspace is on screen.
    /// </summary>
    public bool IsSideSession => active?.IsSideSession ?? false;

    /// <summary>
    /// Gets a value indicating whether the main session of the selected workspace is on screen.
    /// </summary>
    public bool IsMainSession => !IsSideSession;

    /// <summary>
    /// Gets a value indicating whether the selected workspace has side sessions, which go when it is deleted.
    /// </summary>
    public bool HasSideSessions => selected is not null && sideSessions.Any(session => session.Side!.ParentId == selected.Id);

    /// <summary>
    /// Gets the command that shows or hides the usage limits.
    /// </summary>
    public AsyncCommand ToggleUsageCommand { get; }

    /// <summary>
    /// Gets or sets a value indicating whether the usage limits are showing.
    /// </summary>
    public bool IsUsageOpen
    {
        get => isUsageOpen;
        set => Set(ref isUsageOpen, value);
    }

    /// <summary>
    /// Gets or sets the focused workspace; setting it switches to that workspace.
    /// </summary>
    public WorkspaceInfo? Selected
    {
        get => selected;
        set
        {
            if (!Set(ref selected, value))
            {
                return;
            }

            IsDrawerOpen = false;
            IsUsageOpen = false;
            EditMode = EditMode.None;
            _ = active?.Settings.Apply();

            if (value is not null)
            {
                ForgetUnshowable(value);
            }

            Resources.Workspace = value?.Id;
            Active = value is null ? null : Open(Shown(value));
            BeginStopCommand.Refresh();
            _ = Active?.Focus();
            _ = Usage.Show(value?.Service);

            Raise(nameof(HasSelection));
            RaiseSession();
            RefreshSessions();

            if (value is not null && ExpandAround(value))
            {
                BuildEntries();
            }

            SyncSelectedEntry();
            BeginRenameCommand.Refresh();
            MoveWorkspaceToFolderCommand.Refresh();
            BeginDeleteCommand.Refresh();
        }
    }

    /// <summary>
    /// Gets the view model of the focused workspace.
    /// </summary>
    public WorkspaceViewModel? Active
    {
        get => active;
        private set
        {
            WorkspaceViewModel? before = active;

            if (Set(ref active, value))
            {
                if (before is not null)
                {
                    before.Blur();
                }

                Resources.IsSectionShown = value?.ShowSettings ?? false;
                Raise(nameof(IsAiWorking));
                Raise(nameof(IsWorking));
                RaiseNotify();
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the button that turns the ready notification on and off shows: the app can notify, and the focused workspace has work that is not over, being busy, looping, holding queued work or inputs or waiting for tasks it delegated, or its notification is on.
    /// </summary>
    public bool CanNotify => notifier is not null && active is { } view && (view.NotifyWhenReady || view.State != WorkspaceState.Ready || view.IsLooping || view.Chat.IsQueued || view.Chat.HasActiveQueued || HasTasksUnderWay(view.Workspace.Id));

    /// <summary>
    /// Gets a value indicating whether the user is told once the work of the focused workspace is over.
    /// </summary>
    public bool IsNotifying => active?.NotifyWhenReady ?? false;

    /// <summary>
    /// Gets what the notification button says it does.
    /// </summary>
    public string NotifyTip => IsNotifying ? "You will be notified when this workspace is ready. Press to turn the notification off." : "Notify me when this workspace is ready";

    /// <summary>
    /// Gets the command that turns the ready notification of the focused workspace on or off.
    /// </summary>
    public AsyncCommand ToggleNotifyCommand { get; }

    /// <summary>
    /// Gets a value indicating whether the AI is working in the focused workspace.
    /// </summary>
    public bool IsAiWorking => active?.IsAiWorking ?? false;

    /// <summary>
    /// Gets a value indicating whether the focused workspace is busy (the AI works or git runs), which turns the header blue.
    /// </summary>
    public bool IsWorking => active?.IsWorking ?? false;

    /// <summary>
    /// Gets a value indicating whether the client is the desktop one, which has its own window controls and offers the workspace folder on the machine.
    /// </summary>
    public bool IsDesktop => layout == ClientLayout.Panels;

    /// <summary>
    /// Gets the width of the header cell on each side of the title, which the desktop client widens for the window controls.
    /// </summary>
    public GridLength HeaderSide => new(layout == ClientLayout.Panels ? 250 : 76);

    /// <summary>
    /// Gets a value indicating whether the focused workspace can be renamed or deleted: it is focused, shows its main session and is <see cref="WorkspaceState.Ready" />.
    /// </summary>
    public bool CanEditSelected => selected is not null && !IsSideSession && (active?.State ?? WorkspaceState.Ready) == WorkspaceState.Ready;

    /// <summary>
    /// Gets a value indicating whether the focused workspace can be renamed or moved: <see cref="CanEditSelected" />, and it runs no task another server delegated, which still lets it be deleted.
    /// </summary>
    public bool CanChangeSelected => CanEditSelected && active?.Delegation is null;

    /// <summary>
    /// Gets what stopping the work of a delegated workspace means, or <see langword="null" /> for any other workspace.
    /// </summary>
    public string? StopNote => active?.Delegation is { } task ? $"This cancels the task delegated by {task.From}, which is told." : null;

    /// <summary>
    /// Gets a value indicating whether <see cref="StopNote" /> has something to say.
    /// </summary>
    public bool HasStopNote => StopNote is not null;

    /// <summary>
    /// Gets a value indicating whether a workspace is focused.
    /// </summary>
    public bool HasSelection => selected is not null;

    /// <summary>
    /// Gets a value indicating whether the connection to the server is lost and being reopened, which the header says.
    /// </summary>
    public bool IsOffline
    {
        get => isOffline;
        private set
        {
            if (Set(ref isOffline, value))
            {
                Raise(nameof(Title));
            }
        }
    }

    /// <summary>
    /// Gets the header text: the name of the focused workspace, with that of the side session on screen, and a note while the server is out of reach.
    /// </summary>
    public string Title => (selected is null ? "Auto Remote" : sideSessions.FirstOrDefault(session => session.Id == active?.Workspace.Id)?.Name ?? selected.Name) + (isOffline ? " (reconnecting…)" : string.Empty);

    /// <summary>
    /// Gets or sets a value indicating whether the workspace list drawer is open.
    /// </summary>
    public bool IsDrawerOpen
    {
        get => isDrawerOpen;
        set
        {
            if (Set(ref isDrawerOpen, value))
            {
                Resources.IsListed = value;
            }
        }
    }

    /// <summary>
    /// Gets the dialog showing now.
    /// </summary>
    public EditMode EditMode
    {
        get => editMode;
        private set
        {
            if (Set(ref editMode, value))
            {
                Raise(nameof(IsRenaming));
                Raise(nameof(IsDeleting));
                Raise(nameof(IsCreatingGroup));
                Raise(nameof(IsRenamingGroup));
                Raise(nameof(IsDeletingGroup));
                Raise(nameof(IsMovingWorkspace));
                Raise(nameof(IsMovingGroup));
                Raise(nameof(IsMoving));
                Raise(nameof(IsEditing));
                Raise(nameof(IsHalting));
                Raise(nameof(IsStoppingWork));
                Raise(nameof(StopNote));
                Raise(nameof(HasStopNote));
                Raise(nameof(CancelEditText));
                Raise(nameof(NeedsName));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the workspace rename dialog is showing.
    /// </summary>
    public bool IsRenaming => editMode == EditMode.RenameWorkspace;

    /// <summary>
    /// Gets a value indicating whether the workspace delete confirmation is showing.
    /// </summary>
    public bool IsDeleting => editMode == EditMode.DeleteWorkspace;

    /// <summary>
    /// Gets a value indicating whether the new group dialog is showing.
    /// </summary>
    public bool IsCreatingGroup => editMode == EditMode.NewGroup;

    /// <summary>
    /// Gets a value indicating whether the group rename dialog is showing.
    /// </summary>
    public bool IsRenamingGroup => editMode == EditMode.RenameGroup;

    /// <summary>
    /// Gets a value indicating whether the group delete confirmation is showing.
    /// </summary>
    public bool IsDeletingGroup => editMode == EditMode.DeleteGroup;

    /// <summary>
    /// Gets a value indicating whether the dialog for moving a workspace is showing.
    /// </summary>
    public bool IsMovingWorkspace => editMode == EditMode.MoveWorkspace;

    /// <summary>
    /// Gets a value indicating whether the dialog for moving a group is showing.
    /// </summary>
    public bool IsMovingGroup => editMode == EditMode.MoveGroup;

    /// <summary>
    /// Gets a value indicating whether a move dialog is showing.
    /// </summary>
    public bool IsMoving => editMode is EditMode.MoveWorkspace or EditMode.MoveGroup;

    /// <summary>
    /// Gets a value indicating whether the question whether to stop the work is showing.
    /// </summary>
    public bool IsHalting => editMode == EditMode.StopWork;

    /// <summary>
    /// Gets a value indicating whether the question whether to stop the work, keeping what it did, is showing.
    /// </summary>
    public bool IsStoppingWork => editMode == EditMode.StopWork;

    /// <summary>
    /// Gets the label of the button that closes the showing dialog without doing anything.
    /// </summary>
    public string CancelEditText => IsHalting ? "Keep Working" : "Cancel";

    /// <summary>
    /// Gets a value indicating whether any dialog is showing.
    /// </summary>
    public bool IsEditing => editMode != EditMode.None;

    /// <summary>
    /// Gets a value indicating whether the showing dialog has a name box.
    /// </summary>
    public bool NeedsName => editMode is EditMode.RenameWorkspace or EditMode.NewGroup or EditMode.RenameGroup;

    /// <summary>
    /// Gets the name of the group the dialog is about.
    /// </summary>
    public string EditingGroupName => editingGroup?.Name ?? string.Empty;

    /// <summary>
    /// Gets the choice of the group the new group is created in.
    /// </summary>
    public GroupPickerViewModel NewGroupParent { get; } = new();

    /// <summary>
    /// Gets the name of what the move dialog moves: the workspace or the group.
    /// </summary>
    public string MovingName => editingWorkspace?.Name ?? editingGroup?.Name ?? string.Empty;

    /// <summary>
    /// Gets the places the move dialog offers, starting with the top level.
    /// </summary>
    public ObservableCollection<GroupChoice> Destinations { get; } = [];

    /// <summary>
    /// Gets or sets the place chosen in the move dialog.
    /// </summary>
    public GroupChoice? Destination
    {
        get => destination;
        set => Set(ref destination, value);
    }

    /// <summary>
    /// Gets or sets the name typed in the showing dialog.
    /// </summary>
    public string RenameText
    {
        get => renameText;
        set => Set(ref renameText, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether deleting the workspace also deletes its folder.
    /// </summary>
    public bool DeleteFolder
    {
        get => deleteFolder;
        set => Set(ref deleteFolder, value);
    }

    /// <summary>
    /// Gets the command that opens or closes the workspace drawer.
    /// </summary>
    public AsyncCommand ToggleDrawerCommand { get; }

    /// <summary>
    /// Gets the command that asks for a new workspace.
    /// </summary>
    public AsyncCommand NewWorkspaceCommand { get; }

    /// <summary>
    /// Gets the command that asks to clone a repository into a new workspace.
    /// </summary>
    public AsyncCommand CloneWorkspaceCommand { get; }

    /// <summary>
    /// Gets the command that opens the environment settings of the server's AI.
    /// </summary>
    public AsyncCommand EnvironmentCommand { get; }

    /// <summary>
    /// Gets the command that shows the swarm screen.
    /// </summary>
    public AsyncCommand SwarmCommand { get; }

    /// <summary>
    /// Gets the command that asks to change server.
    /// </summary>
    public AsyncCommand ChangeServerCommand { get; }

    /// <summary>
    /// Gets the update of the client and the server that is offered next to the workspaces button, or <see langword="null" /> when none is.
    /// </summary>
    public UpdateViewModel? Update { get; }

    /// <summary>
    /// Gets a value indicating whether this client can open another window of the app, which shows the button for it.
    /// </summary>
    public bool CanOpenNewWindow => newWindow is not null;

    /// <summary>
    /// Gets the command that opens another window of the app as a process of its own.
    /// </summary>
    public AsyncCommand NewWindowCommand { get; }

    /// <summary>
    /// Gets the command that asks whether to stop the work in the focused workspace at once, keeping what it did.
    /// </summary>
    public AsyncCommand BeginStopCommand { get; }

    /// <summary>
    /// Gets the command that does what the showing stop or cancel question asks.
    /// </summary>
    public AsyncCommand ConfirmHaltCommand { get; }

    /// <summary>
    /// Gets the command that opens the workspace rename dialog.
    /// </summary>
    public AsyncCommand BeginRenameCommand { get; }

    /// <summary>
    /// Gets the command that opens the screen that moves the selected workspace to another folder.
    /// </summary>
    public AsyncCommand MoveWorkspaceToFolderCommand { get; }

    /// <summary>
    /// Gets the command that applies the workspace rename.
    /// </summary>
    public AsyncCommand ConfirmRenameCommand { get; }

    /// <summary>
    /// Gets the command that opens the workspace delete confirmation.
    /// </summary>
    public AsyncCommand BeginDeleteCommand { get; }

    /// <summary>
    /// Gets the command that deletes the workspace.
    /// </summary>
    public AsyncCommand ConfirmDeleteCommand { get; }

    /// <summary>
    /// Gets the command that opens the new group dialog, where the group it is created in is chosen.
    /// </summary>
    public AsyncCommand BeginNewGroupCommand { get; }

    /// <summary>
    /// Gets the command that creates the group.
    /// </summary>
    public AsyncCommand ConfirmNewGroupCommand { get; }

    /// <summary>
    /// Gets the command that opens the group rename dialog for the group passed as its parameter.
    /// </summary>
    public AsyncCommand BeginRenameGroupCommand { get; }

    /// <summary>
    /// Gets the command that applies the group rename.
    /// </summary>
    public AsyncCommand ConfirmRenameGroupCommand { get; }

    /// <summary>
    /// Gets the command that opens the group delete confirmation for the group passed as its parameter.
    /// </summary>
    public AsyncCommand BeginDeleteGroupCommand { get; }

    /// <summary>
    /// Gets the command that deletes the group.
    /// </summary>
    public AsyncCommand ConfirmDeleteGroupCommand { get; }

    /// <summary>
    /// Gets the command that opens the move dialog for the workspace passed as its parameter.
    /// </summary>
    public AsyncCommand BeginMoveWorkspaceCommand { get; }

    /// <summary>
    /// Gets the command that opens the move dialog for the group passed as its parameter.
    /// </summary>
    public AsyncCommand BeginMoveGroupCommand { get; }

    /// <summary>
    /// Gets the command that applies the move.
    /// </summary>
    public AsyncCommand ConfirmMoveCommand { get; }

    /// <summary>
    /// Gets the command that moves the group passed as its parameter up among the groups with the same parent.
    /// </summary>
    public AsyncCommand ShiftGroupUpCommand { get; }

    /// <summary>
    /// Gets the command that moves the group passed as its parameter down among the groups with the same parent.
    /// </summary>
    public AsyncCommand ShiftGroupDownCommand { get; }

    /// <summary>
    /// Gets the command that closes the showing dialog.
    /// </summary>
    public AsyncCommand CancelEditCommand { get; }

    /// <summary>
    /// Brings a workspace to the front, as opening its notification does: it is focused with the drawer and any dialog closed, and the workspaces are read again when it is not known yet.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>A task that completes once the workspace is shown.</returns>
    public async Task Show(Guid id)
    {
        if (workspaces.FirstOrDefault(workspace => workspace.Id == MainOf(id)) is { } known)
        {
            Selected = known;
            ShowSession(id);
            EditMode = EditMode.None;
            IsDrawerOpen = false;
        }
        else
        {
            WorkspaceCatalog? catalog = null;

            // A notification can outlive what it is about, which then leaves the screen as it is.
            if (await Attempt(async () => catalog = await List()) && catalog!.Workspaces.Concat(catalog.Sessions).Any(workspace => workspace.Id == id))
            {
                await Start(id, false);
            }
        }

        Sessions.IsOpen = false;
    }

    // The main workspace of what the identifier names, which is itself unless it names a side session.
    private Guid MainOf(Guid id) => sideSessions.FirstOrDefault(session => session.Id == id)?.Side!.ParentId ?? id;

    // The session of a workspace that is on screen when it is selected.
    private WorkspaceInfo Shown(WorkspaceInfo workspace) => shownSessions.TryGetValue(workspace.Id, out Guid id) && sideSessions.FirstOrDefault(session => session.Id == id) is { Side.State: SideSessionState.Ready } side ? side : workspace;

    private bool ShowSession(Guid id)
    {
        if (selected is null || MainOf(id) != selected.Id)
        {
            return false;
        }

        if (id == selected.Id)
        {
            shownSessions.Remove(selected.Id);
        }
        else if (sideSessions.Any(session => session.Id == id && session.Side!.State == SideSessionState.Ready))
        {
            shownSessions[selected.Id] = id;
        }
        else
        {
            return false;
        }

        awaited = null;
        Swap();
        RefreshSessions();
        return true;
    }

    // Main shown in the place of a session that cannot be shown stays, rather than giving way to it again later.
    private void ForgetUnshowable(WorkspaceInfo workspace)
    {
        if (shownSessions.ContainsKey(workspace.Id) && Shown(workspace).Id == workspace.Id)
        {
            shownSessions.Remove(workspace.Id);
        }
    }

    // Work that is thrown away does not end in a notification that it is ready.
    private void Disarm(Guid id)
    {
        tasked.Remove(id);

        if (opened.TryGetValue(id, out WorkspaceViewModel? view))
        {
            view.NotifyWhenReady = false;
        }

        keeper?.Release(id);
        notifier?.Clear(id);
    }

    // The server sends a first task once the session is set up, and the user is told when it is done as for one typed here.
    private void FollowTasks()
    {
        foreach (Guid id in tasked.ToList())
        {
            if (sideSessions.FirstOrDefault(session => session.Id == id) is not { Side.State: not SideSessionState.Deleting } session)
            {
                Disarm(id);
            }
            else if (session.Side.State == SideSessionState.Ready)
            {
                tasked.Remove(id);

                if (session.Side.Error is null)
                {
                    Open(session).NotifyWhenReady = true;
                }
                else
                {
                    keeper?.Release(id);
                }
            }
        }
    }

    // Puts the view of the session that is to be on screen there, when it is not already.
    private void Swap()
    {
        if (selected is null || Shown(selected) is not { } wanted || active?.Workspace.Id == wanted.Id)
        {
            return;
        }

        _ = active?.Settings.Apply();
        EditMode = EditMode.None;
        Active = Open(wanted);
        BeginStopCommand.Refresh();
        _ = Active!.Focus();
        RaiseSession();
    }

    private void RaiseSession()
    {
        Raise(nameof(Title));
        Raise(nameof(IsSideSession));
        Raise(nameof(IsMainSession));
        Raise(nameof(HasSideSessions));
        BeginRenameCommand.Refresh();
        MoveWorkspaceToFolderCommand.Refresh();
        BeginDeleteCommand.Refresh();
    }

    // A new session is switched to as soon as it is set up, unless the user went somewhere else in the meantime.
    private void Await(WorkspaceInfo made, bool hasTask)
    {
        if (hasTask)
        {
            tasked.Add(made.Id);
            keeper?.Hold(made.Id, made.Name);
        }

        if (selected is not null && made.Side?.ParentId == selected.Id)
        {
            awaited = made.Id;
        }

        // The list may have said by now that the session is set up.
        if (sideSessions.Any(session => session.Id == made.Id))
        {
            FollowTasks();
            ShowAwaited();
        }
    }

    private void ShowAwaited()
    {
        if (awaited is not { } wanted || sideSessions.FirstOrDefault(session => session.Id == wanted) is not { Side.State: SideSessionState.Ready } ready)
        {
            return;
        }

        awaited = null;

        // Choosing a session meanwhile ends the wait, so only another workspace is left to look out for. The list would cover the session that was just made.
        if (selected?.Id == ready.Side.ParentId && ShowSession(ready.Id))
        {
            Sessions.IsOpen = false;
        }
    }

    private void RefreshSessions()
    {
        if (selected is null)
        {
            Sessions.Show(null, []);
            return;
        }

        List<SessionEntry> rows = [Row(selected, "Main", null)];
        rows.AddRange(sideSessions.Where(session => session.Side!.ParentId == selected.Id).Select(session => Row(session, session.Side!.Name, session.Side)));
        Sessions.Show(selected, rows);
    }

    private ResourceBadge Badge(Guid workspace)
    {
        if (!badges.TryGetValue(workspace, out ResourceBadge? badge))
        {
            badge = new ResourceBadge { Text = Resources.Brief(workspace, AnySession(workspace, busy)) };
            badges[workspace] = badge;
        }

        return badge;
    }

    private void RefreshBadges()
    {
        foreach ((Guid workspace, ResourceBadge badge) in badges)
        {
            badge.Text = Resources.Brief(workspace, AnySession(workspace, busy));
        }
    }

    private SessionEntry Row(WorkspaceInfo session, string name, SideSession? side) => new()
    {
        Id = session.Id,
        Name = name,
        Side = side,
        IsShown = (active?.Workspace.Id ?? selected!.Id) == session.Id,
        IsWorking = busy.Contains(session.Id),
        Status = side?.State switch
        {
            SideSessionState.Creating => "Setting Up",
            SideSessionState.Integrating => "Integrating",
            SideSessionState.Deleting => "Deleting",
            _ => busy.Contains(session.Id) ? "Working" : waiting.Contains(session.Id) ? "Queued" : looping.Contains(session.Id) ? "Loop" : "Ready"
        }
    };

    // A workspace counts as working, looping or queued when any of its sessions does.
    private bool AnySession(Guid workspace, HashSet<Guid> ids) => ids.Contains(workspace) || sideSessions.Any(session => session.Side!.ParentId == workspace && ids.Contains(session.Id));

    /// <summary>
    /// Loads the workspaces and groups and focuses a workspace.
    /// </summary>
    /// <param name="focus">The workspace to focus; the first workspace when <see langword="null" /> or not found, unless <paramref name="focusFirst" /> is <see langword="false" />.</param>
    /// <param name="focusFirst">Whether the first workspace is focused when <paramref name="focus" /> names none; otherwise nothing is focused.</param>
    /// <returns>A task that completes once the workspaces are shown.</returns>
    public async Task Start(Guid? focus = null, bool focusFirst = true)
    {
        WorkspaceCatalog? catalog = null;

        if (await Attempt(async () => catalog = await List()))
        {
            Fill(catalog!);

            foreach (Guid stale in Stale())
            {
                if (sideSessions.All(session => session.Id != stale) && workspaces.All(workspace => workspace.Id != stale))
                {
                    Disarm(stale);
                }

                Close(stale);
            }

            FollowTasks();
            BuildEntries();
            EditMode = EditMode.None;
            Selected = null;

            if (focus is { } wanted && sideSessions.FirstOrDefault(session => session.Id == wanted) is { } side)
            {
                shownSessions[side.Side!.ParentId] = side.Id;
            }

            Selected = workspaces.FirstOrDefault(workspace => focus is { } id && workspace.Id == MainOf(id)) ?? (focusFirst ? Entries.FirstOrDefault(entry => entry.IsWorkspace)?.Workspace : null);
            IsDrawerOpen = Selected is null;
            await RefreshSwarm();
        }
    }

    /// <summary>
    /// Notifies the user when each of these tasks, delegated by hand from this client, ends.
    /// </summary>
    /// <param name="tasks">The tasks.</param>
    public void Follow(IReadOnlyList<Guid> tasks)
    {
        followed.UnionWith(tasks);
        _ = RefreshSwarm();
    }

    // The AI is told about a job whose remaining tasks wait on a lost one, so nothing of it is under way any more.
    private bool IsUnderWay(SwarmTaskInfo task) => task.IsActive && !(task.State == SwarmTaskState.Waiting && swarmTasks.Any(other => other.JobId == task.JobId && other.State == SwarmTaskState.Lost));

    /// <summary>
    /// Stops following the server and lets go of everything the screen opened, once it is replaced for good.
    /// </summary>
    public void Dispose()
    {
        connection.Lost -= Lose;
        connection.Reconnected -= Return;
        connection.EventReceived -= Receive;

        foreach ((Guid id, WorkspaceViewModel view) in opened)
        {
            keeper?.Release(id);
            view.Dispose();
        }

        opened.Clear();
        held.Clear();
        followed.Clear();
        Usage.Dispose();
        Resources.Dispose();
    }

    private void Lose() => IsOffline = true;

    private void Return()
    {
        IsOffline = false;
        _ = Refresh();
        _ = RefreshSwarm();
    }

    private void Receive(RemoteEvent remoteEvent)
    {
        if (remoteEvent.Kind == RemoteEventKind.WorkspacesChanged)
        {
            _ = Refresh();
        }
        else if (remoteEvent.Kind == RemoteEventKind.StateChanged && remoteEvent.State is { } state)
        {
            bool working = state != WorkspaceState.Ready;
            busyHeard[remoteEvent.WorkspaceId] = working;

            if (working ? busy.Add(remoteEvent.WorkspaceId) : busy.Remove(remoteEvent.WorkspaceId))
            {
                BuildEntries();
            }
        }
        else if (remoteEvent.Kind == RemoteEventKind.LoopChanged && (remoteEvent.Loop is not null ? looping.Add(remoteEvent.WorkspaceId) : looping.Remove(remoteEvent.WorkspaceId)))
        {
            BuildEntries();
        }
        else if (remoteEvent.Kind == RemoteEventKind.SwarmChanged)
        {
            _ = RefreshSwarm();
        }
        else if (remoteEvent.Kind == RemoteEventKind.QueueChanged && (remoteEvent.Queue is not null ? waiting.Add(remoteEvent.WorkspaceId) : waiting.Remove(remoteEvent.WorkspaceId)))
        {
            BuildEntries();
        }
    }

    private async Task RefreshSwarm()
    {
        if (refreshingSwarm)
        {
            refreshSwarmAgain = true;
            return;
        }

        refreshingSwarm = true;

        try
        {
            do
            {
                refreshSwarmAgain = false;
                SwarmStatus? status;

                try
                {
                    status = await connection.GetSwarm(CancellationToken.None);
                }
                catch (Exception exception) when (exception is IOException or SocketException or RemoteException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
                {
                    return;
                }

                if (status is null)
                {
                    return;
                }

                foreach (SwarmTaskInfo task in status.Tasks.Where(task => !task.IsActive && followed.Contains(task.Id)))
                {
                    followed.Remove(task.Id);
                    notifier?.Announce(task.WorkspaceId ?? Guid.Empty, $"Delegated task \"{task.Title}\": {task.State}");
                }

                swarmTasks.Clear();
                swarmTasks.AddRange(status.Tasks.Where(task => !task.IsIncoming));
                RaiseNotify();

                // Tasks that were all discarded are never reported to the AI, so nothing more would end the wait.
                foreach (Guid id in held.Where(id => !swarmTasks.Any(task => task.WorkspaceId == id && !task.IsManual)).ToList())
                {
                    held.Remove(id);
                    keeper?.Release(id);

                    if (opened.TryGetValue(id, out WorkspaceViewModel? view))
                    {
                        view.NotifyWhenReady = false;
                    }
                }

                BuildEntries();
            }
            while (refreshSwarmAgain);
        }
        finally
        {
            refreshingSwarm = false;
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
                WorkspaceCatalog catalog;

                try
                {
                    catalog = await List();
                }
                catch (Exception exception) when (exception is IOException or SocketException or RemoteException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
                {
                    return;
                }

                Apply(catalog);
            }
            while (refreshAgain);
        }
        finally
        {
            refreshing = false;
        }
    }

    private void Apply(WorkspaceCatalog catalog)
    {
        WorkspaceInfo? before = selected;
        Fill(catalog);
        bool reopen = false;

        // Announced before the session's work is ended, which would otherwise be told as ready.
        foreach (Guid removing in sideSessions.Where(session => session.Side!.State == SideSessionState.Deleting).Select(session => session.Id).ToList())
        {
            Disarm(removing);
        }

        foreach (Guid stale in Stale())
        {
            reopen |= stale == before?.Id;

            if (sideSessions.All(session => session.Id != stale) && workspaces.All(workspace => workspace.Id != stale))
            {
                Disarm(stale);
            }

            // The view on screen is replaced below rather than left behind disposed.
            if (stale == active?.Workspace.Id && stale != before?.Id)
            {
                Active = null;
            }

            Close(stale);
        }

        BuildEntries();

        if (before is null)
        {
            return;
        }

        if (workspaces.FirstOrDefault(workspace => workspace.Id == before.Id) is not { } current)
        {
            Selected = null;
            IsDrawerOpen = true;
        }
        else if (reopen)
        {
            Selected = null;
            Selected = current;
        }
        else if (current != before)
        {
            selected = current;
            SyncSelectedEntry();
        }

        FollowTasks();

        if (selected is not null)
        {
            // A session that went, or that is being integrated, gives way to the main one.
            ForgetUnshowable(selected);
            Swap();
            RaiseSession();
            ShowAwaited();
            RefreshSessions();
        }
    }

    private List<Guid> Stale() => [.. opened.Where(pair => !workspaces.Concat(sideSessions).Any(workspace => Same(workspace, pair.Value.Workspace))).Select(pair => pair.Key)];

    private void Close(Guid id)
    {
        opened[id].Dispose();
        opened.Remove(id);
    }

    // A view reads one folder through one provider, so a workspace that changed either needs a new one.
    private bool Same(WorkspaceInfo now, WorkspaceInfo shown) => now.Id == shown.Id && now.Folder == shown.Folder && now.Service == shown.Service;

    private void Fill(WorkspaceCatalog catalog)
    {
        workspaces.Clear();
        workspaces.AddRange(catalog.Workspaces);
        Dictionary<Guid, string> named = sideSessions.ToDictionary(session => session.Id, session => session.Side!.Name);
        sideSessions.Clear();
        sideSessions.AddRange(catalog.Sessions.Where(session => session.Side is not null));
        // History shows the branches of sessions by their names, which a rename changes without git changing.
        HashSet<Guid> renamed = [.. sideSessions.Where(session => named.TryGetValue(session.Id, out string? name) && name != session.Side!.Name).Select(session => session.Side!.ParentId)];

        foreach ((Guid id, WorkspaceViewModel view) in opened)
        {
            if (renamed.Contains(MainOf(id)))
            {
                _ = view.Git.History.Changed();
            }
        }
        groups.Clear();
        groups.AddRange(catalog.Groups);
        expanded.IntersectWith(groups.Select(group => group.Id));
        busy.Clear();
        busy.UnionWith(catalog.Busy);

        foreach ((Guid id, bool working) in busyHeard)
        {
            _ = working ? busy.Add(id) : busy.Remove(id);
        }

        looping.Clear();
        looping.UnionWith(catalog.Looping);
        waiting.Clear();
        waiting.UnionWith(catalog.Queued);
        delegations.Clear();

        foreach (WorkspaceDelegation delegation in catalog.Delegations)
        {
            delegations[delegation.WorkspaceId] = delegation;
        }

        foreach ((Guid id, WorkspaceViewModel view) in opened)
        {
            view.Delegation = delegations.GetValueOrDefault(id);

            if (sideSessions.FirstOrDefault(session => session.Id == id) is { } side)
            {
                view.ShowSide(side.Side!);
            }
        }
    }

    // States change often, so one heard while the list was on its way may be newer than what the list says.
    private Task<WorkspaceCatalog> List()
    {
        busyHeard.Clear();
        return connection.ListWorkspaces(CancellationToken.None);
    }

    private WorkspaceViewModel Open(WorkspaceInfo workspace)
    {
        if (opened.TryGetValue(workspace.Id, out WorkspaceViewModel? existing))
        {
            return existing;
        }

        WorkspaceViewModel created = new(connection, workspace, layout, drafts, recorder, preferences)
        {
            Delegation = delegations.GetValueOrDefault(workspace.Id)
        };
        created.Chat.PromptStarted += () => keeper?.Hold(workspace.Id, NameOf(workspace));
        created.Chat.PromptFailed += () =>
        {
            if (held.Contains(workspace.Id))
            {
                created.NotifyWhenReady = true;
            }
            else if (!created.NotifyWhenReady)
            {
                keeper?.Release(workspace.Id);
            }
        };
        WorkspaceState previous = created.State;
        DateTimeOffset busySince = time.GetUtcNow();
        created.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(WorkspaceViewModel.State) || created.State == previous)
            {
                return;
            }

            if (previous == WorkspaceState.Ready)
            {
                busySince = time.GetUtcNow();
                notifier?.Clear(workspace.Id);
            }
            else if (created.State == WorkspaceState.Ready)
            {
                if (created.NotifyWhenReady)
                {
                    Conclude(created, workspace);
                }

                if (!created.IsLooping && !created.Chat.HasActiveQueued && time.GetUtcNow() - busySince >= minimumBusy)
                {
                    chime?.Play();
                }
            }

            previous = created.State;
        };
        // A loop that ends between its runs changes no state, though the work waited for is over with it.
        created.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(WorkspaceViewModel.IsLooping) && !created.IsLooping && created.State == WorkspaceState.Ready && !created.Chat.IsQueued && created.NotifyWhenReady)
            {
                Conclude(created, workspace);
            }
        };
        created.PropertyChanged += (_, args) =>
        {
            if (created == active && args.PropertyName is nameof(WorkspaceViewModel.State) or nameof(WorkspaceViewModel.IsLooping) or nameof(WorkspaceViewModel.NotifyWhenReady))
            {
                RaiseNotify();
            }
        };
        bool hadQueued = created.Chat.HasActiveQueued;
        created.Chat.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatViewModel.HasActiveQueued))
            {
                // The queued inputs can run out without the state changing, as when they are deleted or paused while the workspace is ready.
                bool ranOut = hadQueued && !created.Chat.HasActiveQueued;
                hadQueued = created.Chat.HasActiveQueued;

                if (ranOut && created.State == WorkspaceState.Ready && !created.IsLooping && created.NotifyWhenReady)
                {
                    Conclude(created, workspace);
                }
            }

            if (created == active && args.PropertyName is nameof(ChatViewModel.IsQueued) or nameof(ChatViewModel.HasActiveQueued))
            {
                RaiseNotify();
            }
        };
        created.PropertyChanged += (_, args) =>
        {
            if (created == active && args.PropertyName == nameof(WorkspaceViewModel.IsAiWorking))
            {
                Raise(nameof(IsAiWorking));
            }
            else if (created == active && args.PropertyName == nameof(WorkspaceViewModel.IsWorking))
            {
                Raise(nameof(IsWorking));
                BeginRenameCommand.Refresh();
                MoveWorkspaceToFolderCommand.Refresh();
                BeginDeleteCommand.Refresh();
            }

            if (created == active && args.PropertyName is nameof(WorkspaceViewModel.ShowSettings))
            {
                Resources.IsSectionShown = created.ShowSettings;
            }

            if (created == active && args.PropertyName is nameof(WorkspaceViewModel.State))
            {
                BeginStopCommand.Refresh();

                if (editMode == EditMode.StopWork && !created.CanHalt)
                {
                    EditMode = EditMode.None;
                }
            }
        };
        opened[workspace.Id] = created;
        _ = created.Start();
        return created;
    }

    // Sessions and workspaces can be renamed while their view stays.
    private string NameOf(WorkspaceInfo workspace) => workspaces.Concat(sideSessions).FirstOrDefault(known => known.Id == workspace.Id)?.Name ?? workspace.Name;

    private bool HasTasksUnderWay(Guid id) => swarmTasks.Any(task => task.WorkspaceId == id && !task.IsManual && IsUnderWay(task));

    // Tells the user that the work they wait for is over, unless more of it is still to come.
    private void Conclude(WorkspaceViewModel view, WorkspaceInfo workspace)
    {
        // While the AI waits for delegated tasks its work is not over, so the app stays kept alive and the user is told once it has dealt with them.
        if (HasTasksUnderWay(workspace.Id))
        {
            held.Add(workspace.Id);
            return;
        }

        held.Remove(workspace.Id);

        // Nor is it while queued inputs are still to be sent or work waits for the local AI (a queued input leaves the list only after it starts to wait there), or while a loop runs, between whose runs the workspace is ready.
        if (view.Chat.HasActiveQueued || view.Chat.IsQueued || view.IsLooping)
        {
            return;
        }

        keeper?.Release(workspace.Id);
        view.NotifyWhenReady = false;
        notifier?.Notify(workspace.Id, NameOf(workspace));
    }

    private void ToggleNotify()
    {
        if (active is not { } view)
        {
            return;
        }

        Guid id = view.Workspace.Id;

        if (view.NotifyWhenReady)
        {
            view.NotifyWhenReady = false;
            held.Remove(id);
            keeper?.Release(id);
            return;
        }

        keeper?.Hold(id, NameOf(view.Workspace));
        view.NotifyWhenReady = true;

        // One that is ready while tasks it delegated are under way waits for them as if it had been waited for all along.
        if (view.State == WorkspaceState.Ready && HasTasksUnderWay(id))
        {
            held.Add(id);
        }
    }

    private void RaiseNotify()
    {
        Raise(nameof(CanNotify));
        Raise(nameof(IsNotifying));
        Raise(nameof(NotifyTip));
    }

    private Task Begin(EditMode mode, string text, WorkspaceGroup? group = null)
    {
        editingGroup = group;
        editingWorkspace = null;
        RenameText = text;
        Error = null;
        Raise(nameof(EditingGroupName));
        EditMode = mode;
        return Task.CompletedTask;
    }

    private bool ExpandAround(WorkspaceInfo workspace)
    {
        bool changed = false;

        for (Guid? group = Parent(workspace.GroupId); group is { } id; group = Parent(groups.First(item => item.Id == id).ParentId))
        {
            changed |= expanded.Add(id);
        }

        return changed;
    }

    private Task BeginNewGroup()
    {
        NewGroupParent.Fill(groups);
        return Begin(EditMode.NewGroup, string.Empty);
    }

    private Task BeginMove(EditMode mode, WorkspaceInfo? workspace, WorkspaceGroup? group)
    {
        if (workspace is null && group is null)
        {
            return Task.CompletedTask;
        }

        HashSet<Guid> blocked = group is null ? [] : [group.Id, .. Descendants(group.Id)];
        Destinations.Clear();
        Destinations.Add(new GroupChoice(null, "Top level"));
        AddDestinations(null, string.Empty, blocked);
        Begin(mode, string.Empty, group);
        editingWorkspace = workspace;
        Raise(nameof(MovingName));
        Guid? current = workspace is null ? group!.ParentId : workspace.GroupId;
        Destination = Destinations.FirstOrDefault(choice => choice.Id == current) ?? Destinations[0];
        return Task.CompletedTask;
    }

    private IEnumerable<Guid> Descendants(Guid id)
    {
        foreach (WorkspaceGroup child in groups.Where(group => group.ParentId == id))
        {
            yield return child.Id;

            foreach (Guid descendant in Descendants(child.Id))
            {
                yield return descendant;
            }
        }
    }

    private void AddDestinations(Guid? parent, string path, HashSet<Guid> blocked)
    {
        foreach (WorkspaceGroup group in Children(parent).Where(group => !blocked.Contains(group.Id)))
        {
            string name = path.Length == 0 ? group.Name : $"{path} / {group.Name}";
            Destinations.Add(new GroupChoice(group.Id, name));
            AddDestinations(group.Id, name, blocked);
        }
    }

    private IEnumerable<WorkspaceGroup> Children(Guid? parent) => groups.Where(group => Parent(group.ParentId) == parent);

    private Guid? Parent(Guid? id) => id is { } known && groups.Any(group => group.Id == known) ? known : null;

    private void BuildEntries()
    {
        List<DrawerEntry> wanted = [];
        AddEntries(null, 0, false, wanted);
        building = true;

        for (int index = Entries.Count - 1; index >= 0; index--)
        {
            if (!wanted.Contains(Entries[index]))
            {
                Entries.RemoveAt(index);
            }
        }

        // A list drops its selection when the selected row is moved, so that row stays and the others move around it.
        if (selectedEntry is not null && wanted.IndexOf(selectedEntry) is int pinned and >= 0)
        {
            for (int index = Entries.IndexOf(selectedEntry) - 1; index >= 0; index--)
            {
                if (wanted.IndexOf(Entries[index]) > pinned)
                {
                    Entries.Move(index, Entries.Count - 1);
                }
            }
        }

        for (int index = 0; index < wanted.Count; index++)
        {
            if (index < Entries.Count && Entries[index] == wanted[index])
            {
                continue;
            }

            int found = -1;

            for (int later = index + 1; later < Entries.Count && found < 0; later++)
            {
                if (Entries[later] == wanted[index])
                {
                    found = later;
                }
            }

            if (found >= 0)
            {
                Entries.Move(found, index);
            }
            else
            {
                Entries.Insert(index, wanted[index]);
            }
        }

        building = false;
        RefreshBadges();
        Raise(nameof(HasNoMatches));
        SyncSelectedEntry();
        RefreshSessions();
    }

    private bool Matches(WorkspaceInfo workspace) => workspace.Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) || workspace.Folder.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);

    private bool Matches(WorkspaceGroup group) => group.Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);

    private bool HoldsMatch(Guid group) => workspaces.Any(workspace => Parent(workspace.GroupId) == group && Matches(workspace)) || Children(group).Any(child => Matches(child) || HoldsMatch(child.Id));

    private bool HoldsBusy(Guid group) => workspaces.Any(workspace => Parent(workspace.GroupId) == group && AnySession(workspace.Id, busy)) || Children(group).Any(child => HoldsBusy(child.Id));

    private void AddEntries(Guid? parent, int depth, bool showAll, List<DrawerEntry> into)
    {
        foreach (WorkspaceGroup group in Children(parent))
        {
            bool shown = !IsFiltering || showAll || Matches(group);

            if (!shown && !HoldsMatch(group.Id))
            {
                continue;
            }

            bool isCollapsed = !IsFiltering && !expanded.Contains(group.Id);
            into.Add(new DrawerEntry { Group = group, IsCollapsed = isCollapsed, Depth = depth, IsWorking = HoldsBusy(group.Id) });

            if (!isCollapsed)
            {
                AddEntries(group.Id, depth + 1, shown && IsFiltering, into);
            }
        }

        foreach (WorkspaceInfo workspace in workspaces.Where(workspace => Parent(workspace.GroupId) == parent && (!IsFiltering || showAll || Matches(workspace))).OrderByDescending(workspace => AnySession(workspace.Id, busy)).ThenByDescending(workspace => AnySession(workspace.Id, looping)).ThenByDescending(workspace => workspace.LastUpdated ?? DateTimeOffset.MinValue).ThenBy(workspace => workspace.Name, StringComparer.OrdinalIgnoreCase))
        {
            into.Add(new DrawerEntry { Workspace = workspace, Depth = depth, IsWorking = AnySession(workspace.Id, busy), IsLooping = AnySession(workspace.Id, looping), IsQueued = AnySession(workspace.Id, waiting), Resources = Badge(workspace.Id), DelegatedBy = delegations.GetValueOrDefault(workspace.Id)?.From });

            foreach (SwarmTaskInfo task in swarmTasks.Where(task => task.WorkspaceId is { } owner && MainOf(owner) == workspace.Id && (task.IsActive || task.State == SwarmTaskState.Lost)).OrderBy(task => task.CreatedAt))
            {
                into.Add(new DrawerEntry { DelegatedTask = task, Depth = depth + 1 });
            }
        }
    }

    private void Tap(DrawerEntry entry)
    {
        if (entry.Group is { } group)
        {
            if (!IsFiltering)
            {
                if (!expanded.Remove(group.Id))
                {
                    expanded.Add(group.Id);
                }

                BuildEntries();
            }

            ShowFocusedAsSelected();
        }
        else if (entry.DelegatedTask is { } task)
        {
            // The task opens on the swarm screen, and the list keeps the focused workspace selected.
            ShowFocusedAsSelected();
            SwarmRequested?.Invoke(task.Id, task.WorkspaceId is { } owner ? MainOf(owner) : null);
        }
        else if (entry.Workspace is { } workspace)
        {
            Selected = workspace;
        }
    }

    // The list took the tapped row as selected. Unless it goes back to the focused workspace, tapping that row again would change nothing.
    private void ShowFocusedAsSelected()
    {
        building = true;
        DrawerEntry? focused = selectedEntry;
        selectedEntry = null;
        Raise(nameof(SelectedEntry));
        selectedEntry = focused;
        Raise(nameof(SelectedEntry));
        building = false;
    }

    private void SyncSelectedEntry()
    {
        DrawerEntry? wanted = Entries.FirstOrDefault(entry => entry.Workspace is { } workspace && workspace.Id == selected?.Id);

        if (ReferenceEquals(wanted, selectedEntry))
        {
            return;
        }

        building = true;
        selectedEntry = wanted;
        Raise(nameof(SelectedEntry));
        building = false;
    }

    private async Task Reload()
    {
        bool open = isDrawerOpen;
        await Start(selected?.Id);
        IsDrawerOpen = open || selected is null;
    }

    private async Task Rename()
    {
        if (selected is null)
        {
            return;
        }

        WorkspaceInfo? renamed = null;

        if (string.IsNullOrWhiteSpace(renameText))
        {
            Error = "Enter a name.";
        }
        else if (await Attempt(async () => renamed = await connection.RenameWorkspace(selected.Id, renameText, CancellationToken.None)) && renamed is not null)
        {
            await Start(renamed.Id);
        }
    }

    private Task OpenNewWindow()
    {
        try
        {
            newWindow?.Open();
        }
        catch (InvalidOperationException exception)
        {
            Error = exception.Message;
        }

        return Task.CompletedTask;
    }

    private async Task Delete()
    {
        if (selected is null)
        {
            return;
        }

        if (await Attempt(() => connection.DeleteWorkspace(selected.Id, deleteFolder, CancellationToken.None)))
        {
            await Start();
        }
    }

    private async Task CreateGroup()
    {
        if (string.IsNullOrWhiteSpace(renameText))
        {
            Error = "Enter a name.";
        }
        else if (await Attempt(() => connection.CreateGroup(renameText, NewGroupParent.GroupId, CancellationToken.None)))
        {
            if (NewGroupParent.GroupId is { } parent)
            {
                expanded.Add(parent);
            }

            await Reload();
        }
    }

    private async Task RenameGroup()
    {
        if (editingGroup is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(renameText))
        {
            Error = "Enter a name.";
        }
        else if (await Attempt(() => connection.RenameGroup(editingGroup.Id, renameText, CancellationToken.None)))
        {
            await Reload();
        }
    }

    private async Task Move()
    {
        if (destination is null)
        {
            return;
        }

        if (editingWorkspace is not null)
        {
            if (await Attempt(() => connection.MoveWorkspace(editingWorkspace.Id, destination.Id, CancellationToken.None)))
            {
                await Reload();
            }
        }
        else if (editingGroup is not null && await Attempt(() => connection.MoveGroup(editingGroup.Id, destination.Id, CancellationToken.None)))
        {
            await Reload();
        }
    }

    private async Task Shift(WorkspaceGroup? group, int offset)
    {
        WorkspaceCatalog? catalog = null;

        if (group is null)
        {
            return;
        }

        if (await Attempt(async () =>
        {
            await connection.ShiftGroup(group.Id, offset, CancellationToken.None);
            catalog = await List();
        }))
        {
            Fill(catalog!);
            BuildEntries();
        }
    }

    private async Task Halt()
    {
        EditMode = EditMode.None;

        if (active is { } workspace)
        {
            await workspace.Stop();
        }
    }

    private async Task DeleteGroup()
    {
        if (editingGroup is not null && await Attempt(() => connection.DeleteGroup(editingGroup.Id, CancellationToken.None)))
        {
            await Reload();
        }
    }
}
