namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The errors the AI of one workspace ended with in the last 30 days, as the server recorded them, for the error panel of the workspace's settings; it only ever shows errors of its own workspace.
/// </summary>
/// <param name="connection">The server connection.</param>
/// <param name="workspaceId">The workspace whose errors are shown.</param>
internal sealed class ErrorHistoryViewModel(IServerConnection connection, Guid workspaceId) : ViewModelBase
{
    /// <summary>
    /// Gets the errors, the newest first.
    /// </summary>
    public ObservableCollection<ErrorEntry> Entries { get; } = [];

    /// <summary>
    /// Gets a value indicating whether the workspace has no error to show.
    /// </summary>
    public bool HasNoErrors => Entries.Count == 0;

    /// <summary>
    /// Reads the errors from the server, which also drops the ones that are too old.
    /// </summary>
    /// <returns>A task that completes once the list is shown or the failure to read it is.</returns>
    public async Task Load()
    {
        IReadOnlyList<ErrorEntry>? errors = null;

        if (await Attempt(async () => errors = await connection.GetErrors(workspaceId, CancellationToken.None)))
        {
            Show(errors!);
        }
    }

    /// <summary>
    /// Follows an event of the server: the history that came with an error of this workspace replaces the list; everything else, including the errors of other workspaces, is ignored.
    /// </summary>
    /// <param name="remoteEvent">The event.</param>
    public void Receive(RemoteEvent remoteEvent)
    {
        if (remoteEvent.WorkspaceId == workspaceId && remoteEvent.Kind == RemoteEventKind.ErrorsChanged && remoteEvent.Errors is { } errors)
        {
            Show(errors);
        }
    }

    private void Show(IReadOnlyList<ErrorEntry> errors)
    {
        Entries.Clear();

        foreach (ErrorEntry entry in errors)
        {
            Entries.Add(entry);
        }

        Raise(nameof(HasNoErrors));
    }
}
