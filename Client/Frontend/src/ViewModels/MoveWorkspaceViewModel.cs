namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The move workspace screen: pick the existing server folder the workspace works in from now on. The AI session is told, no file is moved, so the folder is one the project was already moved to.
/// </summary>
internal sealed class MoveWorkspaceViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MoveWorkspaceViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    /// <param name="workspace">The workspace that is moved.</param>
    public MoveWorkspaceViewModel(IServerConnection connection, WorkspaceInfo workspace)
    {
        this.connection = connection;
        Workspace = workspace;
        Browser = new FolderBrowserViewModel(connection);
        MoveCommand = new AsyncCommand(_ => Move(), () => Browser.CanChoose);
        Browser.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FolderBrowserViewModel.CanChoose))
            {
                MoveCommand.Refresh();
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
    /// Raised with the workspace once it is moved.
    /// </summary>
    public event Action<WorkspaceInfo>? Moved;

    /// <summary>
    /// Raised when the user backs out.
    /// </summary>
    public event Action? Cancelled;

    /// <summary>
    /// Gets the workspace that is moved.
    /// </summary>
    public WorkspaceInfo Workspace { get; }

    /// <summary>
    /// Gets the text that says what the screen does.
    /// </summary>
    public string Description => $"'{Workspace.Name}'";

    /// <summary>
    /// Gets the folder browser.
    /// </summary>
    public FolderBrowserViewModel Browser { get; }

    /// <summary>
    /// Gets the command that moves the workspace to the browser's current folder.
    /// </summary>
    public AsyncCommand MoveCommand { get; }

    /// <summary>
    /// Gets the command that backs out.
    /// </summary>
    public AsyncCommand CancelCommand { get; }

    /// <summary>
    /// Shows the folder the workspace's folder is in now, or the server's default folder when that is gone too.
    /// </summary>
    /// <returns>A task that completes once the folder is shown.</returns>
    public async Task Open()
    {
        await Browser.Start(Path.GetDirectoryName(Workspace.Folder.TrimEnd('/', '\\')) is { Length: > 0 } parent ? parent : null);

        if (Browser.HasError)
        {
            await Browser.Start();
        }
    }

    private async Task Move()
    {
        WorkspaceInfo? moved = null;

        if (await Attempt(async () => moved = await connection.MoveWorkspaceToFolder(Workspace.Id, Browser.CurrentPath, CancellationToken.None)) && moved is not null)
        {
            Moved?.Invoke(moved);
        }
    }
}
