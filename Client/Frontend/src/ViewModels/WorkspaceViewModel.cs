namespace Markwardt.AutoRemote.Client;

/// <summary>
/// One focused workspace: its conversation with the AI, the files in its folder, or its settings.
/// </summary>
internal sealed class WorkspaceViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkspaceViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    /// <param name="workspace">The workspace.</param>
    /// <param name="layout">How the conversation and files are laid out.</param>
    /// <param name="drafts">Where the text typed in the message box is kept, or <see langword="null" /> to keep none.</param>
    /// <param name="recorder">Records speech for voice input, or <see langword="null" /> when this client cannot record.</param>
    public WorkspaceViewModel(IServerConnection connection, WorkspaceInfo workspace, ClientLayout layout = ClientLayout.Tabs, IDraftStore? drafts = null, IVoiceRecorder? recorder = null)
    {
        this.layout = layout;
        this.connection = connection;
        Workspace = workspace;
        Chat = new ChatViewModel(connection, workspace.Id, drafts, recorder);
        Git = new GitViewModel(connection, workspace.Id);
        Files = new FilesViewModel(connection, workspace.Id, Git, layout == ClientLayout.Panels);
        Scripts = new ScriptsViewModel(connection, workspace.Id);
        Settings = new SettingsViewModel(connection, workspace);
        Loop = new LoopViewModel(connection, workspace, Chat.ReviewPrompt);
        Loop.Started += () => Chat.Notice = null;
        OpenLoopCommand = new AsyncCommand(_ => Loop.Open());
        CancelLoopCommand = new AsyncCommand(_ => CancelLoop());
        Chat.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatViewModel.HasContent))
            {
                Raise(nameof(ShowHaltButtons));
                Raise(nameof(ShowSendButton));
                Raise(nameof(ShowReviewButton));
                Raise(nameof(ShowPromptButtons));
                Raise(nameof(ShowPauseButton));
                Raise(nameof(ShowResumeButton));
                Raise(nameof(ShowLoopButton));
            }
        };
        Chat.PromptStarted += () => NotifyWhenReady = true;
        Chat.PromptFailed += () => NotifyWhenReady = false;
        Chat.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatViewModel.IsAiWorking))
            {
                Raise(nameof(IsAiWorking));
                UpdateState();

                if (Chat.IsAiWorking && Git.IsWorking && tab is WorkspaceTab.Files or WorkspaceTab.History)
                {
                    Tab = WorkspaceTab.Chat;
                }
            }
        };
        Scripts.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ScriptsViewModel.IsRunning))
            {
                UpdateState();
            }
            else if (args.PropertyName == nameof(ScriptsViewModel.ActivePaths))
            {
                Files.ShowRunning(Scripts.ActivePaths);
            }
        };
        Files.ScriptRequested += path => _ = RunScript(path);
        Files.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FilesViewModel.IsPreviewing))
            {
                Raise(nameof(ShowContentPanel));
            }
        };
        Git.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(GitViewModel.IsWorking))
            {
                UpdateState();
            }

            if (args.PropertyName == nameof(GitViewModel.IsRepo))
            {
                if (!Git.IsRepo && tab == WorkspaceTab.History)
                {
                    Tab = WorkspaceTab.Chat;
                }

                Raise(nameof(ShowHistoryTab));
                Raise(nameof(ShowSideTabs));
                RaiseLayout();
            }
        };
        ShowChatCommand = new AsyncCommand(_ =>
        {
            Tab = WorkspaceTab.Chat;
            return Task.CompletedTask;
        });
        ShowScriptCommand = new AsyncCommand(_ =>
        {
            Tab = WorkspaceTab.Script;
            return Task.CompletedTask;
        });
        ShowHistoryCommand = new AsyncCommand(async _ =>
        {
            Tab = WorkspaceTab.History;
            _ = Git.SyncOnce();
            await Git.History.Start();
        });
        ShowFilesCommand = new AsyncCommand(async _ =>
        {
            Tab = WorkspaceTab.Files;
            _ = Git.Refresh();
            await Files.Start();
        });
        ToggleSettingsCommand = new AsyncCommand(_ =>
        {
            if (tab == WorkspaceTab.Settings)
            {
                Tab = beforeSettings;
            }
            else
            {
                beforeSettings = tab;
                Tab = WorkspaceTab.Settings;
                _ = Settings.Reload();
            }

            return Task.CompletedTask;
        });
    }

    private readonly ClientLayout layout;

    private readonly IServerConnection connection;

    private WorkspaceState? forced;

    private bool subscribed;

    private LoopInfo? activeLoop;

    private CancellationTokenSource? loopClock;

    private WorkspaceTab tab;

    private WorkspaceTab beforeSettings;

    /// <summary>
    /// Gets the workspace as it was when it was focused.
    /// </summary>
    public WorkspaceInfo Workspace { get; }

    /// <summary>
    /// Gets or sets a value indicating whether the user should be told once the workspace is ready again, which is the case while work this client gave it is running.
    /// </summary>
    public bool NotifyWhenReady { get; set; }

    /// <summary>
    /// Gets the conversation.
    /// </summary>
    public ChatViewModel Chat { get; }

    /// <summary>
    /// Gets the git state, history and controls.
    /// </summary>
    public GitViewModel Git { get; }

    /// <summary>
    /// Gets the file browser.
    /// </summary>
    public FilesViewModel Files { get; }

    /// <summary>
    /// Gets the scripts and tasks the server runs in the workspace.
    /// </summary>
    public ScriptsViewModel Scripts { get; }

    /// <summary>
    /// Gets the workspace's settings.
    /// </summary>
    public SettingsViewModel Settings { get; }

    /// <summary>
    /// Gets the absolute path on the server of a path relative to the workspace folder.
    /// </summary>
    /// <param name="relative">The path relative to the workspace folder, with <c>/</c> separators.</param>
    /// <returns>The absolute path, written with the separator the server's paths use.</returns>
    public string AbsolutePath(string relative)
    {
        string folder = Workspace.Folder.TrimEnd('/', '\\');
        char separator = Workspace.Folder.Contains('\\') && !Workspace.Folder.Contains('/') ? '\\' : '/';
        return relative.Length == 0 ? folder : folder + separator + relative.Replace('/', separator);
    }

    /// <summary>
    /// Gets a value indicating whether the AI is working in the workspace.
    /// </summary>
    public bool IsAiWorking => Chat.IsAiWorking;

    /// <summary>
    /// Gets what the workspace is doing: only one kind of work runs at a time, and more work only starts once it is <see cref="WorkspaceState.Ready" />, except AI input, which is also accepted while <see cref="WorkspaceState.Working" />.
    /// </summary>
    public WorkspaceState State => forced ?? (Scripts.IsRunning ? WorkspaceState.Running : Git.IsWorking ? WorkspaceState.Managing : Chat.IsAiWorking ? WorkspaceState.Working : WorkspaceState.Ready);

    /// <summary>
    /// Gets a value indicating whether the work in the workspace can be stopped: the AI works, a script runs or git works.
    /// </summary>
    public bool CanHalt => State is WorkspaceState.Working or WorkspaceState.Running or WorkspaceState.Managing;

    /// <summary>
    /// Gets a value indicating whether the message box offers the work buttons instead of sending: a script or git runs or work is being ended, or the AI works and nothing is typed, so more input could not be sent.
    /// </summary>
    public bool ShowHaltButtons => State is WorkspaceState.Running or WorkspaceState.Managing or WorkspaceState.Canceling || State == WorkspaceState.Working && !Chat.HasContent;

    /// <summary>
    /// Gets the panel in which a loop of the AI is set up.
    /// </summary>
    public LoopViewModel Loop { get; }

    /// <summary>
    /// Gets the command that opens the loop panel.
    /// </summary>
    public AsyncCommand OpenLoopCommand { get; }

    /// <summary>
    /// Gets the command that cancels the loop without stopping the run in progress.
    /// </summary>
    public AsyncCommand CancelLoopCommand { get; }

    /// <summary>
    /// Gets the loop that runs in the workspace, or <see langword="null" /> when none does.
    /// </summary>
    public LoopInfo? ActiveLoop
    {
        get => activeLoop;
        private set
        {
            if (Set(ref activeLoop, value))
            {
                Raise(nameof(IsLooping));
                Raise(nameof(LoopTip));
                Raise(nameof(LoopNext));
                FollowLoopClock();
                Raise(nameof(ShowLoopButton));
                Raise(nameof(ShowCancelLoopButton));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether a loop runs in the workspace, which shows the loop icon in the header and the button that cancels it.
    /// </summary>
    public bool IsLooping => activeLoop is not null;

    /// <summary>
    /// Gets a description of the loop that runs, for the tooltip of its icon: what it sends, how often, when it ends and how many runs it made.
    /// </summary>
    public string LoopTip => activeLoop is null ? string.Empty : $"Loop: {(activeLoop.Settings.Prompt == Chat.ReviewPrompt ? "review" : "custom prompt")}, {(activeLoop.Settings.IntervalSeconds > 0 ? $"every {activeLoop.Settings.IntervalSeconds / 60.0:0.#} min" : "back to back")}, {Describe(activeLoop.Settings)}, {activeLoop.Runs} run{(activeLoop.Runs == 1 ? string.Empty : "s")} so far";

    /// <summary>
    /// Gets when the next automatic prompt of the loop is sent, in words, which the loop icon in the header shows when clicked and which counts down while it is open.
    /// </summary>
    public string LoopNext => DescribeNext(DateTimeOffset.UtcNow);

    /// <summary>
    /// Gets a value indicating whether nothing is typed and the AI is ready or working, which is when the message box offers the loop button.
    /// </summary>
    public bool ShowPromptButtons => ShowReviewButton || State == WorkspaceState.Working && !Chat.HasContent;

    /// <summary>
    /// Gets a value indicating whether the message box shows the pause button: nothing is typed and the AI is working.
    /// </summary>
    public bool ShowPauseButton => State == WorkspaceState.Working && !Chat.HasContent;

    /// <summary>
    /// Gets a value indicating whether the message box shows the resume button: nothing is typed and the workspace is ready.
    /// </summary>
    public bool ShowResumeButton => State == WorkspaceState.Ready && !Chat.HasContent;

    /// <summary>
    /// Gets a value indicating whether the message box shows the loop button next to the review or stop button: there is no loop yet and nothing is typed, while the AI is ready or working (a loop started during work waits for it to finish).
    /// </summary>
    public bool ShowLoopButton => ShowPromptButtons && activeLoop is null;

    /// <summary>
    /// Gets a value indicating whether the message box shows the button that cancels the loop, whatever else it shows.
    /// </summary>
    public bool ShowCancelLoopButton => activeLoop is not null;

    /// <summary>
    /// Gets a value indicating whether the message box shows the send button: something is typed or attached and the work buttons do not show.
    /// </summary>
    public bool ShowSendButton => !ShowHaltButtons && Chat.HasContent;

    /// <summary>
    /// Gets a value indicating whether the message box shows the review button in place of the send button: nothing is typed or attached and the work buttons do not show.
    /// </summary>
    public bool ShowReviewButton => !ShowHaltButtons && !Chat.HasContent;

    /// <summary>
    /// Gets a value indicating whether the workspace is in any state but <see cref="WorkspaceState.Ready" />, which turns the header blue.
    /// </summary>
    public bool IsWorking => State != WorkspaceState.Ready;

    /// <summary>
    /// Gets the tab showing now.
    /// </summary>
    public WorkspaceTab Tab
    {
        get => tab;
        private set
        {
            if (Set(ref tab, value))
            {
                Raise(nameof(ShowChat));
                Raise(nameof(ShowFiles));
                Raise(nameof(ShowHistory));
                Raise(nameof(ShowScript));
                Raise(nameof(ShowSettings));
                RaiseLayout();
            }
        }
    }

    /// <summary>
    /// Gets how many columns of the workspace area a panel spans: all of them with tabs, one with panels.
    /// </summary>
    public int Span => layout == ClientLayout.Panels ? 1 : 5;

    /// <summary>
    /// Gets the column of the file content panel.
    /// </summary>
    public int ContentColumn => layout == ClientLayout.Panels ? 2 : 0;

    /// <summary>
    /// Gets the column of the conversation panel.
    /// </summary>
    public int ChatColumn => layout == ClientLayout.Panels ? 4 : 0;

    /// <summary>
    /// Gets a value indicating whether the file system panel is showing.
    /// </summary>
    public bool ShowFilesPanel => !ShowSettings && (layout == ClientLayout.Panels || ShowFiles);

    /// <summary>
    /// Gets a value indicating whether the panel of the conversation, the scripts and the history is showing.
    /// </summary>
    public bool ShowChatPanel => !ShowSettings && (layout == ClientLayout.Panels || ShowChat || ShowScript || ShowHistory);

    /// <summary>
    /// Gets a value indicating whether the conversation is showing in its panel.
    /// </summary>
    public bool ShowChatView => layout == ClientLayout.Panels ? tab is not (WorkspaceTab.History or WorkspaceTab.Script) : ShowChat;

    /// <summary>
    /// Gets a value indicating whether the scripts are showing in the panel of the conversation.
    /// </summary>
    public bool ShowScriptView => tab == WorkspaceTab.Script;

    /// <summary>
    /// Gets a value indicating whether the history is showing in its panel.
    /// </summary>
    public bool ShowHistoryView => ShowHistory && Git.IsRepo;

    /// <summary>
    /// Gets a value indicating whether the file content panel is showing: always with panels, and over the file system or the history only while a file or diff is previewed with tabs.
    /// </summary>
    public bool ShowContentPanel => !ShowSettings && (layout == ClientLayout.Panels || ((ShowFiles || ShowHistory) && Files.IsPreviewing));

    /// <summary>
    /// Gets a value indicating whether the history can be shown, which needs a git repository.
    /// </summary>
    public bool ShowHistoryTab => Git.IsRepo;

    /// <summary>
    /// Gets a value indicating whether the panel of the conversation has tabs for the conversation, the scripts and the history, which only panels do; tabs along the bottom hold them next to the files otherwise.
    /// </summary>
    public bool ShowSideTabs => layout == ClientLayout.Panels;

    /// <summary>
    /// Gets a value indicating whether the separators between the panels are showing.
    /// </summary>
    public bool ShowSeparators => layout == ClientLayout.Panels && !ShowSettings;

    /// <summary>
    /// Gets a value indicating whether the tabs along the bottom are showing.
    /// </summary>
    public bool ShowTabs => layout == ClientLayout.Tabs;

    /// <summary>
    /// Gets a value indicating whether the conversation is showing.
    /// </summary>
    public bool ShowChat => tab == WorkspaceTab.Chat;

    /// <summary>
    /// Gets a value indicating whether the file browser is showing.
    /// </summary>
    public bool ShowFiles => tab == WorkspaceTab.Files;

    /// <summary>
    /// Gets a value indicating whether the history is showing.
    /// </summary>
    public bool ShowHistory => tab == WorkspaceTab.History;

    /// <summary>
    /// Gets a value indicating whether the scripts tab is selected.
    /// </summary>
    public bool ShowScript => tab == WorkspaceTab.Script;

    /// <summary>
    /// Gets a value indicating whether the settings are showing.
    /// </summary>
    public bool ShowSettings => tab == WorkspaceTab.Settings;

    /// <summary>
    /// Gets the command that shows the conversation.
    /// </summary>
    public AsyncCommand ShowChatCommand { get; }

    /// <summary>
    /// Gets the command that shows the scripts.
    /// </summary>
    public AsyncCommand ShowScriptCommand { get; }

    /// <summary>
    /// Gets the command that shows the git history.
    /// </summary>
    public AsyncCommand ShowHistoryCommand { get; }

    /// <summary>
    /// Gets the command that shows the file browser, brought up to date and watched for changes.
    /// </summary>
    public AsyncCommand ShowFilesCommand { get; }

    /// <summary>
    /// Gets the command that shows the settings, or goes back to the tab that was showing before them.
    /// </summary>
    public AsyncCommand ToggleSettingsCommand { get; }

    /// <summary>
    /// Loads the conversation.
    /// </summary>
    /// <returns>A task that completes once the conversation is shown.</returns>
    public async Task Start()
    {
        if (!subscribed)
        {
            subscribed = true;
            connection.EventReceived += Receive;
            connection.Reconnected += Resync;
        }

        _ = ReadState();
        _ = ReadLoop();
        _ = Git.Start();
        _ = Scripts.Start();
        await Chat.Start();
    }

    /// <summary>
    /// Stops the work in the workspace at once, keeping what it did; a refusal is shown in the conversation.
    /// </summary>
    /// <returns>A task that completes once the workspace is ready again.</returns>
    public Task Stop() => Halt(connection.StopWorkspace);

    /// <summary>
    /// Called when the workspace gets the focus again, so the file browser, if showing, catches up and watches the right workspace.
    /// </summary>
    /// <returns>A task that completes once the files are up to date.</returns>
    public Task Focus()
    {
        _ = Git.Refresh();
        Git.StartSync();
        return layout == ClientLayout.Panels || Tab == WorkspaceTab.Files ? Files.Start() : Task.CompletedTask;
    }

    /// <summary>
    /// Called when another workspace gets the focus, so this one stops fetching and pulling in the background.
    /// </summary>
    public void Blur() => Git.StopSync();

    /// <summary>
    /// Stops following the server.
    /// </summary>
    public void Dispose()
    {
        if (subscribed)
        {
            subscribed = false;
            connection.EventReceived -= Receive;
            connection.Reconnected -= Resync;
        }

        loopClock?.Cancel();
        Chat.Dispose();
        Scripts.Dispose();
        Files.Dispose();
        Git.Dispose();
    }

    private void UpdateState()
    {
        WorkspaceState state = State;
        Chat.BlockReason = state switch
        {
            WorkspaceState.Running => "A script is running, wait for it to finish",
            WorkspaceState.Managing => "Git is working, wait for it to finish",
            WorkspaceState.Stopping => "Stopping, wait for it to finish",
            WorkspaceState.Canceling => "Cancelling and reverting, wait for it to finish",
            _ => null
        };
        Git.IsLocked = state is WorkspaceState.Working or WorkspaceState.Running or WorkspaceState.Stopping or WorkspaceState.Canceling;
        Raise(nameof(State));
        Raise(nameof(CanHalt));
        Raise(nameof(ShowHaltButtons));
        Raise(nameof(ShowSendButton));
        Raise(nameof(ShowReviewButton));
        Raise(nameof(ShowPromptButtons));
        Raise(nameof(ShowPauseButton));
        Raise(nameof(ShowResumeButton));
        Raise(nameof(ShowLoopButton));
        Raise(nameof(LoopNext));
        Raise(nameof(IsWorking));
    }

    private void Resync()
    {
        _ = ReadState();
        _ = ReadLoop();
        _ = Chat.Resync();
        _ = Scripts.Start();
        _ = Git.Refresh();

        if (layout == ClientLayout.Panels || Tab == WorkspaceTab.Files)
        {
            _ = Files.Start();
        }
    }

    private void Receive(RemoteEvent remoteEvent)
    {
        if (remoteEvent.WorkspaceId != Workspace.Id)
        {
            return;
        }

        if (remoteEvent.Kind == RemoteEventKind.StateChanged && remoteEvent.State is { } state)
        {
            Follow(state);
        }
        else if (remoteEvent.Kind == RemoteEventKind.LoopChanged)
        {
            ActiveLoop = remoteEvent.Loop;
            Chat.Notice = remoteEvent.Loop is null ? remoteEvent.Text : null;
        }
    }

    private void Follow(WorkspaceState state)
    {
        forced = state is WorkspaceState.Stopping or WorkspaceState.Canceling ? state : null;
        UpdateState();
    }

    private async Task ReadState()
    {
        try
        {
            Follow(await connection.GetWorkspaceState(Workspace.Id, CancellationToken.None));
        }
        catch (Exception exception) when (exception is IOException or SocketException or RemoteException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
        {
        }
    }

    private void FollowLoopClock()
    {
        if (activeLoop is null)
        {
            loopClock?.Cancel();
            loopClock = null;
            return;
        }

        if (loopClock is null)
        {
            loopClock = new CancellationTokenSource();
            _ = TickLoopClock(loopClock.Token);
        }
    }

    private async Task TickLoopClock(CancellationToken cancellation)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellation))
            {
                Raise(nameof(LoopNext));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReadLoop()
    {
        try
        {
            ActiveLoop = await connection.GetLoop(Workspace.Id, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or SocketException or RemoteException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
        {
        }
    }

    private async Task CancelLoop()
    {
        if (!await Attempt(() => connection.CancelLoop(Workspace.Id, CancellationToken.None)) && Error is { } message)
        {
            Chat.Show(message);
        }
    }

    /// <summary>
    /// Describes when the loop sends its next prompt: after the interval since the last run began, or once the workspace is free when that is later.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <returns>The description, or an empty text when no loop runs.</returns>
    public string DescribeNext(DateTimeOffset now)
    {
        if (activeLoop is null)
        {
            return string.Empty;
        }

        bool busy = State != WorkspaceState.Ready;

        if (activeLoop.NextRunAt is not { } next)
        {
            return busy ? "The first prompt is sent when the workspace is free." : "The first prompt is being sent.";
        }

        if (next <= now)
        {
            return busy ? "The next prompt is sent as soon as the workspace is free." : "The next prompt is due now.";
        }

        TimeSpan wait = next - now;
        string span = wait.TotalHours >= 1 ? $"{(int)wait.TotalHours} h {wait.Minutes} min" : wait.TotalMinutes >= 1 ? $"{(int)wait.TotalMinutes} min {wait.Seconds} s" : $"{Math.Max(1, wait.Seconds)} s";
        return busy ? $"The next prompt is sent in {span}, or when the workspace is free if that is later." : $"The next prompt is sent in {span}.";
    }

    private string Describe(LoopSettings settings) => settings.Condition switch
    {
        LoopCondition.UntilTime => $"until {settings.Until?.ToLocalTime():t}",
        LoopCondition.ForDuration => $"for {(int)TimeSpan.FromSeconds(settings.DurationSeconds ?? 0).TotalHours}:{TimeSpan.FromSeconds(settings.DurationSeconds ?? 0).Minutes:00} h",
        LoopCondition.Runs => $"{settings.Runs} runs in all",
        LoopCondition.UntilWindow => $"until {settings.Window} reaches {settings.Percent:0.#}%",
        _ => "until cancelled"
    };

    private async Task Halt(Func<Guid, CancellationToken, Task> action)
    {
        if (!await Attempt(() => action(Workspace.Id, CancellationToken.None)) && Error is { } message)
        {
            Chat.Show(message);
        }
    }

    private async Task RunScript(string path)
    {
        Tab = WorkspaceTab.Script;

        if (State != WorkspaceState.Ready)
        {
            Scripts.Reject(State switch
            {
                WorkspaceState.Running => "A script is already running.",
                WorkspaceState.Managing => "Wait for git to finish before running a script.",
                WorkspaceState.Stopping => "Wait for the work to finish stopping before running a script.",
                WorkspaceState.Canceling => "Wait for the work to finish being cancelled before running a script.",
                _ => "Wait for the AI to finish before running a script."
            });
            return;
        }

        await Scripts.Run(path);
    }

    private void RaiseLayout()
    {
        Raise(nameof(ShowFilesPanel));
        Raise(nameof(ShowChatPanel));
        Raise(nameof(ShowChatView));
        Raise(nameof(ShowScriptView));
        Raise(nameof(ShowHistoryView));
        Raise(nameof(ShowContentPanel));
        Raise(nameof(ShowSeparators));
    }
}
