namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The new workspace screen: pick or create the server folder that will host the workspace.
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
    /// Gets the command that creates the workspace in the browser's current folder.
    /// </summary>
    public AsyncCommand StartCommand { get; }

    /// <summary>
    /// Gets the command that backs out.
    /// </summary>
    public AsyncCommand CancelCommand { get; }

    /// <summary>
    /// Shows the folder browser at the server's default folder.
    /// </summary>
    /// <returns>A task that completes once the folder is shown.</returns>
    public Task Open() => Browser.Start();

    private async Task Start()
    {
        WorkspaceInfo? workspace = null;

        if (await Attempt(async () => workspace = await connection.CreateWorkspace(Browser.CurrentPath, CancellationToken.None)) && workspace is not null)
        {
            Created?.Invoke(workspace);
        }
    }
}
