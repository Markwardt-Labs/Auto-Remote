namespace Markwardt.AutoRemote;

/// <summary>
/// A paired server, with the settings of this server for it and what it last told about itself.
/// </summary>
public sealed record SwarmPeer
{
    /// <summary>
    /// Gets the identifier, derived from its identity keys.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets its name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the fingerprint of its identity keys. Read only.
    /// </summary>
    public string Fingerprint { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether this server may delegate tasks to it.
    /// </summary>
    public bool SendWork { get; init; }

    /// <summary>
    /// Gets a value indicating whether this server accepts the tasks it delegates, as long as the server accepts delegated work at all.
    /// </summary>
    public bool AcceptWork { get; init; }

    /// <summary>
    /// Gets how work and results travel between the two.
    /// </summary>
    public SwarmMode Mode { get; init; }

    /// <summary>
    /// Gets the address (<c>host:port</c>) this server connects to in the networked modes; empty when this server waits for the other one to connect.
    /// </summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>
    /// Gets what the AI of its tasks may do here.
    /// </summary>
    public SwarmPermissions Permissions { get; init; } = new();

    /// <summary>
    /// Gets the capabilities it offered in its last profile. Read only.
    /// </summary>
    public IReadOnlyList<SwarmCapability> Capabilities { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether its last profile said it accepts the tasks of this server. Read only.
    /// </summary>
    public bool AcceptsOurWork { get; init; }

    /// <summary>
    /// Gets how many more tasks it would take at once, as its last profile said. Read only.
    /// </summary>
    public int FreeSlots { get; init; }

    /// <summary>
    /// Gets when its last profile was made, which for an air gapped server may be long ago. Read only.
    /// </summary>
    public DateTimeOffset? ProfileAt { get; init; }

    /// <summary>
    /// Gets when anything last arrived from it. Read only.
    /// </summary>
    public DateTimeOffset? LastSeen { get; init; }

    /// <summary>
    /// Gets a value indicating whether a connection with it is open now. Read only.
    /// </summary>
    public bool IsConnected { get; init; }

    /// <summary>
    /// Gets why the last connection or delivery failed, or <see langword="null" />. Read only.
    /// </summary>
    public string? Problem { get; init; }
}
