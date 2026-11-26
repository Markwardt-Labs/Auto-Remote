namespace Markwardt.AutoRemote.Server;

/// <summary>
/// What is stored about a paired server.
/// </summary>
internal sealed class SwarmPeerRecord
{
    /// <summary>
    /// Gets its public keys.
    /// </summary>
    public required SwarmPublicIdentity Identity { get; init; }

    /// <summary>
    /// Gets or sets its name.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Gets or sets whether tasks may be delegated to it.
    /// </summary>
    public bool SendWork { get; set; }

    /// <summary>
    /// Gets or sets whether its tasks are accepted.
    /// </summary>
    public bool AcceptWork { get; set; }

    /// <summary>
    /// Gets or sets the delivery mode.
    /// </summary>
    public SwarmMode Mode { get; set; }

    /// <summary>
    /// Gets or sets the address to connect to.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what its tasks may do here.
    /// </summary>
    public SwarmPermissions Permissions { get; set; } = new();

    /// <summary>
    /// Gets or sets its last profile.
    /// </summary>
    public SwarmProfileBody? Profile { get; set; }

    /// <summary>
    /// Gets or sets when its last profile was made.
    /// </summary>
    public DateTimeOffset? ProfileAt { get; set; }

    /// <summary>
    /// Gets or sets when anything last arrived from it.
    /// </summary>
    public DateTimeOffset? LastSeen { get; set; }

    /// <summary>
    /// Gets or sets when the two paired.
    /// </summary>
    public DateTimeOffset? PairedAt { get; set; }

    /// <summary>
    /// Gets or sets the commits it is known to have, per repository, which later bundles leave out.
    /// </summary>
    public Dictionary<string, List<string>> KnownCommits { get; set; } = [];

    /// <summary>
    /// Gets or sets the packages of it that were handled.
    /// </summary>
    public List<SwarmAcked> Acks { get; set; } = [];

    /// <summary>
    /// Gets or sets why the last connection or delivery failed.
    /// </summary>
    public string? Problem { get; set; }
}
