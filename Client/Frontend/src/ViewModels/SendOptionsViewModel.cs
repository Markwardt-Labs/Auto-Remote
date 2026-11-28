namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The Send Options panel of a workspace: when the work is given to the AI (now, next or over and over), where (in this chat or in a new session, on this server or a paired one) and what happens once it is done.
/// </summary>
internal sealed class SendOptionsViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SendOptionsViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    /// <param name="workspace">The workspace whose chat is on screen.</param>
    /// <param name="chat">Its chat, whose message box holds what is sent.</param>
    /// <param name="git">Its git state, since what is about commits only shows for a repository.</param>
    /// <param name="isLooping">Tells whether a loop runs in the workspace, which rules out starting another.</param>
    public SendOptionsViewModel(IServerConnection connection, WorkspaceInfo workspace, ChatViewModel chat, GitViewModel git, Func<bool> isLooping)
    {
        this.connection = connection;
        this.workspace = workspace;
        this.chat = chat;
        this.git = git;
        this.isLooping = isLooping;
        Loop = new LoopViewModel(connection, workspace);
        Targets.Add(local);
        target = local;
        selectedCapability = Capabilities.First(choice => choice.Value == AiCapability.Balanced);
        ConfirmCommand = new AsyncCommand(_ => Confirm(), CanConfirm);
        CancelCommand = new AsyncCommand(_ =>
        {
            IsOpen = false;
            return Task.CompletedTask;
        });
        chat.SendCommand.CanExecuteChanged += (_, _) => ConfirmCommand.Refresh();
        chat.QueueCommand.CanExecuteChanged += (_, _) => ConfirmCommand.Refresh();
        chat.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatViewModel.HasText))
            {
                RaiseBringIn();
            }
        };
        git.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(GitViewModel.IsRepo))
            {
                RaiseModes();
            }
        };
    }

    private readonly IServerConnection connection;

    private readonly WorkspaceInfo workspace;

    private readonly ChatViewModel chat;

    private readonly GitViewModel git;

    private readonly Func<bool> isLooping;

    private readonly RunTarget local = new() { Name = "This Server", IsLocal = true };

    // Counts the times the panel was opened, so what a slow request ends with is not taken for the one on screen by then.
    private int turn;

    private bool isOpen;

    private SendWhen when;

    private bool isNewSession;

    private bool commitsQueued = true;

    private string name = string.Empty;

    private string expectedResult = string.Empty;

    private Choice<AiCapability> selectedCapability;

    private RunTarget target;

    private bool useCurrentFiles;

    private bool? bringInChoice;

    /// <summary>
    /// Occurs when a side session was made, with whether it was given a first task.
    /// </summary>
    public event Action<WorkspaceInfo, bool>? SessionMade;

    /// <summary>
    /// Occurs when a task was delegated to a paired server, with the identifiers of what was added.
    /// </summary>
    public event Action<IReadOnlyList<Guid>>? Delegated;

    /// <summary>
    /// Gets the settings of a loop, which Repeat takes.
    /// </summary>
    public LoopViewModel Loop { get; }

    /// <summary>
    /// Gets or sets how the main workspace is made ready before a session is made of it, such as by sending what was edited of its settings.
    /// </summary>
    public Func<Guid, Task<bool>>? PrepareMain { get; set; }

    /// <summary>
    /// Gets or sets how it is told whether the AI of the main workspace works while a side session is on screen.
    /// </summary>
    public Func<Guid, bool>? IsMainWorking { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the panel is showing.
    /// </summary>
    public bool IsOpen
    {
        get => isOpen;
        set => Set(ref isOpen, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the work is given to the AI at once. Setting it to <see langword="false" /> changes nothing: another choice is made by setting that one.
    /// </summary>
    public bool IsNow
    {
        get => when == SendWhen.Now;
        set => Choose(SendWhen.Now, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the work is queued behind the current one. Setting it to <see langword="false" /> changes nothing.
    /// </summary>
    public bool IsNext
    {
        get => when == SendWhen.Next;
        set => Choose(SendWhen.Next, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the work is repeated as a loop. Setting it to <see langword="false" /> changes nothing.
    /// </summary>
    public bool IsRepeat
    {
        get => when == SendWhen.Repeat;
        set => Choose(SendWhen.Repeat, value);
    }

    /// <summary>
    /// Gets a value indicating whether a loop can be started, which it cannot while one runs.
    /// </summary>
    public bool CanRepeat => !isLooping();

    /// <summary>
    /// Gets or sets a value indicating whether the work goes to the chat on screen. Setting it to <see langword="false" /> changes nothing.
    /// </summary>
    public bool IsThisChat
    {
        get => !isNewSession;
        set
        {
            if (value)
            {
                SetNewSession(false);
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the work starts a new session. Setting it to <see langword="false" /> changes nothing.
    /// </summary>
    public bool IsNewSession
    {
        get => isNewSession;
        set
        {
            if (value && CanChooseNewSession)
            {
                SetNewSession(true);
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether a new session can be chosen, which only work that is given at once can start.
    /// </summary>
    public bool CanChooseNewSession => when == SendWhen.Now;

    /// <summary>
    /// Gets a value indicating whether the panel says that Send never turns on auto commit, since there is nothing to choose about what happens when the work is done.
    /// </summary>
    public bool ShowSendNote => when == SendWhen.Now && !isNewSession;

    /// <summary>
    /// Gets a value indicating whether it can be chosen whether a queued input is committed.
    /// </summary>
    public bool ShowQueueChoice => when == SendWhen.Next && git.IsRepo;

    /// <summary>
    /// Gets a value indicating whether the settings of a loop show.
    /// </summary>
    public bool ShowLoop => when == SendWhen.Repeat;

    /// <summary>
    /// Gets a value indicating whether it can be chosen whether every run of the loop is committed.
    /// </summary>
    public bool ShowCommitEachRun => ShowLoop && git.IsRepo;

    /// <summary>
    /// Gets a value indicating whether the new session goes to a paired server.
    /// </summary>
    public bool IsRemote => isNewSession && !target.IsLocal;

    /// <summary>
    /// Gets a value indicating whether it can be chosen what the files of the new session start from, which only a session on this server offers.
    /// </summary>
    public bool ShowStartingFiles => isNewSession && target.IsLocal;

    /// <summary>
    /// Gets or sets a value indicating whether the queued input is committed after its work. Setting it to <see langword="false" /> changes nothing.
    /// </summary>
    public bool CommitsAfter
    {
        get => commitsQueued;
        set
        {
            if (value)
            {
                SetCommitsQueued(true);
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the changes of the queued input stay uncommitted. Setting it to <see langword="false" /> changes nothing.
    /// </summary>
    public bool KeepsChanges
    {
        get => !commitsQueued;
        set
        {
            if (value)
            {
                SetCommitsQueued(false);
            }
        }
    }

    /// <summary>
    /// Gets or sets the name of the new session, which is the title of a delegated task.
    /// </summary>
    public string Name
    {
        get => name;
        set => Set(ref name, value ?? string.Empty);
    }

    /// <summary>
    /// Gets what the name is called.
    /// </summary>
    public string NameLabel => IsRemote ? "Title" : "Name";

    /// <summary>
    /// Gets what the first work of the new session is called.
    /// </summary>
    public string TaskLabel => IsRemote ? "Instructions" : "First Task (optional)";

    /// <summary>
    /// Gets the places the new session can run in: this server, each paired one that receives work, and any of them when there is one.
    /// </summary>
    public ObservableCollection<RunTarget> Targets { get; } = [];

    /// <summary>
    /// Gets or sets where the new session runs.
    /// </summary>
    public RunTarget Target
    {
        get => target;
        set
        {
            if (value is not null && Set(ref target, value))
            {
                RaiseModes();
            }
        }
    }

    /// <summary>
    /// Gets or sets what the result of a delegated task is expected to be, which may be left empty.
    /// </summary>
    public string ExpectedResult
    {
        get => expectedResult;
        set => Set(ref expectedResult, value ?? string.Empty);
    }

    /// <summary>
    /// Gets the capabilities a delegated task can need.
    /// </summary>
    public IReadOnlyList<Choice<AiCapability>> Capabilities { get; } = [.. Enum.GetValues<AiCapability>().Select(value => new Choice<AiCapability>(value, value.ToString()))];

    /// <summary>
    /// Gets or sets the capability a delegated task needs.
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
    /// Gets or sets a value indicating whether a new session on this server starts from the files as they are, uncommitted changes included.
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
    /// Gets or sets a value indicating whether a new session on this server starts from the current commit.
    /// </summary>
    public bool UseCurrentCommit
    {
        get => !useCurrentFiles;
        set => UseCurrentFiles = !value;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the new session is brought into this workspace once its work is done. Until it is chosen, it is when the session is given work, as a delegated task always is. Setting it to <see langword="false" /> changes nothing.
    /// </summary>
    public bool BringsIn
    {
        get => bringInChoice ?? (IsRemote || chat.HasText);
        set
        {
            if (value)
            {
                SetBringIn(true);
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the new session stays separate once its work is done. Setting it to <see langword="false" /> changes nothing.
    /// </summary>
    public bool KeepsSeparate
    {
        get => !BringsIn;
        set
        {
            if (value)
            {
                SetBringIn(false);
            }
        }
    }

    /// <summary>
    /// Gets what the choice of bringing the new session in says: a delegated task comes back to the chat it is delegated from, a session of this server always to the main one.
    /// </summary>
    public string BringInText => IsRemote && workspace.Side is { } side ? $"Bring Into {side.Name}" : "Bring Into Main";

    /// <summary>
    /// Gets what the button that goes ahead says, which is what happens.
    /// </summary>
    public string ConfirmText => isNewSession ? IsRemote ? "Delegate Task" : "Start Session" : when switch
    {
        SendWhen.Next => "Queue",
        SendWhen.Repeat => "Start Loop",
        _ => "Send"
    };

    /// <summary>
    /// Gets the command that does what is chosen.
    /// </summary>
    public AsyncCommand ConfirmCommand { get; }

    /// <summary>
    /// Gets the command that closes the panel.
    /// </summary>
    public AsyncCommand CancelCommand { get; }

    private Guid MainId => workspace.Side?.ParentId ?? workspace.Id;

    /// <summary>
    /// Opens the panel on sending what is typed to this chat now.
    /// </summary>
    /// <returns>A task that completes once what the choices offer is read.</returns>
    public Task Open() => Open(false, null);

    /// <summary>
    /// Shows the panel set to repeat one of the prompts of the workspace, as it is asked for from that prompt's button; while a loop runs it opens as usual instead.
    /// </summary>
    /// <param name="prompt">The prompt to repeat.</param>
    /// <returns>A task that completes once what the panel offers is read.</returns>
    public Task OpenRepeating(LoopPromptKind prompt) => Open(false, prompt);

    /// <summary>
    /// Opens the panel on a new session.
    /// </summary>
    /// <returns>A task that completes once what the choices offer is read.</returns>
    public Task OpenNewSession() => Open(true, null);

    /// <summary>
    /// Takes note that a loop started or ended.
    /// </summary>
    public void LoopChanged()
    {
        Raise(nameof(CanRepeat));
        ConfirmCommand.Refresh();
    }

    private async Task Open(bool newSession, LoopPromptKind? repeated)
    {
        int mine = ++turn;
        Error = null;
        // Asked for with a prompt of the workspace, it is about repeating that prompt, which a loop that already runs leaves no room for.
        when = repeated is not null && CanRepeat ? SendWhen.Repeat : SendWhen.Now;
        isNewSession = newSession;
        commitsQueued = chat.LoadedAutoCommit ?? true;
        bringInChoice = null;
        Name = string.Empty;
        ExpectedResult = string.Empty;
        SelectedCapability = Capabilities.First(choice => choice.Value == AiCapability.Balanced);
        ShowTargets([]);
        // Decided as the panel opens and left alone after that: unfinished work of a busy AI is best left behind.
        UseCurrentFiles = !(workspace.Side is null ? chat.IsAiWorking : IsMainWorking?.Invoke(MainId) == true);
        Raise(nameof(UseCurrentCommit));
        RaiseModes();
        LoopChanged();
        IsOpen = true;
        await Task.WhenAll(Loop.Prepare(chat.HasText, repeated), ReadTargets(mine));
    }

    private async Task ReadTargets(int mine)
    {
        try
        {
            SwarmStatus? status = await connection.GetSwarm(CancellationToken.None);

            if (status is not null && mine == turn)
            {
                ShowTargets(status.Peers);
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or RemoteException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
        {
        }
    }

    private void ShowTargets(IReadOnlyList<SwarmPeer> peers)
    {
        List<RunTarget> receiving = [.. peers.Where(peer => peer.SendWork).Select(peer => new RunTarget { Name = peer.Name, PeerId = peer.Id })];

        if (receiving.Count > 0)
        {
            receiving.Add(new RunTarget { Name = "Any Server" });
        }

        // The first stays, so the list is never without what is chosen.
        while (Targets.Count > 1)
        {
            Targets.RemoveAt(Targets.Count - 1);
        }

        foreach (RunTarget found in receiving)
        {
            Targets.Add(found);
        }

        Target = local;
        Raise(nameof(Target));
    }

    private void Choose(SendWhen chosen, bool selected)
    {
        if (!selected || when == chosen || chosen == SendWhen.Repeat && !CanRepeat)
        {
            return;
        }

        when = chosen;
        isNewSession &= chosen == SendWhen.Now;
        Error = null;
        RaiseModes();
    }

    private void SetNewSession(bool value)
    {
        if (isNewSession != value)
        {
            isNewSession = value;
            Error = null;
            RaiseModes();
        }
    }

    private void SetCommitsQueued(bool value)
    {
        commitsQueued = value;
        Raise(nameof(CommitsAfter));
        Raise(nameof(KeepsChanges));
    }

    private void SetBringIn(bool value)
    {
        bringInChoice = value;
        RaiseBringIn();
    }

    private void RaiseBringIn()
    {
        Raise(nameof(BringsIn));
        Raise(nameof(KeepsSeparate));
    }

    private void RaiseModes()
    {
        Raise(nameof(IsNow));
        Raise(nameof(IsNext));
        Raise(nameof(IsRepeat));
        Raise(nameof(IsThisChat));
        Raise(nameof(IsNewSession));
        Raise(nameof(CanChooseNewSession));
        Raise(nameof(ShowSendNote));
        Raise(nameof(ShowQueueChoice));
        Raise(nameof(CommitsAfter));
        Raise(nameof(KeepsChanges));
        Raise(nameof(ShowLoop));
        Raise(nameof(ShowCommitEachRun));
        Raise(nameof(IsRemote));
        Raise(nameof(ShowStartingFiles));
        Raise(nameof(NameLabel));
        Raise(nameof(TaskLabel));
        Raise(nameof(BringInText));
        Raise(nameof(ConfirmText));
        RaiseBringIn();
        ConfirmCommand.Refresh();
    }

    private bool CanConfirm() => isNewSession || when switch
    {
        SendWhen.Next => chat.QueueCommand.CanExecute(null),
        SendWhen.Repeat => CanRepeat,
        _ => chat.SendCommand.CanExecute(null)
    };

    private async Task Confirm()
    {
        if (!CanConfirm())
        {
            return;
        }

        Error = null;

        if (isNewSession)
        {
            await StartSession();
        }
        else if (when == SendWhen.Repeat)
        {
            await StartLoop();
        }
        else if (when == SendWhen.Next)
        {
            bool commit = commitsQueued;
            IsOpen = false;
            await chat.QueueTyped(commit);
        }
        else
        {
            IsOpen = false;
            chat.SendCommand.Execute(null);
        }
    }

    private async Task StartLoop()
    {
        if (Loop.UseCustom && chat.HasAttachments)
        {
            Error = "A loop cannot send attached files. Remove them first.";
            return;
        }

        string typed = chat.Input;
        LoopSettings settings;

        try
        {
            settings = Loop.Build(DateTimeOffset.Now, typed);
        }
        catch (InvalidOperationException exception)
        {
            Error = exception.Message;
            return;
        }

        bool sendsTyped = Loop.UseCustom;
        settings = settings with { AutoCommit = settings.AutoCommit && git.IsRepo };
        await Request(async () => await connection.StartLoop(workspace.Id, settings, CancellationToken.None), () =>
        {
            chat.Notice = null;

            if (sendsTyped)
            {
                Take(typed);
            }
        });
    }

    private async Task StartSession()
    {
        string given = name.Trim();
        string typed = chat.Input;
        string? task = string.IsNullOrWhiteSpace(typed) ? null : typed.Trim();

        if (given.Length == 0)
        {
            Error = IsRemote ? "Enter a title." : "Enter a name.";
            return;
        }

        if (IsRemote && task is null)
        {
            Error = "Write the instructions of the task.";
            return;
        }

        if (chat.HasAttachments)
        {
            Error = "Attached files cannot go to a new session. Remove them first.";
            return;
        }

        bool bringIn = BringsIn;

        if (IsRemote)
        {
            SwarmTaskRequest request = new() { Title = given, Instructions = task!, ExpectedResult = expectedResult.Trim(), Capability = selectedCapability.Value, PeerId = target.PeerId, BringIn = bringIn };
            List<Guid> added = [];
            await Request(async () =>
            {
                SwarmStatus before = await connection.GetSwarm(CancellationToken.None);
                SwarmStatus after = await connection.DelegateTask(workspace.Id, request, CancellationToken.None);
                added.AddRange(after.Tasks.Where(made => !made.IsIncoming && before.Tasks.All(old => old.Id != made.Id)).Select(made => made.Id));
            }, () =>
            {
                Take(typed);
                chat.Notice = "The task was delegated.";
                Delegated?.Invoke(added);
            });
            return;
        }

        SideSessionStart start = useCurrentFiles ? SideSessionStart.Files : SideSessionStart.Commit;
        Guid main = MainId;
        WorkspaceInfo? session = null;
        await Request(async () =>
        {
            if (PrepareMain is not null && !await PrepareMain(main))
            {
                throw new RemoteException("The workspace's settings could not be saved.");
            }

            session = await connection.CreateSideSession(main, given, start, task, bringIn, CancellationToken.None);
        }, () =>
        {
            Take(typed);
            SessionMade?.Invoke(session!, task is not null);
        });
    }

    // What went where it was asked to leaves the message box, unless it was changed meanwhile.
    private void Take(string typed)
    {
        if (chat.Input == typed)
        {
            chat.Input = string.Empty;
        }
    }

    // A failure shows in the panel, or in the chat once the panel it was asked in is gone.
    private async Task Request(Func<Task> request, Action done)
    {
        int mine = turn;

        if (!await Attempt(request))
        {
            if (mine != turn || !isOpen)
            {
                chat.Show(Error!);
            }

            return;
        }

        if (mine == turn)
        {
            IsOpen = false;
        }

        done();
    }
}
