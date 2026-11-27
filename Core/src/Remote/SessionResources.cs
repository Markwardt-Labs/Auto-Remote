namespace Markwardt.AutoRemote;

/// <summary>
/// What one session of a workspace takes.
/// </summary>
public sealed record SessionResources
{
    /// <summary>
    /// Gets the session, which is the workspace itself for the main session.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets the name of the session, <c>Main</c> for the main session.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets whether the session is working or runs a script.
    /// </summary>
    public bool IsWorking { get; init; }

    /// <summary>
    /// Gets what the processes of the session take.
    /// </summary>
    public required ResourceUsage Usage { get; init; }
}
