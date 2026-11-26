namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Everything the swarm stores.
/// </summary>
internal sealed class SwarmDocument
{
    /// <summary>
    /// Gets or sets the settings of this server.
    /// </summary>
    public SwarmServer Server { get; set; } = new();

    /// <summary>
    /// Gets or sets the paired servers.
    /// </summary>
    public List<SwarmPeerRecord> Peers { get; set; } = [];

    /// <summary>
    /// Gets or sets the jobs delegated from here.
    /// </summary>
    public List<SwarmJob> Jobs { get; set; } = [];

    /// <summary>
    /// Gets or sets the tasks delegated from here.
    /// </summary>
    public List<SwarmOutgoing> Outgoing { get; set; } = [];

    /// <summary>
    /// Gets or sets the tasks delegated here.
    /// </summary>
    public List<SwarmIncoming> Incoming { get; set; } = [];

    /// <summary>
    /// Gets or sets the packages handled, so one arriving twice does nothing.
    /// </summary>
    public Dictionary<Guid, DateTimeOffset> Seen { get; set; } = [];

    /// <summary>
    /// Gets or sets when this server last wrote an invitation to a drive, from which on an acceptance of it can be confirmed.
    /// </summary>
    public DateTimeOffset? InvitedAt { get; set; }
}
