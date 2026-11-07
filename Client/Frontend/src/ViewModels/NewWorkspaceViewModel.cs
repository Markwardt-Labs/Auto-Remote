namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The new workspace screen: pick or create the server folder that will host the workspace and choose the AI provider it runs on for good, with an optional API key for its session, and the group it is filed in.
/// </summary>
internal sealed class NewWorkspaceViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NewWorkspaceViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    public NewWorkspaceViewModel(IServerConnection connection)
    {
        this.connection = connection;
        Browser = new FolderBrowserViewModel(connection);
        Provider = new ProviderPickerViewModel(connection);
        Group = new GroupPickerViewModel();
        StartCommand = new AsyncCommand(_ => Start(), () => Browser.CanChoose);
        Browser.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FolderBrowserViewModel.CanChoose))
            {
                StartCommand.Refresh();
            }
        };
        CancelCommand = new AsyncCommand(_ =>
        {
            Cancelled?.Invoke();
            return Task.CompletedTask;
        });
    }

    private readonly IServerConnection connection;

    /// <summary>
    /// Raised with the workspace once it is created.
    /// </summary>
    public event Action<WorkspaceInfo>? Created;

    /// <summary>
    /// Raised when the user backs out.
    /// </summary>
    public event Action? Cancelled;

    /// <summary>
    /// Gets the folder browser.
    /// </summary>
    public FolderBrowserViewModel Browser { get; }

    /// <summary>
    /// Gets the choice of the provider the workspace runs on for good.
    /// </summary>
    public ProviderPickerViewModel Provider { get; }

    /// <summary>
    /// Gets the choice of the group the workspace is filed in.
    /// </summary>
    public GroupPickerViewModel Group { get; }

    /// <summary>
    /// Gets the command that creates the workspace in the browser's current folder.
    /// </summary>
    public AsyncCommand StartCommand { get; }

    /// <summary>
    /// Gets the command that backs out.
    /// </summary>
    public AsyncCommand CancelCommand { get; }

    /// <summary>
    /// Shows the folder browser at the server's default folder and selects the default provider.
    /// </summary>
    /// <returns>A task that completes once both are shown.</returns>
    public async Task Open()
    {
        await Provider.Load();
        await Group.Load(connection);
        await Browser.Start();
    }

    private async Task Start()
    {
        WorkspaceInfo? workspace = null;

        if (await Attempt(async () => workspace = await connection.CreateWorkspace(Browser.CurrentPath, Provider.Service, Provider.Key, Group.GroupId, CancellationToken.None)) && workspace is not null)
        {
            Created?.Invoke(workspace);
        }
    }
}
