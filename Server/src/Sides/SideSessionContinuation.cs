namespace Markwardt.AutoRemote.Server;

/// <summary>
/// What a side session that goes on with the result of a delegated task starts from.
/// </summary>
internal sealed record SideSessionContinuation
{
    /// <summary>
    /// Gets the commit the task started from, which what the session changed is measured against.
    /// </summary>
    public required string BaseCommit { get; init; }

    /// <summary>
    /// Gets the commit holding the files as the task left them.
    /// </summary>
    public required string ResultCommit { get; init; }

    /// <summary>
    /// Gets the report of the task, which the chat of the session starts with.
    /// </summary>
    public required string Report { get; init; }
}
