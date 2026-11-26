namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The readable part of a sealed package, which the signature and the encryption both cover.
/// </summary>
internal sealed record SwarmEnvelopeHeader
{
    /// <summary>
    /// Gets the identifier of the package.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets what it is.
    /// </summary>
    public required SwarmMessageKind Kind { get; init; }

    /// <summary>
    /// Gets the identifier of the sender.
    /// </summary>
    public required string From { get; init; }

    /// <summary>
    /// Gets the identifier of the receiver.
    /// </summary>
    public required string To { get; init; }

    /// <summary>
    /// Gets when it was made.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Gets the random salt the key of this package is derived with.
    /// </summary>
    public required string Salt { get; init; }

    /// <summary>
    /// Gets the nonce of the encryption.
    /// </summary>
    public required string Nonce { get; init; }
}
