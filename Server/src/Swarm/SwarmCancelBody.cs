namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The content of a cancellation.
/// </summary>
internal sealed record SwarmCancelBody
{
    /// <summary>
    /// Gets the identifier of the cancelled task.
    /// </summary>
    public required Guid TaskId { get; init; }
}
