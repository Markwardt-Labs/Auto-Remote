namespace Markwardt.AutoRemote;

/// <summary>
/// The first line a client sends after connecting, proving it knows the server's access key.
/// </summary>
public sealed record HandshakeRequest
{
    /// <summary>
    /// Gets the access key the client was configured with.
    /// </summary>
    public required string AccessKey { get; init; }
}

/// <summary>
/// The server's answer to a <see cref="HandshakeRequest" />; the server closes the connection after a rejection.
/// </summary>
public sealed record HandshakeResponse
{
    /// <summary>
    /// Gets a value indicating whether the access key was correct and requests may follow.
    /// </summary>
    public required bool Accepted { get; init; }
}
