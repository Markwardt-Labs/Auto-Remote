namespace Markwardt.AutoRemote;

/// <summary>
/// A pairing found on the drive that waits for a person to compare the fingerprint and confirm it.
/// </summary>
public sealed record SwarmDrivePairing
{
    /// <summary>
    /// Gets the identifier of the other server, which <see cref="RemoteCommand.ConfirmDrivePairing" /> takes.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the name of the other server.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the fingerprint of the other server, to compare with the one it shows.
    /// </summary>
    public required string Fingerprint { get; init; }

    /// <summary>
    /// Gets what is to be confirmed.
    /// </summary>
    public required SwarmPairingKind Kind { get; init; }
}
