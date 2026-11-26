namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The content of a progress package.
/// </summary>
internal sealed record SwarmProgressBody
{
    /// <summary>
    /// Gets the tasks of the receiver the sender works on.
    /// </summary>
    public required IReadOnlyList<SwarmTaskProgress> Tasks { get; init; }
}
