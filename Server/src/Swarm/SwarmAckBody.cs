namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The content of an acknowledgment.
/// </summary>
internal sealed record SwarmAckBody
{
    /// <summary>
    /// Gets the packages of the receiver the sender has handled.
    /// </summary>
    public required IReadOnlyList<Guid> Ids { get; init; }
}
