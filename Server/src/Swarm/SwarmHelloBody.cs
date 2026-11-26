namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The content of the package that opens a network session.
/// </summary>
internal sealed record SwarmHelloBody
{
    /// <summary>
    /// Gets a random value, so the package is not taken twice.
    /// </summary>
    public required string Nonce { get; init; }
}
