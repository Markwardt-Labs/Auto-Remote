namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Where a new session can run: on this server, on a paired one or on any of them.
/// </summary>
internal sealed record RunTarget
{
    /// <summary>
    /// Gets the name shown for it.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets a value indicating whether it is this server, where the new session is a side session.
    /// </summary>
    public bool IsLocal { get; init; }

    /// <summary>
    /// Gets the paired server, or <see langword="null" /> for this server and for any paired one.
    /// </summary>
    public string? PeerId { get; init; }
}
