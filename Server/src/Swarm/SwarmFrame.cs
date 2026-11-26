namespace Markwardt.AutoRemote.Server;

/// <summary>
/// One line of a network connection between paired servers.
/// </summary>
internal sealed record SwarmFrame
{
    /// <summary>
    /// Gets what it is: <c>pair</c>, <c>paired</c>, <c>hello</c>, <c>part</c>, <c>end</c> or <c>error</c>.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Gets the base64 bytes of a hello, or of one part of a package.
    /// </summary>
    public string? Data { get; init; }

    /// <summary>
    /// Gets which part of a package this is, from 0.
    /// </summary>
    public int Index { get; init; }

    /// <summary>
    /// Gets how many parts the package has.
    /// </summary>
    public int Count { get; init; }

    /// <summary>
    /// Gets the name of the server pairing.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the public keys of the server pairing.
    /// </summary>
    public SwarmPublicIdentity? Identity { get; init; }

    /// <summary>
    /// Gets a random value of the pairing, which both proofs cover.
    /// </summary>
    public string? Nonce { get; init; }

    /// <summary>
    /// Gets the proof of knowing the pairing code.
    /// </summary>
    public string? Proof { get; init; }

    /// <summary>
    /// Gets why the other side refused.
    /// </summary>
    public string? Error { get; init; }
}
