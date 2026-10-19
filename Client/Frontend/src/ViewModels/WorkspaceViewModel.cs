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
    public WorkspaceViewModel(IServerConnection connection, WorkspaceInfo workspace, ClientLayout layout = ClientLayout.Tabs, IDraftStore? drafts = null)
    {
        this.layout = layout;
        this.connection = connection;
        Workspace = workspace;
        Chat = new ChatViewModel(connection, workspace.Id, drafts);
        Git = new GitViewModel(connection, workspace.Id);
        Files = new FilesViewModel(connection, workspace.Id, Git, layout == ClientLayout.Panels);
        Scripts = new ScriptsViewModel(connection, workspace.Id);
        Settings = new SettingsViewModel(connection, workspace);
        Chat.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatViewModel.Input) || args.PropertyName == nameof(ChatViewModel.HasAttachments))
            {
                Raise(nameof(ShowHaltButtons));
                Raise(nameof(ShowPauseButton));
                Raise(nameof(ShowCancelButton));
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
                Raise(nameof(CanCancel));

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
            }

            return Task.CompletedTask;
        });
    }

    private readonly ClientLayout layout;

    private readonly IServerConnection connection;

    private WorkspaceState? forced;

    private bool subscribed;

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
    /// Gets a value indicating whether the work in the workspace can be stopped: the AI works or is paused, a script runs or git works.
    /// </summary>
    public bool CanHalt => State is WorkspaceState.Working or WorkspaceState.Paused or WorkspaceState.Running or WorkspaceState.Managing;

    /// <summary>
    /// Gets a value indicating whether the AI's work can be paused: the AI is working.
    /// </summary>
    public bool CanPause => State == WorkspaceState.Working;

    /// <summary>
    /// Gets a value indicating whether the AI's work can be resumed: it is paused.
    /// </summary>
    public bool CanResume => State == WorkspaceState.Paused;

    /// <summary>
    /// Gets a value indicating whether the AI's work can be cancelled and reverted: the AI works or is paused, and the workspace is a git repository whose files can be put back.
    /// </summary>
    public bool CanCancel => State is WorkspaceState.Working or WorkspaceState.Paused && Git.IsRepo;

    /// <summary>
    /// Gets a value indicating whether the message box offers the work buttons instead of sending: the AI is paused, a script or git runs or work is being ended, or the AI works and nothing is typed, so more input could not be sent.
    /// </summary>
    public bool ShowHaltButtons => State is WorkspaceState.Paused or WorkspaceState.Running or WorkspaceState.Managing or WorkspaceState.Canceling || State == WorkspaceState.Working && string.IsNullOrWhiteSpace(Chat.Input) && Chat.Attachments.Count == 0;

    /// <summary>
    /// Gets a value indicating whether the pause button shows: the AI works and the work buttons show.
    /// </summary>
    public bool ShowPauseButton => ShowHaltButtons && State == WorkspaceState.Working;

    /// <summary>
    /// Gets a value indicating whether the resume button shows: the AI is paused.
    /// </summary>
    public bool ShowResumeButton => ShowHaltButtons && State == WorkspaceState.Paused;

    /// <summary>
    /// Gets a value indicating whether the cancel button shows, which only AI work has: it is working or paused and the work buttons show.
    /// </summary>
    public bool ShowCancelButton => ShowHaltButtons && State is WorkspaceState.Working or WorkspaceState.Paused;

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
    /// Pauses the AI's work in the workspace; a refusal is shown in the conversation.
    /// </summary>
    /// <returns>A task that completes once the AI is paused.</returns>
    public Task Pause() => Halt(connection.PauseWorkspace);

    /// <summary>
    /// Lets the paused AI work in the workspace carry on; a refusal is shown in the conversation.
    /// </summary>
    /// <returns>A task that completes once the AI works again.</returns>
    public Task Resume() => Halt(connection.ResumeWorkspace);

    /// <summary>
    /// Stops the work in the workspace and reverts what it did; a refusal is shown in the conversation.
    /// </summary>
    /// <returns>A task that completes once the workspace is ready again.</returns>
    public Task Cancel() => Halt(connection.CancelWorkspace);

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
            WorkspaceState.Paused => "Paused, resume or stop the work to carry on",
            WorkspaceState.Stopping => "Stopping, wait for it to finish",
            WorkspaceState.Canceling => "Cancelling and reverting, wait for it to finish",
            _ => null
        };
        Git.IsLocked = state is WorkspaceState.Working or WorkspaceState.Paused or WorkspaceState.Running or WorkspaceState.Stopping or WorkspaceState.Canceling;
        Raise(nameof(State));
        Raise(nameof(CanHalt));
        Raise(nameof(CanPause));
        Raise(nameof(CanResume));
        Raise(nameof(ShowHaltButtons));
        Raise(nameof(ShowPauseButton));
        Raise(nameof(ShowResumeButton));
        Raise(nameof(ShowCancelButton));
        Raise(nameof(IsWorking));
        Raise(nameof(CanCancel));
    }

    private void Resync()
    {
        _ = ReadState();
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
        if (remoteEvent.WorkspaceId == Workspace.Id && remoteEvent.Kind == RemoteEventKind.StateChanged && remoteEvent.State is { } state)
        {
            Follow(state);
        }
    }

    private void Follow(WorkspaceState state)
    {
        forced = state is WorkspaceState.Paused or WorkspaceState.Stopping or WorkspaceState.Canceling ? state : null;
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
                WorkspaceState.Paused => "Resume or stop the paused AI before running a script.",
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
