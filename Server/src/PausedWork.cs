namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The AI work of a paused workspace, kept on disk so the pause survives the server restarting: a frozen process cannot, but what is needed to carry on or to revert the work can.
/// </summary>
internal sealed record PausedWork
{
    /// <summary>
    /// Gets the paused workspace.
    /// </summary>
    public required Guid WorkspaceId { get; init; }

    /// <summary>
    /// Gets what cancelling the work puts back, or <see langword="null" /> when the workspace is not a git repository.
    /// </summary>
    public WorkspaceSnapshot? Snapshot { get; init; }

    /// <summary>
    /// Gets the prompts the AI had not answered yet, which are given to it again when it has to start over without its session.
    /// </summary>
    public IReadOnlyList<string> Prompts { get; init; } = [];
}
