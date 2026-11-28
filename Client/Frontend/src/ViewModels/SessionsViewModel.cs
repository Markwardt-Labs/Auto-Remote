namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The sessions of the workspace on screen: the list that opens from its title, with the tasks delegated from it, and what the user is asked before a session is renamed, brought in or deleted.
/// </summary>
internal sealed class SessionsViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SessionsViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server.</param>
    public SessionsViewModel(IServerConnection connection)
    {
        this.connection = connection;
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
        NewCommand = new AsyncCommand(_ =>
        {
            if (main is not null)
            {
                IsOpen = false;
                NewRequested?.Invoke();
            }

            return Task.CompletedTask;
        });
        OpenTaskCommand = new AsyncCommand(parameter => parameter is SwarmTaskViewModel row ? OpenTask(row.Task) : Task.CompletedTask, null, false);
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

    private RemoteTaskViewModel? remoteTask;

    /// <summary>
    /// Occurs when the user picked a session to switch to.
    /// </summary>
    public event Action<Guid>? Chosen;

    /// <summary>
    /// Occurs when the user wants to see what a session changed since it started.
    /// </summary>
    public event Action<Guid>? ChangesRequested;

    /// <summary>
    /// Occurs when a side session was made to go on with a delegated task.
    /// </summary>
    public event Action<WorkspaceInfo>? Made;

    /// <summary>
    /// Occurs when the user asks for a new session, which Send Options describes.
    /// </summary>
    public event Action? NewRequested;

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
    /// Gets the tasks delegated from the workspace on screen, its sessions included, the oldest first.
    /// </summary>
    public ObservableCollection<SwarmTaskViewModel> Tasks { get; } = [];

    /// <summary>
    /// Gets a value indicating whether any task was delegated from the workspace.
    /// </summary>
    public bool HasTasks => Tasks.Count > 0;

    /// <summary>
    /// Gets the delegated task that is opened, or <see langword="null" />.
    /// </summary>
    public RemoteTaskViewModel? OpenedTask
    {
        get => remoteTask;
        private set
        {
            if (Set(ref remoteTask, value))
            {
                Raise(nameof(HasTask));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether a delegated task is opened.
    /// </summary>
    public bool HasTask => remoteTask is not null;

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
    /// Gets a value indicating whether the dialog asks for a name.
    /// </summary>
    public bool NeedsName => dialog == SessionDialog.Rename;

    /// <summary>
    /// Gets a value indicating whether confirming throws work away.
    /// </summary>
    public bool IsDestructive => dialog == SessionDialog.Delete;

    /// <summary>
    /// Gets the title of the dialog.
    /// </summary>
    public string DialogTitle => dialog switch
    {
        SessionDialog.Rename => "Rename session",
        SessionDialog.Delete => "Delete session?",
        SessionDialog.Integrate => "Bring Into Main?",
        SessionDialog.IntegrateWhenFinished => "When Done: Bring Into Main?",
        SessionDialog.CancelIntegration => "Cancel Bring In?",
        _ => string.Empty
    };

    /// <summary>
    /// Gets what the dialog explains.
    /// </summary>
    public string DialogText => dialog switch
    {
        SessionDialog.Delete => $"\"{targetName}\" and all of its changes will be permanently discarded.",
        SessionDialog.Integrate => $"The main session's AI brings the changes of \"{targetName}\" into the main session, and the session is then permanently removed. This cannot be undone.",
        SessionDialog.IntegrateWhenFinished => $"Once its work is done and nothing of it is left to run, the main session's AI brings the changes of \"{targetName}\" into the main session by itself, and the session is then permanently removed. This cannot be undone.",
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
        SessionDialog.Rename => "Rename",
        SessionDialog.Delete => "Delete",
        SessionDialog.Integrate => "Bring Into Main",
        SessionDialog.IntegrateWhenFinished => "Turn On",
        SessionDialog.CancelIntegration => "Cancel Bring In",
        _ => string.Empty
    };

    /// <summary>
    /// Gets the label of the button that closes the dialog.
    /// </summary>
    public string DismissText => dialog == SessionDialog.CancelIntegration ? "Keep Bringing In" : "Cancel";

    /// <summary>
    /// Gets or sets the name typed for a session.
    /// </summary>
    public string Name
    {
        get => name;
        set => Set(ref name, value);
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
    /// Gets the command that asks whether to bring a row's session into the main one.
    /// </summary>
    public AsyncCommand IntegrateCommand { get; }

    /// <summary>
    /// Gets the command that asks whether to cancel bringing a row's session in.
    /// </summary>
    public AsyncCommand CancelIntegrationCommand { get; }

    /// <summary>
    /// Gets the command that switches bringing a row's session in once its work is done on, after asking, or off.
    /// </summary>
    public AsyncCommand WhenFinishedCommand { get; }

    /// <summary>
    /// Gets the command that opens the delegated task given as its parameter, a <see cref="SwarmTaskViewModel" />.
    /// </summary>
    public AsyncCommand OpenTaskCommand { get; }

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
    /// <param name="delegated">The tasks delegated from the workspace and its sessions, the oldest first, or <see langword="null" /> for none.</param>
    public void Show(WorkspaceInfo? workspace, IReadOnlyList<SessionEntry> sessions, IReadOnlyList<SwarmTaskInfo>? delegated = null)
    {
        if (workspace?.Id != main?.Id)
        {
            expanded = null;
            Dialog = SessionDialog.None;
            IsOpen = false;
            CloseTask();
        }

        ShowTasks(delegated ?? []);

        main = workspace;
        rows.Clear();
        rows.AddRange(sessions);
        Build();

        // A question about a session that has gone, or that no longer is what it was asked about, has nothing to act on.
        if (dialog == SessionDialog.None)
        {
            return;
        }

        SessionEntry? asked = rows.FirstOrDefault(row => row.Id == target);

        if (asked is null || dialog == SessionDialog.CancelIntegration && !asked.IsIntegrating || dialog is SessionDialog.Integrate or SessionDialog.IntegrateWhenFinished && !asked.CanManage)
        {
            Dialog = SessionDialog.None;
        }
    }

    /// <summary>
    /// Closes the delegated task that is opened, if any.
    /// </summary>
    public void CloseTask()
    {
        remoteTask?.Dispose();
        OpenedTask = null;
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

    private void ShowTasks(IReadOnlyList<SwarmTaskInfo> delegated)
    {
        List<SwarmTaskViewModel> wanted = [.. delegated.Select(found => new SwarmTaskViewModel(found))];

        if (!wanted.SequenceEqual(Tasks))
        {
            Tasks.Clear();

            foreach (SwarmTaskViewModel row in wanted)
            {
                Tasks.Add(row);
            }

            Raise(nameof(HasTasks));
        }

        if (remoteTask is not null && delegated.FirstOrDefault(found => found.Id == remoteTask.Id) is { } current)
        {
            remoteTask.Take(current);
        }
    }

    private async Task OpenTask(SwarmTaskInfo chosen)
    {
        CloseTask();
        IsOpen = false;
        string? destination = rows.FirstOrDefault(row => row.Id == chosen.WorkspaceId && row.IsSide)?.Name;
        RemoteTaskViewModel opening = new(connection, chosen, destination);
        opening.Closed += () =>
        {
            if (OpenedTask == opening)
            {
                OpenedTask = null;
            }
        };
        opening.Continued += made => Made?.Invoke(made);
        OpenedTask = opening;
        await opening.Load();
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
        string given = name.Trim();
        int mine = turn;
        waiting = mine;
        Raise(nameof(CanConfirm));
        Error = null;

        try
        {
            switch (asked)
            {
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
