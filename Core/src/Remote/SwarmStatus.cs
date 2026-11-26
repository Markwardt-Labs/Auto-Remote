namespace Markwardt.AutoRemote;

/// <summary>
/// Everything the swarm screen shows.
/// </summary>
public sealed record SwarmStatus
{
    /// <summary>
    /// Gets the settings of this server.
    /// </summary>
    public required SwarmServer Server { get; init; }

    /// <summary>
    /// Gets the paired servers.
    /// </summary>
    public IReadOnlyList<SwarmPeer> Peers { get; init; } = [];

    /// <summary>
    /// Gets the pairings found on the drive that wait to be confirmed.
    /// </summary>
    public IReadOnlyList<SwarmDrivePairing> DrivePairings { get; init; } = [];

    /// <summary>
    /// Gets the pairing code that works now, or <see langword="null" />.
    /// </summary>
    public SwarmPairingCode? PairingCode { get; init; }

    /// <summary>
    /// Gets the delegated tasks, the newest first.
    /// </summary>
    public IReadOnlyList<SwarmTaskInfo> Tasks { get; init; } = [];

    /// <summary>
    /// Gets what the last action that has something to tell did, such as syncing the drive.
    /// </summary>
    public string? Report { get; init; }
}
