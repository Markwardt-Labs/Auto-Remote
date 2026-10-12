namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The main screen: the workspace list organized as a tree of groups, the focused workspace, and the dialogs that rename, delete or create things.
/// </summary>
internal sealed class HomeViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HomeViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    /// <param name="layout">How a focused workspace lays out its conversation and files.</param>
    public HomeViewModel(IServerConnection connection, ClientLayout layout = ClientLayout.Tabs)
    {
        this.connection = connection;
        this.layout = layout;
        Usage = new UsageViewModel(connection);
        ToggleUsageCommand = new AsyncCommand(_ =>
        {
            IsUsageOpen = !isUsageOpen;
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
        ChangeServerCommand = new AsyncCommand(_ =>
        {
            ChangeServerRequested?.Invoke();
            return Task.CompletedTask;
        });
        BeginRenameCommand = new AsyncCommand(_ => Begin(EditMode.RenameWorkspace, selected?.Name ?? string.Empty), () => selected is not null);
        ConfirmRenameCommand = new AsyncCommand(_ => Rename());
        BeginDeleteCommand = new AsyncCommand(
            _ =>
            {
                DeleteFolder = true;
                return Begin(EditMode.DeleteWorkspace, string.Empty);
            },
            () => selected is not null);
        ConfirmDeleteCommand = new AsyncCommand(_ => Delete());
        BeginNewGroupCommand = new AsyncCommand(parent => Begin(EditMode.NewGroup, string.Empty, parent as WorkspaceGroup));
        ConfirmNewGroupCommand = new AsyncCommand(_ => CreateGroup());
        BeginRenameGroupCommand = new AsyncCommand(group => Begin(EditMode.RenameGroup, (group as WorkspaceGroup)?.Name ?? string.Empty, group as WorkspaceGroup));
        ConfirmRenameGroupCommand = new AsyncCommand(_ => RenameGroup());
        BeginDeleteGroupCommand = new AsyncCommand(group => Begin(EditMode.DeleteGroup, string.Empty, group as WorkspaceGroup));
        ConfirmDeleteGroupCommand = new AsyncCommand(_ => DeleteGroup());
        BeginMoveWorkspaceCommand = new AsyncCommand(workspace => BeginMove(EditMode.MoveWorkspace, workspace as WorkspaceInfo, null));
        BeginMoveGroupCommand = new AsyncCommand(group => BeginMove(EditMode.MoveGroup, null, group as WorkspaceGroup));
        ConfirmMoveCommand = new AsyncCommand(_ => Move());
        CancelEditCommand = new AsyncCommand(_ =>
        {
            EditMode = EditMode.None;
            Error = null;
            return Task.CompletedTask;
        });
    }

    private readonly IServerConnection connection;

    private readonly ClientLayout layout;

    private bool isUsageOpen;

    private readonly List<WorkspaceInfo> workspaces = [];

    private readonly List<WorkspaceGroup> groups = [];

    private readonly HashSet<Guid> collapsed = [];

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

    private string renameText = string.Empty;

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
    /// Raised when the user asks to connect to a different server.
    /// </summary>
    public event Action? ChangeServerRequested;

    /// <summary>
    /// Gets the drawer rows: the group tree flattened in display order: each group, then what is inside it, then the workspaces in no group.
    /// </summary>
    public ObservableCollection<DrawerEntry> Entries { get; } = [];

    /// <summary>
    /// Gets or sets the tapped drawer row; tapping a workspace focuses it and tapping a group expands or collapses it.
    /// </summary>
    public DrawerEntry? SelectedEntry
    {
        get => selectedEntry;
        set
        {
            if (building || value is null)
            {
                return;
            }

            if (value.Group is { } group)
            {
                if (!collapsed.Remove(group.Id))
                {
                    collapsed.Add(group.Id);
                }

                BuildEntries();
            }
            else if (value.Workspace is { } workspace)
            {
                Selected = workspace;
            }
        }
    }

    /// <summary>
    /// Gets the usage limits of the AI service of the focused workspace.
    /// </summary>
    public UsageViewModel Usage { get; }

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
            Active = value is null ? null : Open(value);
            _ = Active?.Focus();
            _ = Usage.Show(value?.Service);

            Raise(nameof(HasSelection));
            Raise(nameof(Title));
            SyncSelectedEntry();
            BeginRenameCommand.Refresh();
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

                Raise(nameof(IsAiWorking));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the AI is working in the focused workspace, which turns the header blue.
    /// </summary>
    public bool IsAiWorking => active?.IsAiWorking ?? false;

    /// <summary>
    /// Gets a value indicating whether a workspace is focused.
    /// </summary>
    public bool HasSelection => selected is not null;

    /// <summary>
    /// Gets the header text: the focused workspace's name.
    /// </summary>
    public string Title => selected?.Name ?? "Auto Remote";

    /// <summary>
    /// Gets or sets a value indicating whether the workspace list drawer is open.
    /// </summary>
    public bool IsDrawerOpen
    {
        get => isDrawerOpen;
        set => Set(ref isDrawerOpen, value);
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
    /// Gets the explanation shown in the new group dialog: where the group will be created.
    /// </summary>
    public string NewGroupHint => (editingGroup is { } parent ? $"It will be created inside {parent.Name}. " : string.Empty) + "Groups only organize workspaces in Auto Remote. They are not created on the server's disk.";

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
    /// Gets the command that asks to change server.
    /// </summary>
    public AsyncCommand ChangeServerCommand { get; }

    /// <summary>
    /// Gets the command that opens the workspace rename dialog.
    /// </summary>
    public AsyncCommand BeginRenameCommand { get; }

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
    /// Gets the command that opens the new group dialog, inside the group passed as its parameter if any.
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
    /// Gets the command that closes the showing dialog.
    /// </summary>
    public AsyncCommand CancelEditCommand { get; }

    /// <summary>
    /// Loads the workspaces and groups and focuses a workspace.
    /// </summary>
    /// <param name="focus">The workspace to focus; the first workspace when <see langword="null" /> or not found.</param>
    /// <returns>A task that completes once the workspaces are shown.</returns>
    public async Task Start(Guid? focus = null)
    {
        WorkspaceCatalog? catalog = null;

        if (await Attempt(async () => catalog = await connection.ListWorkspaces(CancellationToken.None)))
        {
            workspaces.Clear();
            workspaces.AddRange(catalog!.Workspaces);
            groups.Clear();
            groups.AddRange(catalog.Groups);
            collapsed.IntersectWith(groups.Select(group => group.Id));

            foreach (Guid stale in opened.Keys.Where(key => workspaces.All(workspace => workspace.Id != key)).ToList())
            {
                opened[stale].Dispose();
                opened.Remove(stale);
            }

            BuildEntries();
            EditMode = EditMode.None;
            Selected = null;
            Selected = workspaces.FirstOrDefault(workspace => workspace.Id == focus) ?? Entries.FirstOrDefault(entry => entry.IsWorkspace)?.Workspace;
            IsDrawerOpen = Selected is null;
        }
    }

    private WorkspaceViewModel Open(WorkspaceInfo workspace)
    {
        if (opened.TryGetValue(workspace.Id, out WorkspaceViewModel? existing))
        {
            return existing;
        }

        WorkspaceViewModel created = new(connection, workspace, layout);
        created.Settings.Changed += Replace;
        created.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(WorkspaceViewModel.IsAiWorking) && created == active)
            {
                Raise(nameof(IsAiWorking));
            }
        };
        opened[workspace.Id] = created;
        _ = created.Start();
        return created;
    }

    private Task Begin(EditMode mode, string text, WorkspaceGroup? group = null)
    {
        editingGroup = group;
        editingWorkspace = null;
        RenameText = text;
        Error = null;
        Raise(nameof(EditingGroupName));
        Raise(nameof(NewGroupHint));
        EditMode = mode;
        return Task.CompletedTask;
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

    private void Replace(WorkspaceInfo updated)
    {
        workspaces[workspaces.FindIndex(workspace => workspace.Id == updated.Id)] = updated;

        if (selected?.Id == updated.Id)
        {
            AiService? before = selected.Service;
            selected = updated;

            if (before != updated.Service)
            {
                _ = Usage.Show(updated.Service);
            }
        }

        BuildEntries();
    }

    private void BuildEntries()
    {
        building = true;
        Entries.Clear();
        AddEntries(null, 0);
        building = false;
        SyncSelectedEntry();
    }

    private void AddEntries(Guid? parent, int depth)
    {
        foreach (WorkspaceGroup group in Children(parent))
        {
            bool isCollapsed = collapsed.Contains(group.Id);
            Entries.Add(new DrawerEntry { Group = group, IsCollapsed = isCollapsed, Depth = depth });

            if (!isCollapsed)
            {
                AddEntries(group.Id, depth + 1);
            }
        }

        foreach (WorkspaceInfo workspace in workspaces.Where(workspace => Parent(workspace.GroupId) == parent))
        {
            Entries.Add(new DrawerEntry { Workspace = workspace, Depth = depth });
        }
    }

    private void SyncSelectedEntry()
    {
        building = true;
        selectedEntry = Entries.FirstOrDefault(entry => entry.Workspace is { } workspace && workspace.Id == selected?.Id);
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
        else if (await Attempt(() => connection.CreateGroup(renameText, editingGroup?.Id, CancellationToken.None)))
        {
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

    private async Task DeleteGroup()
    {
        if (editingGroup is not null && await Attempt(() => connection.DeleteGroup(editingGroup.Id, CancellationToken.None)))
        {
            await Reload();
        }
    }
}
