namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The screen that replaces the app while the client and its server run different versions, which can only be settled by updating or by choosing another server.
/// </summary>
internal sealed class VersionMismatchViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="VersionMismatchViewModel" /> class.
    /// </summary>
    /// <param name="info">The version of this client.</param>
    /// <param name="connection">The connection to the server, which says its version.</param>
    /// <param name="update">The update that can settle the difference.</param>
    public VersionMismatchViewModel(IAppInfo info, IServerConnection connection, UpdateViewModel update)
    {
        this.info = info;
        this.connection = connection;
        Update = update;
        ChangeServerCommand = new AsyncCommand(_ =>
        {
            ChangeServerRequested?.Invoke();
            return Task.CompletedTask;
        });
        update.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(UpdateViewModel.IsAvailable) or nameof(UpdateViewModel.CheckError))
            {
                Raise(nameof(Hint));
            }
        };
    }

    private readonly IAppInfo info;

    private readonly IServerConnection connection;

    /// <summary>
    /// Raised when the user chooses to change the server.
    /// </summary>
    public event Action? ChangeServerRequested;

    /// <summary>
    /// Gets the update that can settle the difference.
    /// </summary>
    public UpdateViewModel Update { get; }

    /// <summary>
    /// Gets the command that goes back to the screen for choosing a server.
    /// </summary>
    public AsyncCommand ChangeServerCommand { get; }

    /// <summary>
    /// Gets what is wrong, with both versions.
    /// </summary>
    public string Message => $"This client is version {info.Version} and the server is {(connection.ServerVersion is { } server ? $"version {server}" : "from before versions were checked")}. They only work together when they run the same version, so the client cannot be used with this server as it is.";

    /// <summary>
    /// Gets what the user can do about it.
    /// </summary>
    public string Hint => Update.IsAvailable ? Update.Summary : Update.HasCheckError ? $"The newest release could not be looked up: {Update.CheckError}" : "No newer release would make them match. Choose another server, or update the older one by hand.";
}
