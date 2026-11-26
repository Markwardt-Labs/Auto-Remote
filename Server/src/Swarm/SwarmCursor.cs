namespace Markwardt.AutoRemote.Server;

/// <summary>
/// What one network connection has sent, so what has not changed is not sent again.
/// </summary>
internal sealed class SwarmCursor
{
    /// <summary>
    /// Gets what was told about each task.
    /// </summary>
    public Dictionary<Guid, (SwarmTaskState State, int Messages)> Progress { get; } = [];

    /// <summary>
    /// Gets when each package that waits for its acknowledgment was last sent over this connection.
    /// </summary>
    public Dictionary<string, DateTimeOffset> Sent { get; } = [];

    /// <summary>
    /// Gets the packages acknowledged over this connection.
    /// </summary>
    public HashSet<Guid> Acknowledged { get; } = [];

    /// <summary>
    /// Gets or sets the profile last sent.
    /// </summary>
    public string? Profile { get; set; }

    /// <summary>
    /// Gets or sets when the profile was last sent.
    /// </summary>
    public DateTimeOffset ProfileAt { get; set; }
}
