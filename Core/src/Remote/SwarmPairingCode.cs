namespace Markwardt.AutoRemote;

/// <summary>
/// A code another server pairs with over the network, once, before it expires.
/// </summary>
public sealed record SwarmPairingCode
{
    /// <summary>
    /// Gets the code.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Gets when it expires.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}
