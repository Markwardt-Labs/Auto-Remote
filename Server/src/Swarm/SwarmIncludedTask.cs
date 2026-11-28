namespace Markwardt.AutoRemote.Server;

/// <summary>
/// A task whose changes the result of another task was meant to include.
/// </summary>
internal sealed record SwarmIncludedTask
{
    /// <summary>
    /// Gets the identifier.
    /// </summary>
    public required Guid TaskId { get; init; }

    /// <summary>
    /// Gets the title.
    /// </summary>
    public required string Title { get; init; }
}
