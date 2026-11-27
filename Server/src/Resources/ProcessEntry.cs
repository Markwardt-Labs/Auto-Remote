namespace Markwardt.AutoRemote.Server;

/// <summary>
/// A process that is running on the computer.
/// </summary>
internal sealed record ProcessEntry
{
    /// <summary>
    /// Gets the process identifier.
    /// </summary>
    public required int Id { get; init; }

    /// <summary>
    /// Gets the identifier of the process's parent.
    /// </summary>
    public required int ParentId { get; init; }

    /// <summary>
    /// Gets when the process started, in clock ticks after the computer did, which tells it apart from a later process given the same identifier.
    /// </summary>
    public required long Started { get; init; }

    /// <summary>
    /// Gets the processor time the process has used, in clock ticks.
    /// </summary>
    public required long Ticks { get; init; }
}
