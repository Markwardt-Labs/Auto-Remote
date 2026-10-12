namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The clone workspace screen: enter the repository URL and pick the server folder the clone's folder is created in.
/// </summary>
internal sealed class CloneWorkspaceViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CloneWorkspaceViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    public CloneWorkspaceViewModel(IServerConnection connection)
    {
        this.connection = connection;
        Browser = new FolderBrowserViewModel(connection);
        CloneCommand = new AsyncCommand(_ => Clone(), () => Browser.CanChoose && !string.IsNullOrWhiteSpace(url));
        Browser.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FolderBrowserViewModel.CanChoose))
            {
                CloneCommand.Refresh();
            }
        };
        CancelCommand = new AsyncCommand(_ =>
        {
            Cancelled?.Invoke();
            return Task.CompletedTask;
        });
    }

    private readonly IServerConnection connection;

    private string url = string.Empty;

    private string name = string.Empty;

    /// <summary>
    /// Raised with the workspace once it is cloned and created.
    /// </summary>
    public event Action<WorkspaceInfo>? Created;

    /// <summary>
    /// Raised when the user backs out.
    /// </summary>
    public event Action? Cancelled;

    /// <summary>
    /// Gets the folder browser that picks where the clone's folder is created.
    /// </summary>
    public FolderBrowserViewModel Browser { get; }

    /// <summary>
    /// Gets or sets the URL of the repository to clone.
    /// </summary>
    public string Url
    {
        get => url;
        set
        {
            if (Set(ref url, value))
            {
                CloneCommand.Refresh();
            }
        }
    }

    /// <summary>
    /// Gets or sets the name of the new folder; empty to name it after the repository.
    /// </summary>
    public string Name
    {
        get => name;
        set => Set(ref name, value);
    }

    /// <summary>
    /// Gets the command that clones the repository into the browser's current folder.
    /// </summary>
    public AsyncCommand CloneCommand { get; }

    /// <summary>
    /// Gets the command that backs out.
    /// </summary>
    public AsyncCommand CancelCommand { get; }

    /// <summary>
    /// Shows the folder browser at the server's default folder.
    /// </summary>
    /// <returns>A task that completes once the folder is shown.</returns>
    public Task Open() => Browser.Start();

    private async Task Clone()
    {
        WorkspaceInfo? workspace = null;

        if (await Attempt(async () => workspace = await connection.CloneWorkspace(url.Trim(), Browser.CurrentPath, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), CancellationToken.None)) && workspace is not null)
        {
            Created?.Invoke(workspace);
        }
    }
}
