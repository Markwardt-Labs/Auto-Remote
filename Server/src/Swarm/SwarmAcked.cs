namespace Markwardt.AutoRemote.Server;

/// <summary>
/// A package handled, acknowledged to its sender until it is old.
/// </summary>
internal sealed record SwarmAcked
{
    /// <summary>
    /// Gets the identifier.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets when it was handled.
    /// </summary>
    public required DateTimeOffset At { get; init; }
}
