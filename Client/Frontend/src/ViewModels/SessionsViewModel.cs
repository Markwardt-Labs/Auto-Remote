namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The sessions of the workspace on screen: the list that opens from its title, and what the user is asked before one is made, renamed, integrated or deleted.
/// </summary>
internal sealed class SessionsViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SessionsViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server.</param>
    /// <param name="isAiWorking">Tells whether the AI of a workspace works right now.</param>
    /// <param name="prepare">Sends what was edited of a workspace's settings before a session is made of it, or <see langword="null" /> when nothing is to be sent.</param>
    public SessionsViewModel(IServerConnection connection, Func<Guid, bool> isAiWorking, Func<Guid, Task<bool>>? prepare = null)
    {
        this.connection = connection;
        this.isAiWorking = isAiWorking;
        this.prepare = prepare;
        ToggleCommand = new AsyncCommand(_ =>
        {
            IsOpen = !isOpen;
            return Task.CompletedTask;
        });
        CloseCommand = new AsyncCommand(_ =>
        {
            IsOpen = false;
            return Task.CompletedTask;
        });
        ChooseCommand = new AsyncCommand(entry =>
        {
            if (entry is SessionEntry { CanOpen: true } chosen)
            {
                IsOpen = false;
                Chosen?.Invoke(chosen.Id);
            }

            return Task.CompletedTask;
        });
        ExpandCommand = new AsyncCommand(entry =>
        {
            if (entry is SessionEntry row)
            {
                expanded = expanded == row.Id ? null : row.Id;
                Build();
            }

            return Task.CompletedTask;
        });
        ChangesCommand = new AsyncCommand(entry =>
        {
            if (entry is SessionEntry { CanManage: true } chosen)
            {
                IsOpen = false;
                ChangesRequested?.Invoke(chosen.Id);
            }

            return Task.CompletedTask;
        });
        NewCommand = new AsyncCommand(_ => BeginNew());
        RenameCommand = new AsyncCommand(entry => Begin(SessionDialog.Rename, entry as SessionEntry));
        DeleteCommand = new AsyncCommand(entry => Begin(SessionDialog.Delete, entry as SessionEntry));
        IntegrateCommand = new AsyncCommand(entry => Begin(SessionDialog.Integrate, entry as SessionEntry));
        CancelIntegrationCommand = new AsyncCommand(entry => Begin(SessionDialog.CancelIntegration, entry as SessionEntry));
        WhenFinishedCommand = new AsyncCommand(entry => SwitchWhenFinished(entry as SessionEntry));
        // Not held back while it runs: a request that takes long must not keep the next dialog from being confirmed.
        ConfirmCommand = new AsyncCommand(_ => Confirm(), null, false);
        DismissCommand = new AsyncCommand(_ =>
        {
            Dialog = SessionDialog.None;
            Error = null;
            return Task.CompletedTask;
        });
    }

    private readonly IServerConnection connection;

    private readonly Func<Guid, bool> isAiWorking;

    private readonly Func<Guid, Task<bool>>? prepare;

    // Counts the dialogs, so what a slow request ends with is not taken for the one on screen by then.
    private int turn;

    private int? waiting;

    private readonly List<SessionEntry> rows = [];

    private WorkspaceInfo? main;

    private Guid? expanded;

    private bool isOpen;

    private SessionDialog dialog;

    private Guid target;

    private string targetName = string.Empty;

    private string name = string.Empty;

    private string firstTask = string.Empty;

    private bool useCurrentFiles;

    /// <summary>
    /// Occurs when the user picked a session to switch to.
    /// </summary>
    public event Action<Guid>? Chosen;

    /// <summary>
    /// Occurs when the user wants to see what a session changed since it started.
    /// </summary>
    public event Action<Guid>? ChangesRequested;

    /// <summary>
    /// Occurs when a side session was made, with whether it was given a first task.
    /// </summary>
    public event Action<WorkspaceInfo, bool>? Made;

    /// <summary>
    /// Occurs when a request of a dialog that is no longer on screen failed.
    /// </summary>
    public event Action<string>? Failed;

    /// <summary>
    /// Occurs when the user is about to throw a session away.
    /// </summary>
    public event Action<Guid>? Discarding;

    /// <summary>
    /// Gets the rows of the list, the main session first.
    /// </summary>
    public ObservableCollection<SessionEntry> Entries { get; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the list is shown.
    /// </summary>
    public bool IsOpen
    {
        get => isOpen;
        set
        {
            if (Set(ref isOpen, value))
            {
                Error = null;

                if (!value)
                {
                    expanded = null;
                    Build();
                }
            }
        }
    }

    /// <summary>
    /// Gets what the user is asked.
    /// </summary>
    public SessionDialog Dialog
    {
        get => dialog;
        private set
        {
            if (Set(ref dialog, value))
            {
                turn++;
                Raise(nameof(CanConfirm));
                Raise(nameof(IsPrompting));
                Raise(nameof(IsNew));
                Raise(nameof(NeedsName));
                Raise(nameof(IsDestructive));
                Raise(nameof(DialogTitle));
                Raise(nameof(DialogText));
                Raise(nameof(HasDialogText));
                Raise(nameof(ConfirmText));
                Raise(nameof(DismissText));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether a dialog is shown.
    /// </summary>
    public bool IsPrompting => dialog != SessionDialog.None;

    /// <summary>
    /// Gets a value indicating whether the dialog on screen can be confirmed, which it cannot while its own request runs.
    /// </summary>
    public bool CanConfirm => waiting != turn;

    /// <summary>
    /// Gets a value indicating whether a new session is being described.
    /// </summary>
    public bool IsNew => dialog == SessionDialog.New;

    /// <summary>
    /// Gets a value indicating whether the dialog asks for a name.
    /// </summary>
    public bool NeedsName => dialog is SessionDialog.New or SessionDialog.Rename;

    /// <summary>
    /// Gets a value indicating whether confirming throws work away.
    /// </summary>
    public bool IsDestructive => dialog == SessionDialog.Delete;

    /// <summary>
    /// Gets the title of the dialog.
    /// </summary>
    public string DialogTitle => dialog switch
    {
        SessionDialog.New => "New session",
        SessionDialog.Rename => "Rename session",
        SessionDialog.Delete => "Delete session?",
        SessionDialog.Integrate => "Integrate session?",
        SessionDialog.IntegrateWhenFinished => "Integrate When Finished?",
        SessionDialog.CancelIntegration => "Cancel Integration?",
        _ => string.Empty
    };

    /// <summary>
    /// Gets what the dialog explains.
    /// </summary>
    public string DialogText => dialog switch
    {
        SessionDialog.Delete => $"\"{targetName}\" and all of its changes will be permanently discarded.",
        SessionDialog.Integrate => $"The main session's AI brings the changes of \"{targetName}\" into the main session, and the session is then permanently removed. This cannot be undone.",
        SessionDialog.IntegrateWhenFinished => $"Once its AI marks its task complete and nothing of it runs any more, \"{targetName}\" is integrated into the main session by itself and permanently removed. This cannot be undone.",
        SessionDialog.CancelIntegration => "Main may already contain some of this session's changes. They will not be undone, and you may need to clean up main yourself. The AI may make a few more edits before it stops.",
        _ => string.Empty
    };

    /// <summary>
    /// Gets a value indicating whether the dialog explains something.
    /// </summary>
    public bool HasDialogText => DialogText.Length > 0;

    /// <summary>
    /// Gets the label of the button that goes ahead.
    /// </summary>
    public string ConfirmText => dialog switch
    {
        SessionDialog.New => "Create Session",
        SessionDialog.Rename => "Rename",
        SessionDialog.Delete => "Delete",
        SessionDialog.Integrate => "Integrate",
        SessionDialog.IntegrateWhenFinished => "Turn On",
        SessionDialog.CancelIntegration => "Cancel Integration",
        _ => string.Empty
    };

    /// <summary>
    /// Gets the label of the button that closes the dialog.
    /// </summary>
    public string DismissText => dialog == SessionDialog.CancelIntegration ? "Keep Integrating" : "Cancel";

    /// <summary>
    /// Gets or sets the name typed for a session.
    /// </summary>
    public string Name
    {
        get => name;
        set => Set(ref name, value);
    }

    /// <summary>
    /// Gets or sets the work a new session starts on once it is set up.
    /// </summary>
    public string FirstTask
    {
        get => firstTask;
        set => Set(ref firstTask, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether a new session starts from the files as they are, uncommitted changes included.
    /// </summary>
    public bool UseCurrentFiles
    {
        get => useCurrentFiles;
        set
        {
            if (Set(ref useCurrentFiles, value))
            {
                Raise(nameof(UseCurrentCommit));
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether a new session starts from the current commit.
    /// </summary>
    public bool UseCurrentCommit
    {
        get => !useCurrentFiles;
        set => UseCurrentFiles = !value;
    }

    /// <summary>
    /// Gets the command that opens and closes the list.
    /// </summary>
    public AsyncCommand ToggleCommand { get; }

    /// <summary>
    /// Gets the command that closes the list.
    /// </summary>
    public AsyncCommand CloseCommand { get; }

    /// <summary>
    /// Gets the command that switches to a row's session.
    /// </summary>
    public AsyncCommand ChooseCommand { get; }

    /// <summary>
    /// Gets the command that shows and hides a row's actions.
    /// </summary>
    public AsyncCommand ExpandCommand { get; }

    /// <summary>
    /// Gets the command that shows what a row's session changed since it started.
    /// </summary>
    public AsyncCommand ChangesCommand { get; }

    /// <summary>
    /// Gets the command that asks for a new session.
    /// </summary>
    public AsyncCommand NewCommand { get; }

    /// <summary>
    /// Gets the command that asks for a row's new name.
    /// </summary>
    public AsyncCommand RenameCommand { get; }

    /// <summary>
    /// Gets the command that asks whether to delete a row's session.
    /// </summary>
    public AsyncCommand DeleteCommand { get; }

    /// <summary>
    /// Gets the command that asks whether to integrate a row's session.
    /// </summary>
    public AsyncCommand IntegrateCommand { get; }

    /// <summary>
    /// Gets the command that asks whether to cancel the integration of a row's session.
    /// </summary>
    public AsyncCommand CancelIntegrationCommand { get; }

    /// <summary>
    /// Gets the command that switches integrating a row's session once it is finished on, after asking, or off.
    /// </summary>
    public AsyncCommand WhenFinishedCommand { get; }

    /// <summary>
    /// Gets the command that goes ahead with the dialog.
    /// </summary>
    public AsyncCommand ConfirmCommand { get; }

    /// <summary>
    /// Gets the command that closes the dialog.
    /// </summary>
    public AsyncCommand DismissCommand { get; }

    /// <summary>
    /// Shows the sessions of a workspace.
    /// </summary>
    /// <param name="workspace">The main workspace, or <see langword="null" /> when none is selected.</param>
    /// <param name="sessions">The rows, the main session first.</param>
    public void Show(WorkspaceInfo? workspace, IReadOnlyList<SessionEntry> sessions)
    {
        if (workspace?.Id != main?.Id)
        {
            expanded = null;
            Dialog = SessionDialog.None;
            IsOpen = false;
        }

        main = workspace;
        rows.Clear();
        rows.AddRange(sessions);
        Build();

        // A question about a session that has gone, or that no longer is what it was asked about, has nothing to act on.
        if (dialog is SessionDialog.None or SessionDialog.New)
        {
            return;
        }

        SessionEntry? asked = rows.FirstOrDefault(row => row.Id == target);

        if (asked is null || dialog == SessionDialog.CancelIntegration && !asked.IsIntegrating || dialog is SessionDialog.Integrate or SessionDialog.IntegrateWhenFinished && !asked.CanManage)
        {
            Dialog = SessionDialog.None;
        }
    }

    private void Build()
    {
        List<SessionEntry> wanted = [.. rows.Select(row => row with { IsExpanded = row.Id == expanded && row.HasActions })];

        while (Entries.Count > wanted.Count)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }

        for (int index = 0; index < wanted.Count; index++)
        {
            if (index == Entries.Count)
            {
                Entries.Add(wanted[index]);
            }
            else if (Entries[index] != wanted[index])
            {
                Entries[index] = wanted[index];
            }
        }
    }

    private Task BeginNew()
    {
        if (main is null)
        {
            return Task.CompletedTask;
        }

        Error = null;
        Name = string.Empty;
        FirstTask = string.Empty;
        // Decided as the dialog opens and left alone after that: unfinished work of a busy AI is best left behind.
        UseCurrentFiles = !isAiWorking(main.Id);
        Raise(nameof(UseCurrentCommit));
        Dialog = SessionDialog.New;
        return Task.CompletedTask;
    }

    private Task Begin(SessionDialog kind, SessionEntry? entry)
    {
        if (entry is not { IsSide: true })
        {
            return Task.CompletedTask;
        }

        Error = null;
        target = entry.Id;
        targetName = entry.Side!.Name;
        Name = entry.Side.Name;
        Dialog = kind;
        return Task.CompletedTask;
    }

    private async Task SwitchWhenFinished(SessionEntry? entry)
    {
        if (entry is not { CanManage: true })
        {
            return;
        }

        if (entry.Side!.IntegrateWhenFinished)
        {
            await Attempt(() => connection.SetIntegrateWhenFinished(entry.Id, false, CancellationToken.None));
        }
        else
        {
            await Begin(SessionDialog.IntegrateWhenFinished, entry);
        }
    }

    private async Task Confirm()
    {
        if (!CanConfirm || main is null || dialog == SessionDialog.None)
        {
            return;
        }

        if (NeedsName && string.IsNullOrWhiteSpace(name))
        {
            Error = "Enter a name.";
            return;
        }

        SessionDialog asked = dialog;
        Guid id = target;
        Guid parent = main.Id;
        string given = name.Trim();
        string task = firstTask.Trim();
        SideSessionStart start = useCurrentFiles ? SideSessionStart.Files : SideSessionStart.Commit;
        WorkspaceInfo? made = null;
        int mine = turn;
        waiting = mine;
        Raise(nameof(CanConfirm));
        Error = null;

        try
        {
            switch (asked)
            {
                case SessionDialog.New:
                    if (prepare is not null && !await prepare(parent))
                    {
                        throw new RemoteException("The workspace's settings could not be saved.");
                    }

                    made = await connection.CreateSideSession(parent, given, start, task.Length == 0 ? null : task, CancellationToken.None);
                    break;
                case SessionDialog.Rename:
                    await connection.RenameSideSession(id, given, CancellationToken.None);
                    break;
                case SessionDialog.Delete:
                    Discarding?.Invoke(id);
                    await connection.DeleteSideSession(id, CancellationToken.None);
                    break;
                case SessionDialog.Integrate:
                    await connection.IntegrateSideSession(id, CancellationToken.None);
                    break;
                case SessionDialog.IntegrateWhenFinished:
                    await connection.SetIntegrateWhenFinished(id, true, CancellationToken.None);
                    break;
                case SessionDialog.CancelIntegration:
                    await connection.CancelIntegration(id, CancellationToken.None);
                    break;
            }

            if (turn == mine)
            {
                Dialog = SessionDialog.None;
            }

            if (made is not null)
            {
                Made?.Invoke(made, task.Length > 0);
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or RemoteException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
        {
            if (turn == mine)
            {
                Error = exception.Message;
            }
            else
            {
                Failed?.Invoke(exception.Message);
            }
        }
        finally
        {
            if (waiting == mine)
            {
                waiting = null;
                Raise(nameof(CanConfirm));
            }
        }
    }
}
