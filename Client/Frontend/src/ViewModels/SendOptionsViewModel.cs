namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The Send Options panel of a workspace: what is submitted (what is in the message box, or the review or expand prompt, as the button it is opened from says), when it is given to the AI (now, next or over and over), where (in this chat or in a new session, on this server or a paired one) and what happens once it is done.
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
            if (args.PropertyName is nameof(ChatViewModel.Input) or nameof(ChatViewModel.HasContent) or nameof(ChatViewModel.HasAttachments) or nameof(ChatViewModel.BlockReason) or nameof(ChatViewModel.IsAiWorking) or nameof(ChatViewModel.IsAutoCommitOn) or nameof(ChatViewModel.IsCommitting))
            {
                RaiseModes();
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

    private SubmissionKind submission;

    // The review or expand prompt, once it is read.
    private string prompt = string.Empty;

    private SendWhen when;

    private bool isNewSession;

    private bool commitsNow;

    private bool commitsQueued = true;

    private bool commitsRuns;

    private string name = string.Empty;

    private string expectedResult = string.Empty;

    private Choice<AiCapability> selectedCapability;

    private RunTarget target;

    private bool useCurrentFiles;

    private bool? bringInChoice;

    /// <summary>
    /// Occurs when a side session was made, with what was submitted as its first task.
    /// </summary>
    public event Action<WorkspaceInfo>? SessionMade;

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
    /// Gets what kind of thing is submitted, which cannot be changed: it follows from the button the panel is opened from.
    /// </summary>
    public string SubmissionTitle => submission.ToString();

    /// <summary>
    /// Gets what is submitted: what is typed, or the review or expand prompt once it is read.
    /// </summary>
    public string SubmissionText => submission == SubmissionKind.Custom ? chat.Input.Trim() : prompt;

    /// <summary>
    /// Gets a value indicating whether what is submitted has text.
    /// </summary>
    public bool HasSubmissionText => SubmissionText.Length > 0;

    /// <summary>
    /// Gets the files that are submitted along, or an empty text without any.
    /// </summary>
    public string SubmissionFiles => submission == SubmissionKind.Custom && chat.HasAttachments ? $"Attached: {string.Join(", ", chat.Attachments.Select(attachment => attachment.Name))}" : string.Empty;

    /// <summary>
    /// Gets a value indicating whether files are submitted along.
    /// </summary>
    public bool HasSubmissionFiles => SubmissionFiles.Length > 0;

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
    /// Gets a value indicating whether a loop can be started: not while one runs, nor with attached files, which a loop cannot send, nor while the server takes nothing for the AI, other than during a commit that follows work.
    /// </summary>
    public bool CanRepeat => !isLooping() && !HasFiles && (CanSendNow || chat.IsCommitting);

    /// <summary>
    /// Gets a value indicating whether Now can be chosen, which takes this chat taking a message at once or else a new session.
    /// </summary>
    public bool CanChooseNow => CanSendNow || !HasFiles;

    /// <summary>
    /// Gets a value indicating whether this chat can be chosen, which it cannot for Now while it takes nothing at once.
    /// </summary>
    public bool CanChooseThisChat => when != SendWhen.Now || CanSendNow;

    /// <summary>
    /// Gets or sets a value indicating whether the work goes to the chat on screen. Setting it to <see langword="false" /> changes nothing.
    /// </summary>
    public bool IsThisChat
    {
        get => !isNewSession;
        set
        {
            if (value && CanChooseThisChat)
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
    /// Gets a value indicating whether a new session can be chosen, which only work that is given at once can start, and which cannot take attached files.
    /// </summary>
    public bool CanChooseNewSession => when == SendWhen.Now && !HasFiles;

    /// <summary>
    /// Gets why choices are not offered, a line for each reason, or an empty text when all are.
    /// </summary>
    public string UnavailableNote => string.Join('\n', new[]
    {
        chat.BlockReason is { Length: > 0 } reason ? $"This chat cannot take it now: {char.ToLowerInvariant(reason[0])}{reason[1..]}." : null,
        isLooping() ? "A loop already runs, so Repeat is not offered." : null,
        HasFiles ? "Attached files can only be sent to this chat, now or next." : null
    }.OfType<string>());

    /// <summary>
    /// Gets a value indicating whether some choice is not offered.
    /// </summary>
    public bool HasUnavailableNote => UnavailableNote.Length > 0;

    /// <summary>
    /// Gets a value indicating whether it can be chosen whether work in this chat is committed once it is done.
    /// </summary>
    public bool ShowCommitChoice => !isNewSession && git.IsRepo;

    /// <summary>
    /// Gets a value indicating whether the panel says that what is sent now joins work that is committed anyway.
    /// </summary>
    public bool ShowJoinsCommit => ShowCommitChoice && when == SendWhen.Now && chat.IsAiWorking && chat.IsAutoCommitOn;

    /// <summary>
    /// Gets a value indicating whether the settings of a loop show.
    /// </summary>
    public bool ShowLoop => when == SendWhen.Repeat;

    /// <summary>
    /// Gets a value indicating whether the new session goes to a paired server.
    /// </summary>
    public bool IsRemote => isNewSession && !target.IsLocal;

    /// <summary>
    /// Gets a value indicating whether it can be chosen what the files of the new session start from, which only a session on this server offers.
    /// </summary>
    public bool ShowStartingFiles => isNewSession && target.IsLocal;

    /// <summary>
    /// Gets or sets a value indicating whether the changes are committed after the work, kept apart for each choice of when: off for Now and for the runs of Repeat, and on for Next, unless a loaded draft says otherwise. Setting it to <see langword="false" /> changes nothing.
    /// </summary>
    public bool CommitsAfter
    {
        get => when switch
        {
            SendWhen.Next => commitsQueued,
            SendWhen.Repeat => commitsRuns,
            _ => commitsNow
        };
        set
        {
            if (value)
            {
                SetCommits(true);
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the changes of the work stay uncommitted. Setting it to <see langword="false" /> changes nothing.
    /// </summary>
    public bool KeepsChanges
    {
        get => !CommitsAfter;
        set
        {
            if (value)
            {
                SetCommits(false);
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
    /// Gets or sets a value indicating whether the new session is brought into this workspace once its work is done, which it is until another choice is made. Setting it to <see langword="false" /> changes nothing.
    /// </summary>
    public bool BringsIn
    {
        get => bringInChoice ?? true;
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

    private bool CanSendNow => chat.BlockReason is null;

    // Attached files can only go to this chat, now or next.
    private bool HasFiles => submission == SubmissionKind.Custom && chat.HasAttachments;

    /// <summary>
    /// Takes note that a loop started or ended.
    /// </summary>
    public void LoopChanged() => RaiseModes();

    /// <summary>
    /// Opens the panel on sending to this chat now, or next while the chat takes nothing at once.
    /// </summary>
    /// <param name="kind">What it submits.</param>
    /// <returns>A task that completes once what the panel shows and offers is read.</returns>
    public async Task Open(SubmissionKind kind)
    {
        int mine = ++turn;
        Error = null;
        submission = kind;
        prompt = string.Empty;
        when = CanSendNow ? SendWhen.Now : SendWhen.Next;
        isNewSession = false;
        commitsNow = false;
        commitsQueued = kind != SubmissionKind.Custom || (chat.LoadedAutoCommit ?? true);
        commitsRuns = false;
        bringInChoice = null;
        Name = string.Empty;
        ExpectedResult = string.Empty;
        SelectedCapability = Capabilities.First(choice => choice.Value == AiCapability.Balanced);
        ShowTargets([]);
        // Decided as the panel opens and left alone after that: unfinished work of a busy AI is best left behind.
        UseCurrentFiles = !(workspace.Side is null ? chat.IsAiWorking : IsMainWorking?.Invoke(MainId) == true);
        Raise(nameof(UseCurrentCommit));
        RaiseModes();
        IsOpen = true;
        await Task.WhenAll(ReadPrompt(mine), Loop.Prepare(), ReadTargets(mine));
    }

    private async Task ReadPrompt(int mine)
    {
        if (submission == SubmissionKind.Custom)
        {
            return;
        }

        string read = string.Empty;
        string? problem = null;

        try
        {
            WorkspaceSettings settings = await connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None);
            read = (submission == SubmissionKind.Review ? settings.ReviewPrompt ?? settings.GlobalReviewPrompt : settings.ExpandPrompt ?? settings.GlobalExpandPrompt).Trim();
            problem = read.Length == 0 ? "The prompt of this button is empty." : null;
        }
        catch (Exception exception) when (exception is IOException or SocketException or RemoteException or ObjectDisposedException or InvalidDataException)
        {
            problem = $"The prompt could not be read: {exception.Message}";
        }

        if (mine == turn)
        {
            prompt = read;
            Error = problem;
            RaiseModes();
        }
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
        if (!selected || when == chosen || chosen == SendWhen.Repeat && !CanRepeat || chosen == SendWhen.Now && !CanChooseNow)
        {
            return;
        }

        when = chosen;
        // Where this chat takes nothing at once, Now is about a new session.
        isNewSession = chosen == SendWhen.Now && (isNewSession || !CanSendNow);
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

    private void SetCommits(bool value)
    {
        switch (when)
        {
            case SendWhen.Next:
                commitsQueued = value;
                break;
            case SendWhen.Repeat:
                commitsRuns = value;
                break;
            default:
                commitsNow = value;
                break;
        }

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
        foreach (string property in (string[])
        [
            nameof(SubmissionTitle), nameof(SubmissionText), nameof(HasSubmissionText), nameof(SubmissionFiles), nameof(HasSubmissionFiles),
            nameof(IsNow), nameof(IsNext), nameof(IsRepeat), nameof(CanChooseNow), nameof(CanRepeat), nameof(IsThisChat), nameof(IsNewSession), nameof(CanChooseThisChat), nameof(CanChooseNewSession),
            nameof(UnavailableNote), nameof(HasUnavailableNote), nameof(ShowCommitChoice), nameof(ShowJoinsCommit), nameof(CommitsAfter), nameof(KeepsChanges), nameof(ShowLoop),
            nameof(IsRemote), nameof(ShowStartingFiles), nameof(NameLabel), nameof(BringInText), nameof(ConfirmText)
        ])
        {
            Raise(property);
        }

        RaiseBringIn();
        ConfirmCommand.Refresh();
    }

    private bool CanConfirm()
    {
        if (submission == SubmissionKind.Custom ? !chat.HasContent : prompt.Length == 0)
        {
            return false;
        }

        if (isNewSession)
        {
            return CanChooseNewSession;
        }

        return when switch
        {
            SendWhen.Next => submission != SubmissionKind.Custom || chat.QueueCommand.CanExecute(null),
            SendWhen.Repeat => CanRepeat,
            _ => submission == SubmissionKind.Custom ? chat.SendCommand.CanExecute(null) : CanSendNow
        };
    }

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
            await (submission == SubmissionKind.Custom ? chat.QueueTyped(commit) : chat.QueuePrompt(prompt, commit));
        }
        else
        {
            bool commit = git.IsRepo && commitsNow;
            IsOpen = false;
            await (submission == SubmissionKind.Custom ? chat.SendTyped(commit) : chat.SendPrompt(prompt, commit));
        }
    }

    private async Task StartLoop()
    {
        string typed = chat.Input;
        bool sendsTyped = submission == SubmissionKind.Custom;
        LoopSettings settings;

        try
        {
            settings = Loop.Build(DateTimeOffset.Now, SubmissionText);
        }
        catch (InvalidOperationException exception)
        {
            Error = exception.Message;
            return;
        }

        settings = settings with { AutoCommit = commitsRuns && git.IsRepo };
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
        bool fromBox = submission == SubmissionKind.Custom;
        string typed = chat.Input;
        string task = SubmissionText;

        if (given.Length == 0)
        {
            Error = IsRemote ? "Enter a title." : "Enter a name.";
            return;
        }

        bool bringIn = BringsIn;

        if (IsRemote)
        {
            SwarmTaskRequest request = new() { Title = given, Instructions = task, ExpectedResult = expectedResult.Trim(), Capability = selectedCapability.Value, PeerId = target.PeerId, BringIn = bringIn };
            List<Guid> added = [];
            await Request(async () =>
            {
                SwarmStatus before = await connection.GetSwarm(CancellationToken.None);
                SwarmStatus after = await connection.DelegateTask(workspace.Id, request, CancellationToken.None);
                added.AddRange(after.Tasks.Where(made => !made.IsIncoming && before.Tasks.All(old => old.Id != made.Id)).Select(made => made.Id));
            }, () =>
            {
                if (fromBox)
                {
                    Take(typed);
                }

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
            if (fromBox)
            {
                Take(typed);
            }

            SessionMade?.Invoke(session!);
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
