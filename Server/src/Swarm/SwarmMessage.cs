namespace Markwardt.AutoRemote.Server;

/// <summary>
/// An opened package.
/// </summary>
internal sealed record SwarmMessage
{
    /// <summary>
    /// Gets the identifier, by which a package handled before is recognized.
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
    /// Gets its content as JSON.
    /// </summary>
    public required ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>
    /// Gets the binary data it carries, such as a git bundle.
    /// </summary>
    public ReadOnlyMemory<byte> Blob { get; init; }
}
