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
    public WorkspaceViewModel(IServerConnection connection, WorkspaceInfo workspace, ClientLayout layout = ClientLayout.Tabs)
    {
        this.layout = layout;
        Workspace = workspace;
        Chat = new ChatViewModel(connection, workspace.Id);
        Git = new GitViewModel(connection, workspace.Id);
        Files = new FilesViewModel(connection, workspace.Id, Git);
        Settings = new SettingsViewModel(connection, workspace);
        Chat.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatViewModel.IsAiWorking))
            {
                Raise(nameof(IsAiWorking));

                if (Chat.IsAiWorking && Git.IsWorking && tab is WorkspaceTab.Files or WorkspaceTab.History)
                {
                    Tab = WorkspaceTab.Chat;
                }
            }
        };
        Files.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FilesViewModel.IsPreviewing))
            {
                Raise(nameof(ShowContentPanel));
            }
        };
        Git.PropertyChanged += (_, args) =>
        {
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

    private WorkspaceTab tab;

    private WorkspaceTab beforeSettings;

    /// <summary>
    /// Gets the workspace as it was when it was focused.
    /// </summary>
    public WorkspaceInfo Workspace { get; }

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
    /// Gets the workspace's settings.
    /// </summary>
    public SettingsViewModel Settings { get; }

    /// <summary>
    /// Gets a value indicating whether the AI is working in the workspace.
    /// </summary>
    public bool IsAiWorking => Chat.IsAiWorking;

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
    /// Gets a value indicating whether the panel of the conversation and the history is showing.
    /// </summary>
    public bool ShowChatPanel => !ShowSettings && (layout == ClientLayout.Panels || ShowChat || ShowHistory);

    /// <summary>
    /// Gets a value indicating whether the conversation is showing in its panel.
    /// </summary>
    public bool ShowChatView => layout == ClientLayout.Panels ? tab != WorkspaceTab.History : ShowChat;

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
    /// Gets a value indicating whether the panel of the conversation has tabs for the conversation and the history, which only panels do; tabs along the bottom hold them otherwise.
    /// </summary>
    public bool ShowSideTabs => layout == ClientLayout.Panels && Git.IsRepo;

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
    /// Gets a value indicating whether the settings are showing.
    /// </summary>
    public bool ShowSettings => tab == WorkspaceTab.Settings;

    /// <summary>
    /// Gets the command that shows the conversation.
    /// </summary>
    public AsyncCommand ShowChatCommand { get; }

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
        _ = Git.Start();
        await Chat.Start();
    }

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
        Chat.Dispose();
        Files.Dispose();
        Git.Dispose();
    }

    private void RaiseLayout()
    {
        Raise(nameof(ShowFilesPanel));
        Raise(nameof(ShowChatPanel));
        Raise(nameof(ShowChatView));
        Raise(nameof(ShowHistoryView));
        Raise(nameof(ShowContentPanel));
        Raise(nameof(ShowSeparators));
    }
}
